// <copyright file="FirelyNetIG.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.ComponentModel;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Extensions;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.Packaging;
using Ncqa.Cql.Model;
using Fhir.CodeGen.Common.Utils;
using static Fhir.CodeGen.Lib.Language.Firely.CSharpFirelyCommon;
using static Fhir.CodeGen.Common.Extensions.FhirNameConventionExtensions;
using static Hl7.Fhir.Model.CodeSystem;
using Hl7.Fhir.Specification;
using Hl7.Fhir.Specification.Navigation;
using Hl7.FhirPath;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.Metrics;
using static Fhir.CodeGen.Lib.Language.Firely.CSharpFirely2;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Lib.Packaging;

#if NETSTANDARD2_0
using Fhir.CodeGen.Common.Polyfill;
#endif

namespace Fhir.CodeGen.Lib.Language.Firely;

public partial class FirelyNetIG : ILanguage
{
    /// <summary>(Immutable) Name of the language.</summary>
    private const string LanguageName = "FirelyNetIg";

    /// <summary>Gets the language name.</summary>
    public string Name => LanguageName;

    /// <summary>Gets the FHIR primitive type map.</summary>
    public Dictionary<string, string> FhirPrimitiveTypeMap => CSharpFirelyCommon.PrimitiveTypeMap;

    /// <summary>Gets a value indicating whether this language is idempotent.</summary>
    public bool IsIdempotent => true;

    /// <summary>The definition writers.</summary>
    private Dictionary<string, ExportStreamWriter> _definitionWriters = [];

    /// <summary>The extension writers.</summary>
    private Dictionary<string, ExportStreamWriter> _extensionWriters = [];

    /// <summary>The value set writers.</summary>
    private Dictionary<string, ExportStreamWriter> _valueSetWriters = [];

    /// <summary>The namespace to use during export.</summary>
    private const string _firelyNamespace = "Hl7.Fhir.Model";

    private const string _classNameDefinitions = "CanonicalUrls";
    private const string _classNameExtensions = "ExtensionAccessors";
    private const string _classNameValueSets = "ValueSetUtils";
    private const string _namespaceSuffixProfiles = ".Profiles";

    private const string _extUrlPrefix = "Extension";
    private const string _profileUrlPrefix = "Profile";

    private const string _processingValuePrefix = "val";
    private const string _processingExtPrefix = "ext";
    private const string _processingExtAddPrefix = "addExt";
    private const string _processingArraySuffix = "Rec";
    private const string _recordClassSuffix = "Record";
    private const string _recordValueSuffix = "";

    private const string _extGetterPrefixScalar = "Get an Extension representing the ";
    private const string _extGetterPrefixArray = "Get any Extensions representing the ";

    private const string _extValueGetterPrefixScalar = "Get the ";
    private const string _extValueGetterPrefixArray = "Get all of the ";

    private const string _extSetterPrefixScalar = "Set an Extension representing the ";
    private const string _extSetterPrefixArray = "Set any Extensions representing the ";

    private const string _extValueSetterPrefixScalar = "Set the ";
    private const string _extValueSetterPrefixArray = "Set all of the ";

    private Dictionary<string, CSharpFirely2.WrittenValueSetInfo> _valueSetInfoByUrl = [];
    private Dictionary<string, string> _valueSetNameMaps = [];

    /// <summary>The package data by directive.</summary>
    private Dictionary<string, PackageData> _packageDataByDirective = [];
    private Dictionary<string, HashSet<string>> _extensionNamesByPackageDirective = [];

    private Dictionary<string, CanonicalInfo> _canonicalsByUrl = [];
    private Dictionary<string, string> _firelyToFhirPaths = [];

    private Dictionary<string, StructureDefinitionWalker> _walkers = [];

    private FhirPathCompiler _fpCompiler = new(new Hl7.FhirPath.Expressions.SymbolTable());

#if NET8_0_OR_GREATER
    [GeneratedRegex(".+(\\.extension(\\:[^.]+)?(\\.url)?)")]
    private static partial Regex FindExtensionPathRegex();
    private static readonly Regex _findExtensionPathRegex = FindExtensionPathRegex();
#else
    private static readonly Regex _findExtensionPathRegex = new Regex(".+(\\.extension(\\:[^.]+)?(\\.url)?)", RegexOptions.Compiled);
#endif

    private static readonly HashSet<string> _nameComponentsToRemove = new(StringComparer.OrdinalIgnoreCase)
    {
        "extensions",
        "ext",
        "www",
        "org",
        "com",
    };

    private static readonly HashSet<string> _exclusionSet = new()
    {
        /*
         * Extension definition is incorrect and not worth working around.
         * See https://jira.hl7.org/browse/FHIR-46629
         * TODO: remove from exclusion set when corrected.
         */
        "http://hl7.org/fhir/StructureDefinition/implementationguide-sourceFile",
    };

    private record class CanonicalInfo
    {
        public required string Url { get; init; }
        public required string Name { get; init; }
        public required IConformanceResource Conformance { get; init; }
        public required PackageData Package { get; init; }
    }

    private record struct PackageData
    {
        public required string Key { get; set; }
        public required string PackageId { get; init; }
        public required string PackageVersion { get; init; }
        public required string Namespace { get; init; }
        public required string ClassPrefix { get; init; }
        public required string VersionSanitized { get; init; }
        public required string FolderName { get; init; }
    }

    /// <summary>URL of the extension data by.</summary>
    private Dictionary<string, ExtensionData> _extensionDataByUrl = [];

    private record class ExtensionData()
    {
        public required ComponentDefinition Component { get; init; }
        public required string Name { get; init; }
        public required string Url { get; init; }
        public required string Summary { get; init; }
        public required string Remarks { get; init; }
        public required bool IsList { get; init; }
        public required string ParentName { get; init; }
        public required string DefinitionDirective { get; init; }
        public required string PackageNamespace { get; init; }
        public required ExtensionContextRec[] Contexts { get; init; }
        public required ElementDefinition? ValueElement { get; init; }
        public required CSharpFirely2.WrittenElementInfo? ElementInfo { get; init; }
        public required ExtensionData[] Children { get; init; }
        public required ExternalExtensionReferenceData[] ExternalChildren { get; init; }
        public required string ValueTypeName { get; init; }
    }

    private record class ExternalExtensionReferenceData
    {
        public required string Name { get; init; }
        public required string Url { get; init; }
        public required string Summary { get; init; }
        public required string Remarks { get; init; }
        public required bool IsRequired { get; init; }
        public required bool IsList { get; init; }
        public required string ParentName { get; init; }
        public required string ValueTypeName { get; init; }
    }

    private record class ExtensionContextRec
    {
        public required StructureDefinition.ContextComponent AllowedContext { get; init; }

        public required ComponentDefinition? ContextTarget { get; init; }

        public required CSharpFirely2.WrittenElementInfo? ContextElementInfo { get; init; }
        public required string ContextTypeName { get; init; }
    }


    /// <summary>FHIR information we are exporting.</summary>
    private DefinitionCollection _info = null!;

    /// <summary>Options for controlling the export.</summary>
    private FirelyNetIGOptions _options = null!;

    public enum ExtensionAccessorExportCodes
    {
        [Description("Do not export export extension accessor functions.")]
        None,

        //[Description("Export extension accessor functions based on Extension objects.")]
        //ExtensionAccessors,

        [Description("Export extension accessor functions that use record classes for values.")]
        RecordAccessors,
    }

    public Type ConfigType => typeof(FirelyNetIGOptions);
    public class FirelyNetIGOptions : ConfigGenerate
    {
        [ConfigOption(
            ArgName = "--extension-accessors",
            Description = "Style to export extension accessors with.")]
        public ExtensionAccessorExportCodes ExtensionAccessorExport { get; set; } = ExtensionAccessorExportCodes.RecordAccessors;

        private static ConfigurationOption ExtensionAccessorExportParameter { get; } = new()
        {
            Name = "ExtensionAccessorExport",
            DefaultValue = ExtensionAccessorExportCodes.RecordAccessors,
            CliOption = new System.CommandLine.Option<ExtensionAccessorExportCodes>("--extension-accessors")
            {
                Description = "Style to export extension accessors with.",
                Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
                Required = false,
            },
        };

        private static readonly ConfigurationOption[] _options =
        [
            ExtensionAccessorExportParameter,
        ];

        public override ConfigurationOption[] GetOptions()
        {
            return [.. base.GetOptions(), .. _options];
        }

        public override void Parse(System.CommandLine.ParseResult parseResult)
        {
            // parse base properties
            base.Parse(parseResult);

            // iterate over options for ones we are interested in
            foreach (ConfigurationOption opt in _options)
            {
                switch (opt.Name)
                {
                    case "ExtensionAccessorExport":
                        ExtensionAccessorExport = GetOpt(parseResult, opt, ExtensionAccessorExport);
                        break;
                }
            }
        }


        //private const string DefaultNamespace = "Hl7.Fhir.{realm}.{package}-{version}";

        ///// <summary>Gets or sets the namespace.</summary>
        //[ConfigOption(
        //    ArgName = "--namespace",
        //    Description = $"Base namespace prefix for exported classes, default is '{DefaultNamespace}', use '' (empty string) for none.")]
        //public string Namespace { get; set; } = DefaultNamespace;

        //private static ConfigurationOption NamespaceParameter { get; } = new()
        //{
        //    Name = "Namespace",
        //    DefaultValue = DefaultNamespace,
        //    CliOption = new System.CommandLine.Option<string>("--namespace", $"Base namespace for exported classes, default is '{DefaultNamespace}', use '' (empty string) for none.")
        //    {
        //        Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
        //        IsRequired = false,
        //    },
        //};
    }

    /// <summary>Opens the scope.</summary>
    private void OpenScope(ExportStreamWriter writer)
        => CSharpFirelyCommon.OpenScope(writer);

    /// <summary>Closes the scope.</summary>
    private void CloseScope(ExportStreamWriter writer, bool includeSemicolon = false, bool suppressNewline = false)
        => CSharpFirelyCommon.CloseScope(writer, includeSemicolon, suppressNewline);

    /// <summary>Writes an indented comment.</summary>
    /// <param name="value">    The value.</param>
    /// <param name="isSummary">(Optional) True if is summary, false if not.</param>
    /// <param name="singleLine"></param>
    private void WriteIndentedComment(ExportStreamWriter writer, string value, bool isSummary = true, bool singleLine = false, bool isRemarks = false)
        => writer.WriteIndentedComment(value, isSummary, singleLine, isRemarks);

    public void Export(object untypedOptions, DefinitionCollection info)
    {
        if (untypedOptions is not FirelyNetIGOptions options)
        {
            throw new ArgumentException("Options must be of type FirelyNetIGOptions");
        }

        // check to see if there are multiple versions of FHIR loaded - IG generation can only work with a single one
        PackageIdentity[] fhirCorePackageIds = info.Manifests.Keys
            .Where(key => FhirPackageUtils.PackageIsFhirRelease(key.Id))
            .ToArray();
        int fhirCoreVersionCount = fhirCorePackageIds
            .Select(d => info.Manifests[d].Version.ToString())
            .Distinct()
            .Count();
        if (fhirCoreVersionCount > 1)
        {
            throw new Exception("Multiple versions of FHIR are loaded, IG generation can only work with a single version. Either specify a package with a single version or use the --fhir-version parameter to filter.");
        }

        // set internal vars so we don't pass them to every function
        _info = info;
        _options = options;

        _firelyToFhirPaths.Clear();
        _canonicalsByUrl.Clear();

        if (!Directory.Exists(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
        }

        // need to process structures so that we have a type index of valid types
        BuildCoreTypeMap();

        // need to process ValueSets so that we know which ones have enums
        ProcessAndWriteValueSets();

        IEnumerator<IConformanceResource> conformanceEnumerator = _info.CanonicalEnumerator;

        Dictionary<string, CanonicalInfo> canonicalsByName = [];

        // traverse canonicals building names for them
        while (conformanceEnumerator.MoveNext())
        {
            IConformanceResource cr = conformanceEnumerator.Current;

            // skip anything that is not a domain resource - should never happen
            if (cr is not DomainResource r)
            {
                continue;
            }

            if (!_info.TryGetPackageSource(r, out string packageId, out string packageVersion))
            {
                packageId = _info.MainPackageId;
                packageVersion = _info.MainPackageVersion;
            }

            PackageData packageData = GetPackageData(packageId, packageVersion);

            string name = GetCanonicalNameLiteral(cr);

            // check for name collisions - typically from FHIR + THO URLs defining the same artifact
            if (canonicalsByName.TryGetValue(packageData.Key + name, out CanonicalInfo? existing))
            {
                if (existing.Url == cr.Url)
                {
                    // ignore duplicates
                    continue;
                }

                // check to see if existing is THO and current is FHIR
                if (existing.Url.StartsWith(CommonDefinitions.THOUrlPrefix) &&
                    cr.Url.StartsWith(CommonDefinitions.FhirUrlPrefix))
                {
                    canonicalsByName.Remove(packageData.Key + existing.Name);
                    existing = existing with { Name = existing.Name + "THO" };
                    canonicalsByName.Add(packageData.Key + existing.Name, existing);
                    _canonicalsByUrl[existing.Url] = existing;

                    name = name + "FHIR";
                }
                // check to see if existing is FHIR and current is THO
                else if (existing.Url.StartsWith(CommonDefinitions.FhirUrlPrefix) &&
                         cr.Url.StartsWith(CommonDefinitions.THOUrlPrefix))
                {
                    canonicalsByName.Remove(packageData.Key + existing.Name);
                    existing = existing with { Name = existing.Name + "FHIR" };
                    canonicalsByName.Add(packageData.Key + existing.Name, existing);
                    _canonicalsByUrl[existing.Url] = existing;

                    name = name + "THO";
                }
                else
                {
                    string existingName2 = GetCanonicalNameLiteral(existing.Conformance, true);
                    string currentName2 = GetCanonicalNameLiteral(cr, true);

                    if (existingName2 == currentName2)
                    {
                        //name = packageData.ClassPrefix + currentName2;
                        throw new Exception($"Name collision for '{name}'");
                    }

                    canonicalsByName.Remove(existing.Package.Key + existing.Name);
                    existing = existing with { Name = existingName2 };
                    canonicalsByName.Add(existing.Package.Key + existing.Name, existing);
                    _canonicalsByUrl[existing.Url] = existing;

                    name = currentName2;
                }
            }

            canonicalsByName.Add(packageData.Key + name, new()
            {
                Url = cr.Url,
                Name = name,
                Conformance = cr,
                Package = packageData,
            });

            _canonicalsByUrl.Add(cr.Url, new()
            {
                Url = cr.Url,
                Name = name,
                Conformance = cr,
                Package = packageData,
            });
        }

        HashSet<string> writtenCanonicalUrls = [];
        Dictionary<string, HashSet<string>> writtenExtensionDefinitions = [];

        // write extensions and relevant canonical URLs
        foreach (StructureDefinition sd in _info.ExtensionsByUrl.Values)
        {
            // get canonical info for this extension
            if (!_canonicalsByUrl.TryGetValue(sd.Url, out CanonicalInfo? ci))
            {
                throw new Exception($"Could not find canonical info for extension '{sd.Url}'");
            }

            if (_exclusionSet.Contains(sd.Url))
            {
                continue;
            }

            WriteExtension(sd, ci, writtenExtensionDefinitions);
            writtenCanonicalUrls.Add(ci.Url);
        }

        // TODO(ginoc): finish profile export
        //// write profile contents
        //foreach (StructureDefinition sd in _info.ProfilesByUrl.Values)
        //{
        //    WriteProfile(sd);
        //    writtenCanonicals.Add(sd.Url);
        //}

        // traverse our canonicals, sorted by name - note we need to do this last because other definitions write their own (extensions need to recurse, etc.).
        foreach (CanonicalInfo ci in canonicalsByName.Values.OrderBy(ci => ci.Name))
        {
            if (writtenCanonicalUrls.Contains(ci.Url))
            {
                continue;
            }

            // write the canonical URL
            WriteCanonicalUrl(ci.Conformance, ci.Package, ci.Name);
            writtenCanonicalUrls.Add(ci.Url);
        }

        CloseWriters();
    }

    private void BuildCoreTypeMap()
    {
        foreach (StructureDefinition primitive in _info.PrimitiveTypesByName.Values)
        {
            AddComponentToMap(primitive.cgComponent());
        }

        foreach (StructureDefinition complexType in _info.ComplexTypesByName.Values)
        {
            AddComponentToMap(complexType.cgComponent());
        }

        foreach (StructureDefinition resource in _info.ResourcesByName.Values)
        {
            AddComponentToMap(resource.cgComponent());
        }

        foreach (StructureDefinition fhirInterface in _info.InterfacesByName.Values)
        {
            AddComponentToMap(fhirInterface.cgComponent());
        }

        // manually add datatype
        _firelyToFhirPaths[$"{_firelyNamespace}.DataType"] = "DataType";
    }

    private void AddComponentToMap(
        ComponentDefinition cd,
        string? exportName = null)
    {
        if (!TypeNameMappings.TryGetValue(cd.Structure.Name, out string? structureName))
        {
            structureName = cd.Structure.Name.ToPascalCase();
        }

        if (exportName == null)
        {
            exportName = structureName;
        }
        else
        {
            exportName = structureName + "." + exportName;
        }

        _firelyToFhirPaths[$"{_firelyNamespace}.{exportName}"] = cd.Element.Path;

        // check for nested components
        foreach (ComponentDefinition component in cd.cgChildComponents(_info))
        {
            string componentExportName;

            if (string.IsNullOrEmpty(component.cgExplicitName()))
            {
                componentExportName =
                    $"{component.cgName(NamingConvention.PascalCase)}Component";
            }
            else
            {
                componentExportName =
                    $"{component.cgExplicitName()}" +
                    $"Component";
            }

            AddComponentToMap(
                component,
                componentExportName);
        }
    }

    /// <summary>
    /// Gets a name literal representation for a canonical resource.
    /// </summary>
    /// <param name="cr">The conformance resource.</param>
    /// <param name="hasCollision">Indicates if there is a collision in the name.</param>
    /// <returns>The name literal representation of the canonical.</returns>
    private string GetCanonicalNameLiteral(IConformanceResource cr, bool hasCollision = false)
    {
        Resource r = (Resource)cr;

        FhirArtifactClassEnum fhirArtifactClass;

        if (r is StructureDefinition sd)
        {
            fhirArtifactClass = sd.cgArtifactClass();
        }
        else
        {
            fhirArtifactClass = FhirArtifactClassEnum.Unknown;
        }

        return GetCanonicalNameLiteral(r.Id, cr.Name, cr.Url, r.TypeName, fhirArtifactClass, hasCollision);
    }

    /// <summary>
    /// Gets a name literal representation for a canonical resource.
    /// </summary>
    /// <param name="id">               The identifier.</param>
    /// <param name="name">             The language name.</param>
    /// <param name="url">              URL of the resource.</param>
    /// <param name="typeName">         Name of the type.</param>
    /// <param name="fhirArtifactClass">The FHIR artifact class.</param>
    /// <param name="hasCollision">     (Optional) Indicates if there is a collision in the name.</param>
    /// <returns>The name literal representation of the canonical.</returns>
    private string GetCanonicalNameLiteral(
        string id,
        string name,
        string url,
        string typeName,
        FhirArtifactClassEnum fhirArtifactClass,
        bool hasCollision = false)
    {
        string postfix = string.Empty;

        // default to using the name or ID
        List<string> components = string.IsNullOrEmpty(name) ? id.Split('-', '_', '.').ToList() : name.Split('-', '_', '.').ToList();

        if (hasCollision)
        {
            // grab the components of the last part of the URL
            string[] urlComponents = url.EndsWith('/')
                ? url.Split('/')[^2].Split('-', '_', '.')
                : url.Split('/')[^1].Split('-', '_', '.');

            // check for the URL only containing numbers
            if (!urlComponents.Any(c => c.Any(char.IsLetter)))
            {
                // manually append the numbers to the end of the name
                postfix = "_" + string.Join("_", urlComponents);
            }
            else
            {
                // use the URL as the name
                components = urlComponents.ToList();
            }
        }
        // check for no alphabetical characters in the current components, rejoin as a single component
        else if (!components.Any(c => c.Any(char.IsLetter)))
        {
            // just append the number after the rest is cleaned up
            postfix = "_" + string.Join("_", components);
            components.Clear();
        }

        string prefix;

        // insert the resource type
        if (fhirArtifactClass != FhirArtifactClassEnum.Unknown)
        {
            // use the cg type instead so we get better names
            prefix = fhirArtifactClass.ToString();
        }
        else if (typeName.EndsWith("Definition", StringComparison.Ordinal))
        {
            prefix = typeName[..^10];
        }
        else
        {
            prefix = typeName;
        }

        components.Insert(0, prefix);

        // remove component parts we want to drop
        components = components.Where(c => !_nameComponentsToRemove.Contains(c)).ToList();

        // remove repetitions of words, but maintain any numbers
        HashSet<string> used = [];
        List<string> valid = [];
        foreach (string current in components)
        {
            // skip numbers and leave in their existing order
            if (!current.Any(char.IsLetter))
            {
                valid.Add(current);
                continue;
            }

            // check to see if this word already exists
            if (used.Contains(current))
            {
                // skip
                continue;
            }

            valid.Add(current);
            used.Add(current);
        }

        // join everything into a PascalCase string
        return CleanName(components.ToPascalCaseWord()) + postfix;
    }

    /// <summary>Writes the canonical URL of a conformance resource.</summary>
    /// <param name="sd">The SD.</param>
    private void WriteCanonicalUrl(IConformanceResource cr, PackageData packageData, string name, ExportStreamWriter? writer = null)
    {
        // get a definition writer
        writer ??= GetCanonicalUrlWriter(packageData);

        string summary = string.IsNullOrEmpty(cr.Description)
            ? "Name: " + cr.Name
            : cr.Description + "\nName:" + cr.Name;

        string remarks = cr.Purpose;

        if (cr is ValueSet vs)
        {
            string[] referencedCodeSystems = vs.cgReferencedCodeSystems().ToArray();
            if (referencedCodeSystems.Length == 1)
            {
                remarks = (string.IsNullOrEmpty(remarks) ? string.Empty : remarks + "\n") +
                    "System: " + referencedCodeSystems.First();
            }
            else
            {
                remarks += (string.IsNullOrEmpty(remarks) ? string.Empty : remarks + "\n") +
                    "Systems: " + referencedCodeSystems.Length + "\n -" + string.Join("\n -", referencedCodeSystems);
            }
        }

        writer.WriteIndentedComment(summary, isSummary: true);
        writer.WriteIndentedComment(remarks, isSummary: false, isRemarks: true);
        writer.WriteLineIndented($"public const string {name} = \"{cr.Url}\";");

        writer.WriteLine();
    }

    private static string CleanName(string name)
    {
        return FhirSanitizationUtils.SanitizeForProperty(name, [], NamingConvention.PascalCase);
    }

    /// <summary>
    /// Process all value sets.
    /// This is a hybrid processor that matches both
    /// CSharpFirely2.WriteSharedValueSets and CSharpFirely2.WriteEnums.
    /// Essentially, it tracks manual exclusions and determines the
    /// correct namespace based on bindings.
    /// </summary>
    private void ProcessAndWriteValueSets()
    {
        HashSet<string> processedValueSets = [];
        Dictionary<string, HashSet<string>> usedEnumsByPackage = [];

        // traverse all versions of all value sets
        foreach ((string unversionedUrl, string[] versions) in _info.ValueSetVersions.OrderBy(kvp => kvp.Key))
        {
            // check for exclusions
            if (CSharpFirely2.ExclusionSet.Contains(unversionedUrl))
            {
                continue;
            }

            // traverse value sets starting with highest version
            foreach (string vsVersion in versions.OrderDescending())
            {
                if (!_info.TryGetValueSet(unversionedUrl, vsVersion, out ValueSet? unexpandedVs))
                {
                    continue;
                }

                if (processedValueSets.Contains(unexpandedVs.Url))
                {
                    continue;
                }

                processedValueSets.Add(unexpandedVs.Url);

                //IEnumerable<StructureElementCollection> bindings = _info.AllBindingsForVs(unexpandedVs.Url);
                //Hl7.Fhir.Model.BindingStrength? strongestBinding = _info.StrongestBinding(bindings);

                //if (strongestBinding != Hl7.Fhir.Model.BindingStrength.Required)
                //{
                //    /* Since required bindings cannot be extended, those are the only bindings that
                //       can be represented using enums in the POCO classes (using <c>Code&lt;T&gt;</c>). All other coded members
                //       use <c>Code</c>, <c>Coding</c> or <c>CodeableConcept</c>.
                //       Consequently, we only need to generate enums for valuesets that are used as
                //       required bindings anywhere in the data model. */
                //    continue;
                //}

                if (!_info.TryExpandVs(unversionedUrl + "|" + vsVersion, out ValueSet? vs))
                {
                    continue;
                }

                //IEnumerable<string> referencedBy = bindings.cgExtractBaseTypes(_info);

                //string vsClassName = string.Empty;

                //if ((referencedBy.Count() < 2) && !CSharpFirely2._explicitSharedValueSets.Contains((_info.FhirSequence.ToString(), vs.Url)))
                //{
                //    vsClassName = referencedBy.First();
                //}

                if (!_info.TryGetPackageSource(vs, out string packageId, out string packageVersion))
                {
                    packageId = _info.MainPackageId;
                    packageVersion = _info.MainPackageVersion;
                }

                if (!usedEnumsByPackage.TryGetValue($"{packageId}@{packageVersion}", out HashSet<string>? usedEnumNames))
                {
                    usedEnumNames = [];
                    usedEnumsByPackage.Add($"{packageId}@{packageVersion}", usedEnumNames);
                }

                ProcessAndWriteValueSet(vs, usedEnumNames, packageId, packageVersion);
            }
        }
    }

    private void ProcessAndWriteValueSet(
        ValueSet vs,
        //string className,
        HashSet<string> usedEnumNames,
        string packageId,
        string packageVersion)
    {
        //if (_valueSetInfoByUrl.ContainsKey(vs.Url))
        //{
        //    return;
        //}

        string name = (vs.Name ?? vs.Id)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        string nameSanitized = FhirSanitizationUtils.SanitizeForProperty(name, [], NamingConvention.PascalCase);

        // Enums and their containing classes cannot have the same name,
        // so we have to correct these here
        if (CSharpFirely2.EnumNamesOverride.TryGetValue(vs.Url, out var replacementName))
        {
            nameSanitized = replacementName;
        }

        if (usedEnumNames.Contains(nameSanitized))
        {
            return;
        }

        usedEnumNames.Add(nameSanitized);

        // do not write the value set if it is in a core package
        if (FhirPackageUtils.PackageIsFhirRelease(packageId))
        {
            return;
        }

        PackageData packageData = GetPackageData(packageId, packageVersion);

        // build an updated name for the value set
        string defaultName = $"Hl7.Fhir.Model.ValueSet.{nameSanitized}";
        string fixedName = packageData.Namespace + ".Vs" + nameSanitized;

        if (defaultName != fixedName)
        {
            if (_valueSetNameMaps.ContainsKey(defaultName))
            {
                Console.Write("");
            }
            else
            {
                // add the mapping to the global list
                _valueSetNameMaps.Add(defaultName, fixedName);
            }
        }

        IEnumerable<FhirConcept> concepts = vs.cgGetFlatConcepts(_info);

        if (!concepts.Any())
        {
            return;
        }

        string vsName = "Vs" + nameSanitized;

        // get a writer for the value set
        ExportStreamWriter writer = GetValueSetWriter(packageData, vsName);

        IEnumerable<string> referencedCodeSystems = vs.cgReferencedCodeSystems();
        if (referencedCodeSystems.Count() == 1)
        {
            WriteIndentedComment(
                writer,
                $"{vs.Description}\n" +
                $"(url: {vs.Url})\n" +
                $"(system: {referencedCodeSystems.First()})");
        }
        else
        {
            WriteIndentedComment(
                writer,
                $"{vs.Description}\n" +
                $"(url: {vs.Url})\n" +
                $"(systems: {referencedCodeSystems.Count()})");
        }

        writer.WriteLineIndented($"public static class {vsName}");
        writer.OpenScope();             // open class

        List<string> switchStatements = [];

        HashSet<string> usedLiterals = [];

        foreach (FhirConcept concept in concepts.OrderBy(c => c.Code))
        {
            string codeName = ConvertEnumValue(concept.Code);
            string codeValue = FhirSanitizationUtils.SanitizeForValue(concept.Code);
            string description = string.IsNullOrEmpty(concept.Definition)
                ? $"MISSING DESCRIPTION\n(system: {concept.System})"
                : $"{concept.Definition}\n(system: {concept.System})";

            if (concept.HasProperty("status", "deprecated"))
            {
                description += "\nThis enum is DEPRECATED.";
            }

            WriteIndentedComment(writer, description);

            string display = FhirSanitizationUtils.SanitizeForValue(concept.Display);

            if (usedLiterals.Contains(codeName))
            {
                // start at 2 so that the unadorned version makes sense as v1
                for (int i = 2; i < 1000; i++)
                {
                    if (usedLiterals.Contains($"{codeName}_{i}"))
                    {
                        continue;
                    }

                    codeName = $"{codeName}_{i}";
                    break;
                }
            }

            usedLiterals.Add(codeName);

            if (string.IsNullOrEmpty(concept.Display))
            {
                writer.WriteLineIndented($"public static Hl7.Fhir.Model.Coding {codeName} => new Hl7.Fhir.Model.Coding(" +
                    $"\"{concept.System}\", " +
                    $"\"{concept.Code}\");");
            }
            else
            {
                writer.WriteLineIndented($"public static Hl7.Fhir.Model.Coding {codeName} => new Hl7.Fhir.Model.Coding(" +
                    $"\"{concept.System}\", " +
                    $"\"{concept.Code}\", " +
                    $"\"{concept.Display}\");");
            }

            switchStatements.Add($"case (\"{concept.System}\", \"{concept.Code}\"): yield return c; break;");
        }

        // write utility functions
        WriteIndentedComment(writer, "From a set of codes, return the ones from this value set.");
        writer.WriteLineIndented("public static IEnumerable<Hl7.Fhir.Model.Coding> FilterForThisSet(IEnumerable<Hl7.Fhir.Model.Coding> values)");
        OpenScope(writer);      // open function

        writer.WriteLineIndented($"if (!values.Any()) yield break;");
        writer.WriteLineIndented($"foreach (Hl7.Fhir.Model.Coding c in values)");
        OpenScope(writer);      // open foreach

        writer.WriteLineIndented($"switch (c.System, c.Code)");
        OpenScope(writer);      // open switch

        foreach (string statement in switchStatements)
        {
            writer.WriteLineIndented(statement);
        }

        CloseScope(writer, suppressNewline: true);     // close switch
        CloseScope(writer, suppressNewline: true);     // close foreach
        CloseScope(writer, suppressNewline: true);     // close function
        CloseScope(writer);     // close class

        WriteNamespaceClose(writer);
        WriteFooter(writer);
        writer.Dispose();
    }

    private void WriteExtensionAccessorsRecords(ExtensionData extData, ExportStreamWriter writer)
    {
        HashSet<string> appliedContextTypes = [];
        HashSet<string> usedRecordTypes = [];

        foreach (ExtensionContextRec ctx in extData.Contexts)
        {
            string contextType = ctx.ContextTarget?.IsRootOfStructure ?? false
                ? ctx.ContextTypeName
                    : ctx.ContextElementInfo != null
                    ? ctx.ContextElementInfo.PropertyType is ListTypeReference ctLtr
                        ? ctLtr.Element.PropertyTypeString
                        : ctx.ContextElementInfo.PropertyType.PropertyTypeString
                    : ctx.ContextTypeName;

            bool resolved = false;

            if (TypeNameMappings.TryGetValue(contextType, out string? mappedType))
            {
                contextType = mappedType;
                resolved = true;
            }

            if (ContextTypeMappings.TryGetValue(contextType, out mappedType))
            {
                contextType = mappedType;
                resolved = true;
            }

            if (!contextType.Contains('.'))
            {
                contextType = "Hl7.Fhir.Model." + contextType;
            }

            if (!resolved &&
                contextType.StartsWith("Hl7.Fhir.Model.", StringComparison.Ordinal) &&
                !_firelyToFhirPaths.ContainsKey(contextType))
            {
                // this is likely a type that does not exist, just output some comments
                writer.WriteLine();
                writer.WriteLineIndented($"/*");
                writer.WriteLineIndented($" * Generation Issue! Cannot resolve context {contextType} for extension {extData.Name} ({extData.Url}).");
                writer.WriteLineIndented($" * It it from a different version of FHIR?");
                writer.WriteLineIndented($" */");
                writer.WriteLine();

                // skip this context
                continue;
            }

            if (appliedContextTypes.Contains(contextType))
            {
                continue;
            }
            appliedContextTypes.Add(contextType);

            //writer.WriteLineIndented($"// Exported for {ctx.AllowedContext.Type}:{ctx.AllowedContext.Expression}.");

            //if (ctx.ContextTarget != null)
            //{
            //    writer.WriteLineIndented($"// Expanded to {ctx.ContextTarget.Element.Path}.");
            //}

            //if (ctx.ContextElementInfo != null)
            //{
            //    writer.WriteLineIndented($"// Type: {ctx.ContextElementInfo.ElementType}.");
            //}

            string elementType = extData.ValueTypeName.EndsWith('?')
                ? extData.ValueTypeName[0..^1]
                : extData.ValueTypeName;

            if (elementType.StartsWith("Hl7.Fhir.Model.", StringComparison.Ordinal) &&
                !_firelyToFhirPaths.ContainsKey(elementType))
            {
                // this is likely a type that does not exist, just output some comments
                writer.WriteLine();
                writer.WriteLineIndented($"/*");
                writer.WriteLineIndented($" * Generation Issue! Cannot resolve datatype {elementType} for extension {extData.Name} ({extData.Url}).");
                writer.WriteLineIndented($" * Is it from a different version of FHIR?");
                writer.WriteLineIndented($" */");
                writer.WriteLine();

                // datatype means we can't do anything with this extension
                break;
            }

            //string etShort = elementType.StartsWith("Hl7.Fhir.Model.", StringComparison.Ordinal)
            //    ? elementType = elementType[15..]
            //    : elementType;

            //if (!etShort.Contains('.'))
            //{
            //    if (!TypeNameMappings.ContainsKey(etShort) &&
            //        !TypeNameMappings.ContainsValue(etShort) &&
            //        !ContextTypeMappings.ContainsKey(etShort) &&
            //        !ContextTypeMappings.ContainsValue(etShort) &&
            //        (etShort != "DataType") &&
            //        !_info.TryResolveElementTree(etShort, out StructureDefinition? _, out ElementDefinition[] _))
            //    {
            //    }
            //}


            // check for simple extensions
            if (extData.ElementInfo != null)
            {
                // write a getter comment
                if (extData.IsList)
                {
                    writer.WriteIndentedComment(_extValueGetterPrefixArray + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }
                else
                {
                    writer.WriteIndentedComment(_extValueGetterPrefixScalar + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }

                if (extData.IsList)
                {
                    switch (elementType)
                    {
                        case "Hl7.Fhir.Model.Extension":
                            writer.WriteLineIndented($"public static IEnumerable<{_firelyNamespace}.Extension> {extData.Name}Get(this {contextType} o) =>");
                            writer.WriteLineIndented($"  o.GetExtensions({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            break;

                        case "List<Hl7.Fhir.Model.Extension>":
                            writer.WriteLineIndented($"public static IEnumerable<{_firelyNamespace}.Extension> {extData.Name}Get(this {contextType} o) =>");
                            writer.WriteLineIndented($"  o.GetExtensions({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            break;

                        case string lt when lt.StartsWith("List<", StringComparison.Ordinal):
                            writer.WriteLineIndented($"public static IEnumerable{elementType[4..]} {extData.Name}Get(this {contextType} o) =>");
                            writer.WriteLineIndented(
                                $"  o" +
                                $".GetExtensions({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name})" +
                                $".Where(e => e.Value != null && e.Value is {elementType[5..^1]})" +
                                $".Select(e => ({elementType[5..^1]})e.Value!);");
                            break;

                        default:
                            writer.WriteLineIndented($"public static IEnumerable<{extData.ValueTypeName}> {extData.Name}Get(this {contextType} o) =>");
                            writer.WriteLineIndented(
                                $"  o" +
                                $".GetExtensions({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name})" +
                                $".Where(e => e.Value != null && e.Value is {extData.ValueTypeName})" +
                                $".Select(e => ({extData.ValueTypeName})e.Value!);");
                            break;
                    }
                }
                else
                {
                    writer.WriteLineIndented($"public static {elementType}? {extData.Name}Get(this {contextType} o) =>");

                    switch (elementType)
                    {
                        case "bool":
                        case "bool?":
                            writer.WriteLineIndented($"  o.GetBoolExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            break;

                        case "int":
                        case "int?":
                            writer.WriteLineIndented($"  o.GetIntegerExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            break;

                        case "string":
                        case "string?":
                            writer.WriteLineIndented($"  o.GetStringExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            break;

                        default:
                            writer.WriteLineIndented($"  o.GetExtensionValue<{elementType}>({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            break;
                    }
                }

                writer.WriteLine();

                // write a setter comment
                if (extData.IsList)
                {
                    writer.WriteIndentedComment(_extValueSetterPrefixArray + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }
                else
                {
                    writer.WriteIndentedComment(_extValueSetterPrefixScalar + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }

                if (extData.IsList)
                {
                    switch (elementType)
                    {
                        case "Hl7.Fhir.Model.Extension":
                            writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, IEnumerable<{elementType}>? val)");
                            OpenScope(writer);      // setter function open
                            writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            writer.WriteLineIndented("if (val == null) return;");
                            writer.WriteLineIndented("if (!val.Any()) return;");
                            writer.WriteLineIndented($"o.Extension.AddRange(val);");
                            CloseScope(writer);      // setter function close
                            break;

                        case "List<Hl7.Fhir.Model.Extension>":
                            writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, IEnumerable<{_firelyNamespace}.Extension>? val)");
                            OpenScope(writer);      // setter function open
                            writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            writer.WriteLineIndented("if (val == null) return;");
                            writer.WriteLineIndented("if (!val.Any()) return;");
                            writer.WriteLineIndented($"o.Extension.AddRange(val);");
                            CloseScope(writer);      // setter function close
                            break;

                        case string lt when lt.StartsWith("List<", StringComparison.Ordinal):
                            writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, IEnumerable{elementType[4..]}? val)");
                            OpenScope(writer);      // setter function open
                            writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            writer.WriteLineIndented("if (val == null) return;");
                            writer.WriteLineIndented("if (!val.Any()) return;");
                            writer.WriteLineIndented($"foreach ({elementType[5..^1]} v in val)");
                            OpenScope(writer);      // foreach open
                            writer.WriteLineIndented($"o.AddExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name}, v);");
                            CloseScope(writer, suppressNewline: true);     // foreach close
                            CloseScope(writer);      // setter function close
                            break;

                        default:
                            writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, IEnumerable<{elementType}>? val)");
                            OpenScope(writer);      // setter function open
                            writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            writer.WriteLineIndented("if (val == null) return;");
                            writer.WriteLineIndented("if (!val.Any()) return;");
                            writer.WriteLineIndented($"foreach ({elementType} v in val)");
                            OpenScope(writer);      // foreach open
                            writer.WriteLineIndented($"o.AddExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name}, v);");
                            CloseScope(writer, suppressNewline: true);     // foreach close
                            CloseScope(writer);      // setter function close
                            break;
                    }
                }
                else
                {
                    switch (elementType)
                    {
                        case "Hl7.Fhir.Model.Extension":
                            writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, {elementType}? val)");
                            OpenScope(writer);      // setter function open
                            writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            writer.WriteLineIndented("if (val == null) return;");
                            writer.WriteLineIndented($"o.Extension.Add(val);");
                            CloseScope(writer);      // setter function close
                            break;

                        case "List<Hl7.Fhir.Model.Extension>":
                        case string lt when lt.StartsWith("List<", StringComparison.Ordinal):
                            throw new Exception($"List elementType found on scalar extension!");

                        default:
                            writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, {elementType}? val)");
                            OpenScope(writer);      // setter function open
                            writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                            writer.WriteLineIndented("if (val == null) return;");
                            writer.WriteLineIndented($"o.AddExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name}, val);");
                            CloseScope(writer);      // setter function close
                            break;
                    }
                }
            }
            else
            {
                // write the accessor class
                if (WriteExtensionAccessorClass(writer, extData, usedRecordTypes))
                {
                    usedRecordTypes.Add(elementType);
                }

                // write a comment for the getter
                if (extData.IsList)
                {
                    writer.WriteIndentedComment(_extValueGetterPrefixArray + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }
                else
                {
                    writer.WriteIndentedComment(_extValueGetterPrefixScalar + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }

                if (extData.IsList)
                {
                    writer.WriteLineIndented($"public static IEnumerable<{elementType}> {extData.Name}Get(this {contextType} o)");
                    OpenScope(writer);      // function open
                    writer.WriteLineIndented($"IEnumerable<Extension> roots = o.GetExtensions({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                    writer.WriteLineIndented("if (!roots.Any()) yield break;");
                    writer.WriteLineIndented($"foreach (Extension root in roots)");
                    OpenScope(writer);      // foreach open

                    // pull values from the extension tree
                    WriteExtensionRecordPropertyReads(writer, extData, "root");

                    // add to our list
                    writer.WriteLineIndented($"yield return new {elementType}()");
                    WriteExtensionRecordCreateFromProperties(writer, extData);

                    CloseScope(writer, suppressNewline: true);     // foreach close
                    CloseScope(writer);     // function close
                }
                else
                {
                    writer.WriteLineIndented($"public static {elementType}? {extData.Name}Get(this {contextType} o)");
                    OpenScope(writer);      // function open
                    writer.WriteLineIndented($"Extension? root = o.GetExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                    writer.WriteLineIndented("if (root == null) return null;");

                    // pull values from the extension tree
                    WriteExtensionRecordPropertyReads(writer, extData, "root");

                    writer.WriteLineIndented($"return new {elementType}()");
                    WriteExtensionRecordCreateFromProperties(writer, extData);

                    CloseScope(writer);     // function close
                }

                // write a comment for the setter
                if (extData.IsList)
                {
                    writer.WriteIndentedComment(_extValueSetterPrefixArray + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }
                else
                {
                    writer.WriteIndentedComment(_extValueSetterPrefixScalar + extData.Summary, isSummary: true);
                    writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
                }

                // write a setter for either a single value or an array
                if (extData.IsList)
                {
                    writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, IEnumerable<{elementType}> values)");
                    OpenScope(writer);      // setter function open
                    writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                    WriteExtensionRecordPropertyWrites(writer, extData, "o", "values");
                    CloseScope(writer);     // setter function close
                }
                else
                {
                    writer.WriteLineIndented($"public static void {extData.Name}Set(this {contextType} o, {elementType}? value)");
                    OpenScope(writer);      // setter function open
                    writer.WriteLineIndented($"o.RemoveExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extData.Name});");
                    WriteExtensionRecordPropertyWrites(writer, extData, "o", "value");
                    CloseScope(writer);     // setter function close
                }
            }
        }
    }


    private void WriteExtensionRecordPropertyReads(ExportStreamWriter writer, ExtensionData extData, string parentVarName)
    {
        if ((extData.ValueElement != null) && (extData.ElementInfo != null))
        {
            ListTypeReference? extLTR = extData.ElementInfo.PropertyType is ListTypeReference ltr ? ltr : null;
            string elementType = extLTR?.Element.PropertyTypeString ?? extData.ElementInfo.PropertyType.PropertyTypeString;

            if (elementType.EndsWith('?'))
            {
                elementType = elementType[0..^1];
            }

            string valName = _processingValuePrefix + extData.Name;
            string extName = extData.ParentName + extData.Name;

            if (extData.IsList)
            {
                writer.WriteLineIndented($"IEnumerable<{elementType}>? {valName} = (IEnumerable<{elementType}>){parentVarName}" +
                    $".GetExtensions({extData.PackageNamespace}.{_classNameDefinitions}.{extName})" +
                    $".Where(e => e.Value != null && e.Value is {elementType})" +
                    $".Select(e => ({elementType})e.Value!);");
            }
            else
            {
                switch (elementType)
                {
                    case "bool":
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}.GetBoolExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extName});");
                        break;

                    case "int":
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}.GetIntegerExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extName});");
                        break;

                    case "string":
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}.GetStringExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extName});");
                        break;

                    default:
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}.GetExtensionValue<{elementType}>({extData.PackageNamespace}.{_classNameDefinitions}.{extName});");
                        break;
                }
            }

            return;
        }

        foreach (ExtensionData extensionData in extData.Children)
        {
            if (extensionData.Children.Length != 0)
            {
                writer.WriteLineIndented($"Extension? ext{extensionData.Name} = {parentVarName}.GetExtension({extData.PackageNamespace}.{_classNameDefinitions}.{extensionData.Name});");
                writer.WriteLineIndented($"if (ext{extensionData.Name} != null)");
                OpenScope(writer);

                // nest through children
                foreach (ExtensionData child in extensionData.Children)
                {
                    WriteExtensionRecordPropertyReads(writer, child, $"ext{extensionData.Name}");
                }

                CloseScope(writer);
                continue;
            }

            WriteExtensionRecordPropertyReads(writer, extensionData, parentVarName);
        }

        foreach (ExternalExtensionReferenceData externalExt in extData.ExternalChildren)
        {
            string valName = _processingValuePrefix + externalExt.Name;

            if (externalExt.IsList)
            {
                // use the URL since we do not know if we have access to the literal
                writer.WriteLineIndented($"IEnumerable<Extension> {valName} = {parentVarName}.GetExtensions(\"{externalExt.Url}\");");
            }
            else
            {
                // use the URL since we do not know if we have access to the literal
                writer.WriteLineIndented($"Extension? {valName} = {parentVarName}.GetExtension(\"{externalExt.Url}\");");
            }
        }
    }

    private void WriteExtensionRecordCreateFromProperties(ExportStreamWriter writer, ExtensionData extData)
    {
        OpenScope(writer);      // new record open

        // traverse our sub-extensions to use as properties
        foreach (ExtensionData subExtData in extData.Children)
        {
            string valName = _processingValuePrefix + subExtData.Name;

            if (subExtData.IsList)
            {
                writer.WriteLineIndented($"{subExtData.Name}{_recordValueSuffix} = {valName}?.ToList(),");
            }
            //else if ((subExtData.ElementInfo?.PropertyType is PrimitiveTypeReference ptr) && (ptr.Name == ptr.ConveniencePropertyTypeString))
            //{
            //    writer.WriteLineIndented($"{subExtData.Name}{_recordValueSuffix} = {valName}.Value,");
            //}
            else
            {
                writer.WriteLineIndented($"{subExtData.Name}{_recordValueSuffix} = {valName},");
            }
        }

        // traverse external extensions for properties
        foreach (ExternalExtensionReferenceData externalExt in extData.ExternalChildren)
        {
            string valName = _processingValuePrefix + externalExt.Name;

            if (externalExt.IsList)
            {
                writer.WriteLineIndented($"{externalExt.Name}{_recordValueSuffix} = {valName}.Any() ? {valName}.ToList() : null,");
            }
            else
            {
                writer.WriteLineIndented($"{externalExt.Name}{_recordValueSuffix} = {valName},");
            }
        }

        CloseScope(writer, includeSemicolon: true, suppressNewline: true);     // new record close
    }


    private void WriteProfileExtRecordPropertyReads(ExportStreamWriter writer, ProfileSliceInfo psi, string parentVarName)
    {
        if (psi.ValueExtData == null)
        {
            return;
        }

        if ((psi.ValueExtData.ValueElement != null) && (psi.ValueExtData.ElementInfo != null))
        {
            bool isList = psi.ValueExtData.IsList && psi.Element.cgCardinalityMax() > 1;

            ListTypeReference? extLTR = psi.ValueExtData.ElementInfo.PropertyType is ListTypeReference ltr ? ltr : null;
            string elementType = extLTR?.Element.PropertyTypeString ?? psi.ValueExtData.ElementInfo.PropertyType.PropertyTypeString;
            //string elementType = psi.ValueExtData.ElementInfo.IsPrimitive ? psi.ValueExtData.ElementInfo.PrimitiveHelperType! : psi.ValueExtData.ElementInfo.ElementType!;

            string valName = _processingValuePrefix + psi.ValueExtData.Name;
            string extName = psi.ValueExtData.ParentName + psi.ValueExtData.Name;

            if (isList)
            {
                writer.WriteLineIndented($"IEnumerable<{elementType}>? {valName} = (IEnumerable<{elementType}>){parentVarName}" +
                    $".GetExtensions({psi.ValueExtData.PackageNamespace}.{_classNameDefinitions}.{extName})" +
                    $".Where(e => e.Value != null && e.Value is {elementType})" +
                    $".Select(e => ({elementType})e.Value!);");
            }
            else
            {
                switch (elementType)
                {
                    case "bool":
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}?.Value is FhirBoolean vb ? vb.Value : null;");
                        break;

                    case "int":
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}?.Value is Integer vi ? vi.Value : null;");
                        break;

                    case "string":
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}?.Value is FhirString vs ? vs.Value : null;");
                        break;

                    default:
                        writer.WriteLineIndented($"{elementType}? {valName} = {parentVarName}?.Value is {elementType} vv ? vv : null;");
                        break;
                }
            }

            writer.WriteLine();
            //return;
        }


        foreach (ProfileSliceInfo childPsi in psi.ChildSlicesBySliceId.Values)
        {
            if (childPsi.ValueExtData == null)
            {
                throw new Exception(parentVarName + " has a child slice with no value extension data.");
            }

            //if (childPsi.ChildSlicesBySliceId.Count != 0)
            //{
            //    writer.WriteLineIndented($"Extension? ext{childPsi.ValueExtData.Name} = {parentVarName}.GetExtension({_classNameDefinitions}.{childPsi.ValueExtData.Name});");
            //    writer.WriteLineIndented($"if (ext{childPsi.ValueExtData.Name} != null)");
            //    OpenScope(writer);

            //    // nest through children
            //    foreach (ProfileSliceInfo grandchild in childPsi.ChildSlicesBySliceId.Values)
            //    {
            //        WriteProfileRecordPropertyReads(writer, grandchild, $"ext{childPsi.ValueExtData.Name}");
            //    }

            //    CloseScope(writer);
            //    continue;
            //}

            string childVarName = _processingExtPrefix + childPsi.ValueExtData.Name;

            writer.WriteLineIndented($"Extension? {childVarName} = {parentVarName}?.Value?.GetExtension({psi.ValueExtData.PackageNamespace}.{_classNameDefinitions}.{childPsi.ValueExtData.Name});");
            WriteProfileExtRecordPropertyReads(writer, childPsi, childVarName);
        }
    }

    private void WriteProfileExtRecordCreateFromProperties(ExportStreamWriter writer, ProfileSliceInfo psi, bool openScope = true)
    {
        if ((psi.ValueExtData == null) ||
            (psi.ValueExtData.ValueElement == null) || (psi.ValueExtData.ElementInfo == null))
        {
            return;
        }

        if (openScope)
        {
            OpenScope(writer);      // new record open
        }

        // put this level's value
        string valName = _processingValuePrefix + psi.ValueExtData.Name;

        writer.WriteLineIndented($"{psi.SliceName.ToPascalCase()} = {valName},");

        // traverse our sub-extensions to use as properties
        foreach (ProfileSliceInfo child in psi.ChildSlicesBySliceId.Values)
        {
            WriteProfileExtRecordCreateFromProperties(writer, child, false);
        }

        if (openScope)
        {
            CloseScope(writer, includeSemicolon: true, suppressNewline: true);     // new record close
        }
    }

    private void WriteExtensionRecordPropertyWrites(
        ExportStreamWriter writer,
        ExtensionData extData,
        string parentObjectName,
        string parentValueName,
        string parentBoolName = "")
    {
        string extensionLiteral = $"{extData.PackageNamespace}.{_classNameDefinitions}.{extData.ParentName}{extData.Name}";
        string extProcessingName = _processingExtPrefix + extData.Name;
        string valName = string.IsNullOrEmpty(extData.ParentName)
            ? parentValueName
            : $"{parentValueName}.{extData.Name}{_recordValueSuffix}";

        // check if this is a terminal leaf (has a value element)
        if ((extData.ValueElement != null) && (extData.ElementInfo != null))
        {
            writer.WriteLineIndented($"if ({valName} != null)");
            OpenScope(writer);      // if open

            ListTypeReference? extLTR = extData.ElementInfo.PropertyType is ListTypeReference ltr ? ltr : null;
            _ = CSharpFirely2.TryGetPrimitiveType(extData.ElementInfo.PropertyType, out PrimitiveTypeReference? extPTR);

            //string elementType = extData.ElementInfo.IsPrimitive ? extData.ElementInfo.PrimitiveHelperType! : extData.ElementInfo.ElementType!;
            //string elementType = (extPTR?.Name == extPTR?.ConveniencePropertyTypeString ? extPTR?.Name : null) ?? extData.ElementInfo.PropertyType.PropertyTypeString;
            string elementType = extLTR?.Element.PropertyTypeString ?? extData.ElementInfo.PropertyType.PropertyTypeString;

            if (extData.IsList)
            {
                string iteratorValName = _processingValuePrefix + extData.Name + _processingArraySuffix;
                string iteratorBoolName = _processingExtAddPrefix + extData.Name + _processingArraySuffix;

                writer.WriteLineIndented($"if ({valName}.Any())");
                OpenScope(writer);          // if open

                if (!string.IsNullOrEmpty(parentBoolName))
                {
                    writer.WriteLineIndented($"bool {iteratorBoolName} = false;");
                }

                writer.WriteLineIndented($"foreach ({elementType}? {iteratorValName} in {valName})");
                OpenScope(writer);          // foreach open

                writer.WriteLineIndented($"if ({_processingValuePrefix}{extData.Name}{_processingArraySuffix} == null) continue;");

                //if ((extPTR != null) && (extPTR.PropertyTypeString != elementType))
                //{
                //    writer.WriteLineIndented($"{parentObjectName}.AddExtension({extensionLiteral}, new {extPTR.PropertyTypeString}({iteratorValName}));");
                //}
                //else
                //{
                    writer.WriteLineIndented($"{parentObjectName}.AddExtension({extensionLiteral}, {iteratorValName});");
                //}

                if (!string.IsNullOrEmpty(parentBoolName))
                {
                    writer.WriteLineIndented($"{iteratorBoolName} = true;");
                }

                CloseScope(writer);         // foreach close

                if (!string.IsNullOrEmpty(parentBoolName))
                {
                    writer.WriteLineIndented($"if ({iteratorBoolName}) {parentBoolName} = true;");
                }

                CloseScope(writer);         // if close
            }
            //else if ((extPTR != null) && (extPTR.PropertyTypeString != elementType))
            //{
            //    writer.WriteLineIndented($"{parentObjectName}.AddExtension({extensionLiteral}, new {extPTR.PropertyTypeString}({valName}));");
            //    if (!string.IsNullOrEmpty(parentBoolName))
            //    {
            //        writer.WriteLineIndented($"{parentBoolName} = true;");
            //    }
            //}
            else
            {
                writer.WriteLineIndented($"{parentObjectName}.AddExtension({extensionLiteral}, {valName});");
                if (!string.IsNullOrEmpty(parentBoolName))
                {
                    writer.WriteLineIndented($"{parentBoolName} = true;");
                }
            }

            CloseScope(writer);      // if close
            return;
        }

        string boolName = _processingExtAddPrefix + extData.Name;
        string extIteratorValName = _processingValuePrefix + extData.Name + _processingArraySuffix;

        writer.WriteLineIndented($"if ({valName} != null)");
        OpenScope(writer);      // if open

        string extParentValueName = extData.IsList
            ? extData.ParentName + extData.Name + _recordClassSuffix
            : valName;

        // check for array
        if (extData.IsList)
        {
            writer.WriteLineIndented($"foreach ({extData.ParentName}{extData.Name}{_recordClassSuffix} {extIteratorValName} in {valName})");
            OpenScope(writer);      // foreach open
            writer.WriteLineIndented($"Extension {extProcessingName} = new Extension() {{ Url = {extensionLiteral} }};");
            writer.WriteLineIndented($"bool {boolName} = false;");

            foreach (ExtensionData subExt in extData.Children)
            {
                WriteExtensionRecordPropertyWrites(writer, subExt, extProcessingName, extIteratorValName, boolName);
            }

            foreach (ExternalExtensionReferenceData externalExt in extData.ExternalChildren)
            {
                if (externalExt.IsList)
                {
                    writer.WriteLineIndented($"if (({extIteratorValName}.{externalExt.Name}{_recordValueSuffix} != null) && ({extIteratorValName}.{externalExt.Name}{_recordValueSuffix}.Count > 0))");
                    OpenScope(writer);
                    writer.WriteLineIndented($"{extProcessingName}.Extension.AddRange({extIteratorValName}.{externalExt.Name}{_recordValueSuffix});");
                    writer.WriteLineIndented($"{boolName} = true;");
                    CloseScope(writer);
                }
                else
                {
                    writer.WriteLineIndented($"if ({extIteratorValName}.{externalExt.Name}{_recordValueSuffix} != null)");
                    OpenScope(writer);
                    writer.WriteLineIndented($"{extProcessingName}.Extension.Add({extIteratorValName}.{externalExt.Name}{_recordValueSuffix});");
                    writer.WriteLineIndented($"{boolName} = true;");
                    CloseScope(writer);
                }
            }

            writer.WriteLineIndented($"if ({boolName}) {parentObjectName}.Extension.Add({extProcessingName});");

            CloseScope(writer, suppressNewline: true);      // foreach close
        }
        else
        {
            writer.WriteLineIndented($"Extension {extProcessingName} = new Extension() {{ Url = {extensionLiteral} }};");
            writer.WriteLineIndented($"bool {boolName} = false;");

            foreach (ExtensionData subExt in extData.Children)
            {
                WriteExtensionRecordPropertyWrites(writer, subExt, extProcessingName, extParentValueName, boolName);
            }

            foreach (ExternalExtensionReferenceData externalExt in extData.ExternalChildren)
            {
                if (externalExt.IsList)
                {
                    writer.WriteLineIndented($"if (({valName}.{externalExt.Name}{_recordValueSuffix} != null) && ({valName}.{externalExt.Name}{_recordValueSuffix}.Count > 0))");
                    OpenScope(writer);
                    writer.WriteLineIndented($"{extProcessingName}.Extension.AddRange({valName}.{externalExt.Name}{_recordValueSuffix});");
                    writer.WriteLineIndented($"{boolName} = true;");
                    CloseScope(writer);
                }
                else
                {
                    writer.WriteLineIndented($"if ({valName}.{externalExt.Name}{_recordValueSuffix} != null)");
                    OpenScope(writer);
                    writer.WriteLineIndented($"{extProcessingName}.Extension.Add({valName}.{externalExt.Name}{_recordValueSuffix});");
                    writer.WriteLineIndented($"{boolName} = true;");
                    CloseScope(writer);
                }
            }

            writer.WriteLineIndented($"if ({boolName}) {parentObjectName}.Extension.Add({extProcessingName});");
        }

        CloseScope(writer, suppressNewline:true);     // if close
    }

    private bool WriteExtensionAccessorClass(ExportStreamWriter writer, ExtensionData extData, HashSet<string> usedRecordTypes)
    {
        if (usedRecordTypes.Contains(extData.ValueTypeName))
        {
            return false;
        }

        if (string.IsNullOrEmpty(extData.ParentName))
        {
            // write a comment for the accessor record class
            writer.WriteIndentedComment($"Record class to access the contents of the {extData.Name} extension\nExtension Url: {extData.Url}", isSummary: true);
            writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
        }
        else
        {
            // write a comment for the accessor record class
            writer.WriteIndentedComment($"Record class to access the contents of the {extData.ParentName}.{extData.Name} sub extension\nExtension Url: {extData.Url})", isSummary: true);
            writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);
        }

        // open our class
        writer.WriteLineIndented($"public record class {extData.ValueTypeName}");
        OpenScope(writer);

        // traverse our sub-extensions to use as properties
        foreach (ExtensionData subExtData in extData.Children)
        {
            string et;

            // check if we have type information
            if ((subExtData.ValueElement != null) && (subExtData.ElementInfo != null))
            {
                ListTypeReference? subExtLTR = subExtData.ElementInfo.PropertyType is ListTypeReference ltr ? ltr : null;
                _ = CSharpFirely2.TryGetPrimitiveType(subExtData.ElementInfo.PropertyType, out PrimitiveTypeReference? subExtPTR);

                //et = (subExtPTR?.Name == subExtPTR?.ConveniencePropertyTypeString ? subExtPTR?.ConveniencePropertyTypeString : null)
                //    ?? subExtData.ElementInfo.PropertyType.PropertyTypeString;
                et = subExtLTR?.Element.PropertyTypeString ?? subExtData.ElementInfo.PropertyType.PropertyTypeString;
                //et = subExtData.ElementInfo.IsPrimitive ? subExtData.ElementInfo.PrimitiveHelperType! : subExtData.ElementInfo.ElementType!;

                // get optional flags
                BuildElementOptionalFlags(
                    subExtData.ValueElement,
                    out string summary,
                    out string isModifier,
                    out string choice,
                    out string allowedTypes,
                    out string resourceReferences);

                string types;
                if (string.IsNullOrEmpty(allowedTypes))
                {
                    types = string.Empty;
                }
                else
                {
                    types = string.IsNullOrEmpty(choice) ? "\n.Net Types:" : "\n.Net Choice Types: " +
                        allowedTypes[14..^3]
                        .Replace("typeof(Hl7.Fhir.Model.", "")
                        .Replace("),", ",")
                        .Replace(",", ", ");
                }

                // write our comments
                WriteIndentedComment(
                    writer,
                    subExtData.Summary,
                    isSummary: true);
                WriteIndentedComment(
                    writer,
                    subExtData.Remarks +
                    (string.IsNullOrEmpty(resourceReferences) ? string.Empty : "\nReferences: " + resourceReferences[12..^2]) +
                    (string.IsNullOrEmpty(types) ? string.Empty : types),
                    isSummary: false,
                    isRemarks: true);
            }
            else
            {
                et = extData.Name + subExtData.Name + _recordClassSuffix;

                // write our comments
                WriteIndentedComment(writer, subExtData.Summary, isSummary: true);
                WriteIndentedComment(writer, subExtData.Remarks, isSummary: false, isRemarks: true);
            }

            if (TypeNameMappings.TryGetValue(et, out string? mappedType))
            {
                et = mappedType;
            }

            // write this property
            if (subExtData.IsList && (!et.StartsWith("List<", StringComparison.Ordinal)))
            {
                writer.WriteLineIndented($"public List<{et}>? {subExtData.Name}{_recordValueSuffix} {{ get; init; }}");
            }
            else if (et.EndsWith('?'))
            {
                writer.WriteLineIndented($"public {et} {subExtData.Name}{_recordValueSuffix} {{ get; init; }}");
            }
            else
            {
                writer.WriteLineIndented($"public {et}? {subExtData.Name}{_recordValueSuffix} {{ get; init; }}");
            }
        }

        // traverse our external sub-extensions to use as Extension properties
        foreach (ExternalExtensionReferenceData externalChild in extData.ExternalChildren)
        {
            string et = externalChild.ValueTypeName;

            // write our comments
            WriteIndentedComment(writer, externalChild.Summary, isSummary: true);
            WriteIndentedComment(writer, externalChild.Remarks, isSummary: false, isRemarks: true);

            if (TypeNameMappings.TryGetValue(et, out string? mappedType))
            {
                et = mappedType;
            }

            // write this property
            if (externalChild.IsList && (!et.StartsWith("List<", StringComparison.Ordinal)))
            {
                writer.WriteLineIndented($"public List<{et}>? {externalChild.Name}{_recordValueSuffix} {{ get; init; }}");
            }
            else if (et.EndsWith('?'))
            {
                writer.WriteLineIndented($"public {et} {externalChild.Name}{_recordValueSuffix} {{ get; init; }}");
            }
            else
            {
                writer.WriteLineIndented($"public {et}? {externalChild.Name}{_recordValueSuffix} {{ get; init; }}");
            }
        }

        // close our record class
        CloseScope(writer);

        // check for sub-extensions that we need to nest into
        foreach (ExtensionData subExtData in extData.Children)
        {
            if ((subExtData.ValueElement == null) || (subExtData.ElementInfo == null))
            {
                WriteExtensionAccessorClass(writer, subExtData, usedRecordTypes);
            }
        }

        return true;
    }

    private bool WriteProfileAccessorClass(ExportStreamWriter writer, StructureDefinition sd, ProfileSliceInfo sliceData)
    {
        //if (usedRecordTypes.Contains(profileData.SliceName))
        //{
        //    return false;
        //}

        // write a comment for the accessor record class
        writer.WriteIndentedComment(
            $"Record class to access the contents of the {sliceData.SliceName} slice and child slices" +
                $"\nElement Id: {sliceData.Id}" +
                $"\nChild slices:\n  {string.Join("\n  ", sliceData.ChildSlicesBySliceId.Values.Select(psi => psi.Id))}",
            isSummary: true);
        //writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);

        // open our class
        writer.WriteLineIndented($"public record class {sliceData.ValueTypeName}");
        OpenScope(writer);

        //bool isExtensionSlice = sliceData.Discriminator == null ? false : _findExtensionPathRegex.IsMatch(sliceData.Discriminator.Path);

        // first we need to write a property for the slice itself
        if (sliceData.ValueExtData != null)
        {
            writer.WriteIndentedComment($"Access the primary extension of this slice.");
            writer.WriteIndentedComment(sliceData.ValueExtData.Summary, isSummary: false, isRemarks:true);
            writer.WriteLineIndented($"public {sliceData.ValueExtData.ValueTypeName}? {sliceData.SliceName.ToPascalCase()} {{ get; init; }}");
        }

        WriteProfileComponentProps(sliceData.SliceName.ToPascalCase(), sliceData.ChildSlicesBySliceId);

        //// traverse our sub-extensions to use as properties
        //foreach (ProfileSliceInfo subData in sliceData.ChildSlicesBySliceId.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
        //{
        //    string et;

        //    // check if we have type information
        //    if ((subExtData.ValueElement != null) && (subExtData.ElementInfo != null))
        //    {
        //        et = subExtData.ElementInfo.IsPrimitive ? subExtData.ElementInfo.PrimitiveHelperType! : subExtData.ElementInfo.ElementType!;

        //        // get optional flags
        //        BuildElementOptionalFlags(
        //            subExtData.ValueElement,
        //            out string summary,
        //            out string isModifier,
        //            out string choice,
        //            out string allowedTypes,
        //            out string resourceReferences);

        //        // write our comments
        //        WriteIndentedComment(
        //            writer,
        //            subExtData.Summary +
        //            (string.IsNullOrEmpty(choice) ? string.Empty : "\nChoice type: " + choice[9..]) +
        //            (string.IsNullOrEmpty(allowedTypes) ? string.Empty : "\nAllowed Types: " + allowedTypes[14..^2]) +
        //            (string.IsNullOrEmpty(resourceReferences) ? string.Empty : "\nReferences: " + resourceReferences[12..^2]),
        //            isSummary: true);
        //        WriteIndentedComment(writer, subExtData.Remarks, isSummary: false, isRemarks: true);
        //    }
        //    else
        //    {
        //        et = extData.Name + subExtData.Name + _recordClassSuffix;

        //        // write our comments
        //        WriteIndentedComment(writer, subExtData.Summary, isSummary: true);
        //        WriteIndentedComment(writer, subExtData.Remarks, isSummary: false, isRemarks: true);
        //    }

        //    // write this property
        //    if (subExtData.IsList)
        //    {
        //        writer.WriteLineIndented($"public List<{et}>? {subExtData.Name}{_recordValueSuffix} {{ get; init; }}");
        //    }
        //    else
        //    {
        //        writer.WriteLineIndented($"public {et}? {subExtData.Name}{_recordValueSuffix} {{ get; init; }}");
        //    }
        //}

        // close our record class
        CloseScope(writer);

        //// check for sub-extensions that we need to nest into
        //foreach (ExtensionData subExtData in extData.Children)
        //{
        //    if ((subExtData.ValueElement == null) || (subExtData.ElementInfo == null))
        //    {
        //        WriteExtensionAccessorClass(writer, subExtData, usedRecordTypes);
        //    }
        //}

        return true;

        void WriteProfileComponentProps(string parentName, Dictionary<string, ProfileSliceInfo> data)
        {
            // traverse slices attached to this slice
            foreach (ProfileSliceInfo si in data.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
            {
                string sn = si.SliceName.ToPascalCase();
                string currentValName = sn.StartsWith(parentName, StringComparison.Ordinal) ? sn : parentName + sn;

                if (si.ValueExtData != null)
                {
                    writer.WriteIndentedComment($"Access the {si.SliceName} extension of this slice.");
                    writer.WriteIndentedComment(si.ValueExtData.Summary, isSummary: false, isRemarks: true);
                    writer.WriteLineIndented($"public {si.ValueExtData.ValueTypeName}? {currentValName} {{ get; init; }}");
                }

                WriteProfileComponentProps(currentValName, si.ChildSlicesBySliceId);
            }
        }
    }


    private record class ProfileSliceInfo
    {
        public required string Id { get; init; }
        public required string ParentPath { get; init; }
        public required string Path { get; init; }
        public required string ElementName { get; init; }
        public required string SliceName { get; init; }
        public required string AccessorTypeName { get; init; }
        public required string ValueTypeName { get; init; }
        public required ElementDefinition Element { get; init; }
        public required ElementDefinition? ElementThatDefinedThisSlicing { get; init; }
        public required SliceDiscriminator? Discriminator { get; init; }

        public required ComponentDefinition? ContextCd { get; init;  }
        public required ExtensionData? ValueExtData { get; init; }

        public Dictionary<string, ProfileSliceInfo> ChildSlicesBySliceId { get; init; } = [];
    }

    private void WriteProfileComments(StructureDefinition sd, ExportStreamWriter writer)
    {

        if (!string.IsNullOrEmpty(sd.Description))
        {
            WriteIndentedComment(writer, sd.Description, isSummary: true);
        }
        else
        {
            WriteIndentedComment(writer, sd.Title, isSummary: true);
        }

        // check for must-support elements
        IEnumerable<ElementDefinition> mustSupports = sd.cgElements().Where(ed => ed.MustSupport == true);

        if (mustSupports.Any())
        {
            writer.WriteLineIndented("/// <remarks>");
            writer.WriteIndentedComment("Must support elements:", isSummary: false);

            foreach (ElementDefinition ms in mustSupports)
            {
                writer.WriteIndentedComment($"- {ms.Path}: {ms.cgShort()}", isSummary: false);
            }

            writer.WriteLineIndented("/// </remarks>");
        }
    }

    private void WriteProfile(StructureDefinition sd)
    {
        if (!_info.TryGetPackageSource(sd, out string packageId, out string packageVersion))
        {
            packageId = _info.MainPackageId;
            packageVersion = _info.MainPackageVersion;
        }

        //if (FhirPackageUtils.PackageIsFhirRelease(packageId))
        //{
        //    // skip core packages
        //    return;
        //}

        PackageData packageData = GetPackageData(packageId, packageVersion);

        string name = string.IsNullOrEmpty(sd.Name)
            ? sd.Id.ToPascalCase()
            : sd.Name.ToPascalCase();

        // remove any hyphens from the name
        name = name.Replace('-', '_');

        // get a canonical url writer
        ExportStreamWriter canonicalUrlWriter = GetCanonicalUrlWriter(packageData);

        WriteProfileComments(sd, canonicalUrlWriter);

        canonicalUrlWriter.WriteLineIndented($"public const string Profile{name} = \"{sd.Url}\";");
        canonicalUrlWriter.WriteLine();

        string filename = Path.Combine(_options.OutputDirectory, packageData.FolderName, $"{name}.cs");

        if (!Directory.Exists(Path.GetDirectoryName(filename)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
        }

        using (FileStream stream = new(filename, FileMode.Create))
        using (ExportStreamWriter writer = new(stream))
        {
            WriteHeader(writer, [packageData.Namespace]);

            WriteNamespaceOpen(writer, packageData.Namespace + _namespaceSuffixProfiles);

            WriteProfileComments(sd, writer);

            writer.WriteLineIndented($"public static class {name}");
            OpenScope(writer);      // class

            // write the profile URL
            WriteIndentedComment(writer, "The official URL for this profile.", isSummary: true);
            writer.WriteLineIndented($"public const string ProfileUrl = \"{sd.Url}\";");
            writer.WriteLine();

            // write the profile version
            if (!string.IsNullOrEmpty(sd.Version))
            {
                WriteIndentedComment(writer, "The declared version of this profile.", isSummary: true);
                writer.WriteLineIndented($"public const string ProfileVersion = \"{sd.Version}\";");
                writer.WriteLine();
            }
            else
            {
                WriteIndentedComment(writer, "The package version of this profile.", isSummary: true);
                writer.WriteLineIndented($"public const string ProfileVersion = \"{packageData.PackageVersion}\";");
                writer.WriteLine();
            }

            Dictionary<string, ProfileSliceInfo> profileSlices = [];

            // traverse all elements
            foreach (ElementDefinition ed in sd.cgElements(skipSlices: false))
            {
                string id = ed.ElementId;
                string[] pathComponents = ed.Path.Split('.');

                Dictionary<string, ProfileSliceInfo> currentDict = profileSlices;

                int firstColonLoc = ed.ElementId.IndexOf(':');
                while (firstColonLoc != -1)
                {
                    int endOfSliceNameLoc = ed.ElementId.IndexOfAny([':', '.'], firstColonLoc + 1);

                    if (endOfSliceNameLoc == -1)
                    {
                        endOfSliceNameLoc = ed.ElementId.Length;
                    }

                    string sliceId = id[..endOfSliceNameLoc];
                    string sliceName = ed.ElementId.Substring(firstColonLoc + 1, endOfSliceNameLoc - firstColonLoc - 1);

                    if (!currentDict.TryGetValue(sliceId, out ProfileSliceInfo? psi))
                    {
                        int dotCount = sliceId.Count(c => c == '.');

                        SliceDiscriminator? discriminator = null;
                        StructureDefinition? contextSd = null;
                        ElementDefinition? contextElement = null;
                        ComponentDefinition? contextCd = null;
                        ExtensionData? valueExtData = null;

                        if (sd.cgTryGetSlicingParent(ed, out ElementDefinition? slicingEd))
                        {
                            // get all the elements for this slice
                            IEnumerable<ElementDefinition> sliceElements = sd.cgElementsForSlice(ed, sliceName);

                            // use our sliced element and parent to get the discriminators
                            SliceDiscriminator[] discriminators = sd.cgDiscriminatedValues(_info, slicingEd, sliceName, sliceElements).ToArray();

                            if (discriminators.Length > 1)
                            {
                                Console.WriteLine($"Found multiple discriminators for {id} in {sd.Url} ({sd.Name})");
                                break;
                                //throw new Exception($"Found multiple discriminators for {id}");
                            }

                            if (discriminators.Length == 1)
                            {
                                discriminator = discriminators[0];

                                bool isExtensionSlice = _findExtensionPathRegex.IsMatch(discriminator.Path);

                                if (isExtensionSlice)
                                {
                                    // figure out the path we want to root our C# extension on
                                    //string rootPath = discriminator.Path[..^14];
                                    string rootPath = discriminator.Path.Substring(0, discriminator.Path.LastIndexOf(".extension", StringComparison.Ordinal));
                                    //string rootId = discriminator.Id.EndsWith(".url", StringComparison.Ordinal) ? discriminator.Id[..^4] : discriminator.Id;
                                    string rootId = _findExtensionPathRegex.IsMatch(discriminator.Id)
                                        ? discriminator.Id.Substring(0, discriminator.Id.LastIndexOf(".extension", StringComparison.Ordinal))
                                        : discriminator.Id;


                                    if (sd.cgTryGetElementById(rootId, out contextElement))
                                    {
                                        contextSd = sd;
                                        contextCd = new()
                                        {
                                            Structure = sd,
                                            Element = contextElement,
                                            IsRootOfStructure = !contextElement.Path.Contains('.'),
                                        };
                                    }
                                    // resolve the root path element
                                    else if (sd.cgTryGetElementByPath(rootPath, out contextElement))
                                    {
                                        contextSd = sd;
                                        contextCd = new()
                                        {
                                            Structure = sd,
                                            Element = contextElement,
                                            IsRootOfStructure = !contextElement.Path.Contains('.'),
                                        };
                                    }
                                    else if (_info.TryFindElementByPath(rootPath, out contextSd, out contextElement))
                                    {
                                        contextCd = new()
                                        {
                                            Structure = contextSd,
                                            Element = contextElement,
                                            IsRootOfStructure = !contextElement.Path.Contains('.'),
                                        };
                                    }
                                    else if (_info.ComplexTypesByName.TryGetValue("Element", out contextSd))
                                    {
                                        contextCd = new(contextSd);
                                        contextElement = contextCd.Element;
                                    }
                                    else
                                    {
                                        // this should never happen
                                        throw new Exception($"Cannot resolve profile slice info: {sd.Name} ({sd.Url}) : {discriminator.Id}");
                                    }

                                    string extUrl = discriminator.Value.ToString() ?? string.Empty;

                                    // try to resolve this extension
                                    if (!TryGetExtensionData(extUrl, out valueExtData))
                                    {
                                        writer.WriteLineIndented($"// Could not resolve extension: {extUrl}");
                                    }
                                }
                            }
                        }


                        psi = new ProfileSliceInfo
                        {
                            Id = sliceId,
                            ParentPath = string.Join(".", pathComponents.Take(dotCount)),
                            Path = string.Join(".", pathComponents.Take(dotCount + 1)),
                            ElementName = pathComponents[dotCount],
                            SliceName = sliceName,
                            AccessorTypeName = sd.Name.ToPascalCase() + sliceName.ToPascalCase(),
                            ValueTypeName = sd.Name.ToPascalCase() + sliceName.ToPascalCase(),
                            Discriminator = discriminator,
                            Element = ed,
                            ElementThatDefinedThisSlicing = slicingEd,
                            ContextCd = contextCd,
                            ValueExtData = valueExtData,
                        };

                        currentDict[sliceId] = psi;
                    }

                    firstColonLoc = ed.ElementId.IndexOf(':', firstColonLoc + 1);
                    if (firstColonLoc != -1)
                    {
                        currentDict = psi.ChildSlicesBySliceId;
                    }
                }
            }

            WriteProfileSlices(writer, sd, profileSlices);

            //// get all elements that are 'first level' slices (i.e., not a slice of a slice)
            //ElementDefinition[] slices = sd.cgElements(skipSlices: false).Where(ed => ed.ElementId.LastIndexOf(':') > ed.ElementId.LastIndexOf('.')).ToArray();

            //foreach (ElementDefinition ed in slices)
            //{
            //    string sliceName = string.IsNullOrEmpty(ed.SliceName) ? ed.ElementId.Substring(ed.ElementId.LastIndexOf(':') + 1) : ed.SliceName;

            //    writer.WriteLineIndented($"// Slice: {ed.ElementId} ({sliceName})");

            //    // get the parent that defines the slicing
            //    if (!sd.cgTryGetSlicingParent(ed, out ElementDefinition? slicingEd))
            //    {
            //        writer.WriteLineIndented($"// Could not resolve the parent element for: {ed.ElementId}.");

            //        // TODO(ginoc): extensions can be implicitly sliced
            //        continue;
            //    }

            //    writer.WriteLineIndented($"// Slicing Parent: {slicingEd.ElementId}");

            //    // get all the elements for this slice
            //    IEnumerable<ElementDefinition> sliceElements = sd.cgElementsForSlice(ed, sliceName);

            //    // use our sliced element and parent to get the discriminators
            //    IEnumerable<SliceDiscriminator> sliceDiscriminators = sd.cgDiscriminatedValues(_info, slicingEd, sliceName, sliceElements);

            //    foreach (SliceDiscriminator discriminator in sliceDiscriminators)
            //    {
            //        writer.WriteLineIndented("// Discriminator:");
            //        writer.WriteLineIndented($"//                Id: {discriminator.Id}");
            //        writer.WriteLineIndented($"//              Type: {discriminator.Type}");
            //        writer.WriteLineIndented($"//              Path: {discriminator.Path}");
            //        writer.WriteLineIndented($"//   PostResolvePath: {discriminator.PostResolvePath}");
            //        writer.WriteLineIndented($"//             Value: {discriminator.Value}");
            //        writer.WriteLineIndented($"//         IsBinding: {discriminator.IsBinding}");
            //        writer.WriteLineIndented($"//       BindingName: {discriminator.BindingName}");

            //        //bool isExtensionSlice = discriminator.Path.EndsWith(".extension.url", StringComparison.Ordinal);
            //        bool isExtensionSlice = _findExtensionPathRegex.IsMatch(discriminator.Path);

            //        if (isExtensionSlice)
            //        {
            //            // figure out the path we want to root our C# extension on
            //            //string rootPath = discriminator.Path[..^14];
            //            string rootPath = discriminator.Path.Substring(0, discriminator.Path.LastIndexOf(".extension", StringComparison.Ordinal));
            //            //string rootId = discriminator.Id.EndsWith(".url", StringComparison.Ordinal) ? discriminator.Id[..^4] : discriminator.Id;
            //            string rootId = _findExtensionPathRegex.IsMatch(discriminator.Id)
            //                ? discriminator.Id.Substring(0, discriminator.Id.LastIndexOf(".extension", StringComparison.Ordinal))
            //                : discriminator.Id;

            //            StructureDefinition? contextSd;
            //            ElementDefinition? contextElement;
            //            ComponentDefinition contextCd;

            //            if (sd.cgTryGetElementById(rootId, out contextElement))
            //            {
            //                contextSd = sd;
            //                contextCd = new()
            //                {
            //                    Structure = sd,
            //                    Element = contextElement,
            //                    IsRootOfStructure = !contextElement.Path.Contains('.'),
            //                };
            //            }
            //            // resolve the root path element
            //            else if (sd.cgTryGetElementByPath(rootPath, out contextElement))
            //            {
            //                contextSd = sd;
            //                contextCd = new()
            //                {
            //                    Structure = sd,
            //                    Element = contextElement,
            //                    IsRootOfStructure = !contextElement.Path.Contains('.'),
            //                };
            //            }
            //            else if (_info.TryFindElementByPath(rootPath, out contextSd, out contextElement))
            //            {
            //                contextCd = new()
            //                {
            //                    Structure = contextSd,
            //                    Element = contextElement,
            //                    IsRootOfStructure = !contextElement.Path.Contains('.'),
            //                };
            //            }
            //            else if (_info.ComplexTypesByName.TryGetValue("Element", out contextSd))
            //            {
            //                contextCd = new(contextSd);
            //                contextElement = contextCd.Element;
            //            }
            //            else
            //            {
            //                // this should never happen
            //                throw new Exception($"Cannot resolve profile slice info: {sd.Name} ({sd.Url}) : {discriminator.Id}");
            //            }

            //            string extUrl = discriminator.Value.ToString() ?? string.Empty;

            //            // try to resolve this extension
            //            if (TryGetExtensionData(extUrl, out ExtensionData? extData) &&
            //                TryGetPackageData(extData.DefinitionDirective, out PackageData extPackageData))
            //            {
            //                // we have a definition we can link to
            //                writer.WriteLineIndented($"// Slice uses extension: {extPackageData.Namespace}.{extData.Name.ToPascalCase()}");

            //                // write a getter
            //                WriteProfileSliceExtensionAccessors(writer, sd, sliceName, contextCd, extData);
            //            }
            //            else
            //            {
            //                writer.WriteLineIndented($"// Could not resolve extension: {extUrl}");
            //            }
            //        }
            //        else
            //        {
            //            // figure out the path we want to root our C# extension on
            //            string rootPath = slicingEd.Path;

            //            writer.WriteLineIndented($"// Non-extension slice...");
            //        }

            //    }

            //    writer.WriteLine();

            //    //    string sliceName = !string.IsNullOrEmpty(ed.SliceName)
            //    //        ? ed.SliceName
            //    //        : ed.ElementId.Substring(ed.ElementId.LastIndexOf(':') + 1);

            //    //    // get the types for this slice
            //    //    ElementDefinition.TypeRefComponent[] edTypes = ed.cgTypes().Values.ToArray();

            //    //    foreach (ElementDefinition.TypeRefComponent edType in edTypes)
            //    //    {
            //    //        string typeName = edType.cgName();

            //    //        switch (typeName)
            //    //        {
            //    //            case "Extension":
            //    //                {
            //    //                    // iterate over each profile
            //    //                    foreach (string profileUrl in edType.Profile)
            //    //                    {
            //    //                        WriteProfileExtensionAccessors(
            //    //                            writer,
            //    //                            sd,
            //    //                            name,
            //    //                            ed,
            //    //                            sliceName,
            //    //                            profileUrl,
            //    //                            exportedExtensions);
            //    //                    }
            //    //                }
            //    //                break;
            //    //        }
            //    //    }
            //}

            CloseScope(writer);     // class

            WriteNamespaceClose(writer);
        }
    }

    private void WriteProfileSlices(
        ExportStreamWriter writer,
        StructureDefinition sd,
        Dictionary<string, ProfileSliceInfo> slices)
    {
        foreach (ProfileSliceInfo psi in slices.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
        {
            ElementDefinition ed = psi.Element;

            string sliceName = string.IsNullOrEmpty(ed.SliceName) ? ed.ElementId.Substring(ed.ElementId.LastIndexOf(':') + 1) : ed.SliceName;


            // get the parent that defines the slicing
            ElementDefinition? slicingEd = psi.ElementThatDefinedThisSlicing;
            if (slicingEd == null)
            {
                writer.WriteLineIndented($"// Slice: {ed.ElementId} ({sliceName})");
                writer.WriteLineIndented($"// Could not resolve the parent element for: {ed.ElementId}.");

                // TODO(ginoc): extensions can be implicitly sliced
                continue;
            }

            if (psi.ChildSlicesBySliceId.Count != 0)
            {
                WriteProfileAccessorClass(writer, sd, psi);
            }

            writer.WriteLineIndented($"// Slice: {ed.ElementId} ({sliceName})");
            writer.WriteLineIndented($"// Slicing Parent: {slicingEd.ElementId}");

            // use child slices to build a record type that can bundle with the primary extension
            if (psi.ChildSlicesBySliceId.Count != 0)
            {
                foreach (ProfileSliceInfo childPsi in psi.ChildSlicesBySliceId.Values)
                {
                    writer.WriteLineIndented($"// Child Slice: {childPsi.Element.ElementId} ({childPsi.SliceName})");

                    if (childPsi.ChildSlicesBySliceId.Count != 0)
                    {
                        writer.WriteLineIndented($"// Unhandled deeply-nested slice pattern in: {sd.Url} : {childPsi.Id}");
                    }
                }
            }

            // use our sliced element and parent to get the discriminators
            SliceDiscriminator? discriminator = psi.Discriminator;

            if (discriminator == null)
            {
                writer.WriteLineIndented($"// Failed to resolve discriminator for slice: {ed.ElementId}, sliced by: {slicingEd.ElementId}");
                continue;
            }

            writer.WriteLineIndented($"// Discriminator:");
            writer.WriteLineIndented($"//                Id: {discriminator.Id}");
            writer.WriteLineIndented($"//              Type: {discriminator.Type}");
            writer.WriteLineIndented($"//              Path: {discriminator.Path}");
            writer.WriteLineIndented($"//   PostResolvePath: {discriminator.PostResolvePath}");
            writer.WriteLineIndented($"//             Value: {discriminator.Value}");
            writer.WriteLineIndented($"//         IsBinding: {discriminator.IsBinding}");
            writer.WriteLineIndented($"//       BindingName: {discriminator.BindingName}");

            //bool isExtensionSlice = discriminator.Path.EndsWith(".extension.url", StringComparison.Ordinal);
            bool isExtensionSlice = _findExtensionPathRegex.IsMatch(discriminator.Path);

            if (isExtensionSlice)
            {
                // figure out the path we want to root our C# extension on
                //string rootPath = discriminator.Path[..^14];
                string rootPath = discriminator.Path.Substring(0, discriminator.Path.LastIndexOf(".extension", StringComparison.Ordinal));
                //string rootId = discriminator.Id.EndsWith(".url", StringComparison.Ordinal) ? discriminator.Id[..^4] : discriminator.Id;
                string rootId = _findExtensionPathRegex.IsMatch(discriminator.Id)
                    ? discriminator.Id.Substring(0, discriminator.Id.LastIndexOf(".extension", StringComparison.Ordinal))
                    : discriminator.Id;

                StructureDefinition? contextSd;
                ElementDefinition? contextElement;
                ComponentDefinition contextCd;

                if (sd.cgTryGetElementById(rootId, out contextElement))
                {
                    contextSd = sd;
                    contextCd = new()
                    {
                        Structure = sd,
                        Element = contextElement,
                        IsRootOfStructure = !contextElement.Path.Contains('.'),
                    };
                }
                // resolve the root path element
                else if (sd.cgTryGetElementByPath(rootPath, out contextElement))
                {
                    contextSd = sd;
                    contextCd = new()
                    {
                        Structure = sd,
                        Element = contextElement,
                        IsRootOfStructure = !contextElement.Path.Contains('.'),
                    };
                }
                else if (_info.TryFindElementByPath(rootPath, out contextSd, out contextElement))
                {
                    contextCd = new()
                    {
                        Structure = contextSd,
                        Element = contextElement,
                        IsRootOfStructure = !contextElement.Path.Contains('.'),
                    };
                }
                else if (_info.ComplexTypesByName.TryGetValue("Element", out contextSd))
                {
                    contextCd = new(contextSd);
                    contextElement = contextCd.Element;
                }
                else
                {
                    // this should never happen
                    throw new Exception($"Cannot resolve profile slice info: {sd.Name} ({sd.Url}) : {discriminator.Id}");
                }

                string extUrl = discriminator.Value.ToString() ?? string.Empty;

                // try to resolve this extension
                if (TryGetExtensionData(extUrl, out ExtensionData? extData) &&
                    TryGetPackageData(extData.DefinitionDirective, out PackageData extPackageData))
                {
                    // we have a definition we can link to
                    writer.WriteLineIndented($"// Slice uses extension: {extPackageData.Namespace}.{extData.Name.ToPascalCase()}");

                    // write a getter
                    WriteProfileSliceExtensionAccessors(writer, sd, psi, contextCd);
                }
                else
                {
                    writer.WriteLineIndented($"// Could not resolve extension: {extUrl}");
                }
            }
            else
            {
                // figure out the path we want to root our C# extension on
                string rootPath = slicingEd.Path;

                writer.WriteLineIndented($"// Non-extension slice...");
            }


            writer.WriteLine();
        }
    }

    private void WriteProfileSliceExtensionAccessors(
        ExportStreamWriter writer,
        StructureDefinition profileSd,
        ProfileSliceInfo psi,
        ComponentDefinition contextCd)
    {
        if (psi.ValueExtData == null)
        {
            throw new Exception($"Could not resolve extension data for slice: {psi.Id}");
        }

        if (!TryGetPackageData(psi.ValueExtData.DefinitionDirective, out PackageData extPackageData))
        {
            throw new Exception($"Could not get package information for extension: {psi.ValueExtData.Name} ({psi.ValueExtData.Url})");
        }

        string fnBase = profileSd.cgName().ToPascalCase() + psi.SliceName.ToPascalCase();
        string extValueType = (psi.ChildSlicesBySliceId.Count == 0) ? psi.ValueExtData.ValueTypeName : psi.ValueTypeName;

        string contextType;
        if (contextCd.IsRootOfStructure == true)
        {
            contextType = contextCd.Element.Path;
        }
        else
        {
            // we are okay using a narrower type than the extension in the profile context
            CSharpFirely2.WrittenElementInfo? ei = BuildElementInfo(contextCd.Structure.cgName(), contextCd.Element);

            contextType = ei?.PropertyType.PropertyTypeString ?? "Hl7.Fhir.Model.Element";

            //// need to find the context info
            //ExtensionContextRec? ctx = extData.Contexts.FirstOrDefault(ctx => ctx.ContextTarget?.Element.Path == contextCd.Element.Path);

            //if ((ctx == null) && contextCd.DerivesFromDataType(_info))
            //{
            //    ctx = extData.Contexts.FirstOrDefault(ctx => ctx.ContextElementInfo?.ElementType == "Hl7.Fhir.Model.DataType");
            //}

            //contextType = ctx?.ContextTypeName ?? "Hl7.Fhir.Model.Element";
        }

        if (!contextType.Contains('.'))
        {
            contextType = "Hl7.Fhir.Model." + contextType;
        }

        if (psi.ChildSlicesBySliceId.Count == 0)
        {
            if (psi.ValueExtData.IsList)
            {
                writer.WriteLineIndented($"public static IEnumerable<{extValueType}> {fnBase}Get(this {contextType} o) =>");
                writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Get(o);");

                writer.WriteLineIndented($"public static void {fnBase}Set(this {contextType} o, IEnumerable<{extValueType}>? val) =>");
                writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Set(o, val);");

            }
            else
            {
                writer.WriteLineIndented($"public static {extValueType}? {fnBase}Get(this {contextType} o) =>");
                writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Get(o);");


                writer.WriteLineIndented($"public static void {fnBase}Set(this {contextType} o, {extValueType}? val) =>");
                writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Set(o, val);");
            }

            // without sub-slices, we can write a simple getter/setter and be done

            return;
        }

        if (psi.ValueExtData.IsList)
        {
            writer.WriteLineIndented($"public static IEnumerable<{extValueType}> {fnBase}Get(this {contextType} o)");
            OpenScope(writer);
            writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Get(o);");
            CloseScope(writer);


            writer.WriteLineIndented($"public static void {fnBase}Set(this {contextType} o, IEnumerable<{extValueType}>? val)");
            OpenScope(writer);
            writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Set(o, val);");
            CloseScope(writer);

        }
        else
        {
            writer.WriteLineIndented($"public static {extValueType}? {fnBase}Get(this {contextType} o)");

            OpenScope(writer);      // function open
            writer.WriteLineIndented($"Extension? root = o.GetExtension({psi.ValueExtData.PackageNamespace}.{_classNameDefinitions}.{psi.ValueExtData.Name});");

            // pull values from the extension tree
            WriteProfileExtRecordPropertyReads(writer, psi, "root");

            writer.WriteLineIndented($"return new {extValueType}()");
            WriteProfileExtRecordCreateFromProperties(writer, psi);
            CloseScope(writer);     // function close


            writer.WriteLineIndented($"public static void {fnBase}Set(this {contextType} o, {extValueType}? val)");
            OpenScope(writer);
            writer.WriteLineIndented($"  {extPackageData.Namespace}.{_classNameExtensions}.{psi.ValueExtData.Name.ToPascalCase()}Set(o, val);");
            CloseScope(writer);
        }


    }


    //private void WriteProfileExtensionAccessors(
    //    ExportStreamWriter writer,
    //    StructureDefinition profileSd,
    //    string profileName,
    //    ElementDefinition profiledElement,
    //    string sliceName,
    //    string extensionUrl,
    //    Dictionary<string, ExtensionData> exportedExtensions)
    //{
    //    ComponentDefinition? extCd = null;
    //    bool isArray = false;

    //    _ = exportedExtensions.TryGetValue(extensionUrl, out ExtensionData? exportedExt);

    //    // try to resolve this extension
    //    if (_info.TryResolveByCanonicalUri(extensionUrl, out Resource? extR) &&
    //        (extR is StructureDefinition extSd))
    //    {
    //        extCd = new(extSd);
    //        isArray = extCd.Element.cgCardinalityMax() > 1;
    //    }

    //    StructureDefinition.ContextComponent[] contexts = extCd?.Structure.Context?.ToArray()
    //        ?? [new() { Type = StructureDefinition.ExtensionContextType.Element, Expression = "Element" }];

    //    string returnType = isArray ? "Extension?" : "IEnumerable<Extension>";
    //    string getAlias = isArray ? "GetExtension" : "GetExtensions";

    //    // strip off the .extension suffix if it exists
    //    string elementPath = profiledElement.Path.EndsWith(".extension", StringComparison.Ordinal)
    //        ? profiledElement.Path.Substring(0, profiledElement.Path.Length - ".extension".Length)
    //        : profiledElement.Path;

    //    // iterate over all the allowed contexts
    //    foreach (StructureDefinition.ContextComponent ctx in contexts)
    //    {
    //        // for now, only handle element contexts
    //        if (ctx.Type != StructureDefinition.ExtensionContextType.Element)
    //        {
    //            continue;
    //        }

    //        // check to see if everything in the path sequence is a scalar
    //        if (_info.TryResolveElementTree(elementPath, out StructureDefinition? baseSd, out ElementDefinition[] elements))
    //        {
    //            bool allScalars = elements.All(ed => ed.cgCardinalityMax() == 1);

    //            // if everything is scalar, we can base off the root type
    //            if (allScalars)
    //            {
    //                // write a getter, which returns either a single extension or an enumerable of extensions
    //                writer.WriteLineIndented($"public static {returnType} {profileName}{sliceName.ToPascalCase()}(this {baseSd.cgName().ToPascalCase()} r) =>");

    //                if (exportedExt != null)
    //                {
    //                    writer.WriteLineIndented($"  r.{getAlias}({_classNameDefinitions}.{exportedExt.Name})");
    //                }
    //                else
    //                {
    //                    writer.WriteLineIndented($"  r.{getAlias}(\"{extensionUrl}\");");
    //                }
    //            }
    //            else
    //            {
    //                // write a getter, which returns either a single extension or an enumerable of extensions
    //                writer.WriteLineIndented($"public static {returnType} {profileName}{sliceName.ToPascalCase()}(this SomeComponentDefinition c) =>");

    //                if (exportedExt != null)
    //                {
    //                    writer.WriteLineIndented($"  c.{getAlias}({_classNameDefinitions}.{exportedExt.Name})");
    //                }
    //                else
    //                {
    //                    writer.WriteLineIndented($"  c.{getAlias}(\"{extensionUrl}\");");
    //                }
    //            }
    //        }
    //        else
    //        {
    //            // write a getter, which returns either a single extension or an enumerable of extensions
    //            writer.WriteLineIndented($"public static {returnType} {profileName}{sliceName.ToPascalCase()}(this Element e) =>");

    //            if (exportedExt != null)
    //            {
    //                writer.WriteLineIndented($"  e.{getAlias}({_classNameDefinitions}.{exportedExt.Name})");
    //            }
    //            else
    //            {
    //                writer.WriteLineIndented($"  e.{getAlias}(\"{extensionUrl}\");");
    //            }
    //        }

    //        writer.WriteLine();
    //    }
    //}

    /// <summary>
    /// Closes the writers used for exporting.
    /// </summary>
    private void CloseWriters()
    {
        foreach (ExportStreamWriter writer in _definitionWriters.Values)
        {
            closeAndDispose(writer);
        }
        _definitionWriters.Clear();

        foreach (ExportStreamWriter writer in _extensionWriters.Values)
        {
            closeAndDispose(writer);
        }
        _extensionWriters.Clear();

        foreach (ExportStreamWriter writer in _valueSetWriters.Values)
        {
            closeAndDispose(writer);
        }
        _valueSetWriters.Clear();

        return;

        void closeAndDispose(ExportStreamWriter writer)
        {
            writer.CloseScope();        // class

            WriteNamespaceClose(writer);

            WriteFooter(writer);

            writer.Dispose();
        }
    }

    /// <summary>Attempts to get package data a PackageData from the given string.</summary>
    /// <param name="directive">  The directive.</param>
    /// <param name="packageData">[out] Information describing the package.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    private bool TryGetPackageData(string directive, out PackageData packageData)
    {
        string[] components = directive.StartsWith('@') ? directive[1..].Split('@') : directive.Split('@', '#');
        if (components.Length != 2)
        {
            // invalid directive
            packageData = default;
            return false;
        }

        if (_packageDataByDirective.TryGetValue(directive, out packageData))
        {
            return true;
        }

        packageData = GetPackageData(components[0], components[1]);

        return true;
    }

    /// <summary>Gets package data.</summary>
    /// <param name="packageId">     Identifier for the package.</param>
    /// <param name="packageVersion">The package version.</param>
    /// <returns>The package data.</returns>
    private PackageData GetPackageData(
        string packageId,
        string packageVersion)
    {
        string directive = $"{packageId}@{packageVersion}";

        if (_packageDataByDirective.TryGetValue(directive, out PackageData pd))
        {
            return pd;
        }

        string versionSanitized = SanitizeVersion(packageVersion);

        if (packageId.StartsWith("hl7.fhir.", StringComparison.OrdinalIgnoreCase))
        {
            string[] components = packageId.Split('.');
            string realm;
            string name;

            if (components.Length > 3)
            {
                realm = components[2].ToPascalCase();
                name = string.Join(".", components.Skip(3)).ToPascalCase();
            }
            else
            {
                realm = "Uv";
                name = packageId.ToPascalCase();
            }

            pd = new()
            {
                Key = packageId + "@" + packageVersion,
                PackageId = packageId,
                PackageVersion = packageVersion,
                Namespace = $"Hl7.Fhir.Packages.{realm}.{name}_{versionSanitized}",
                ClassPrefix = $"{realm.ToPascalCase()}{name.ToPascalCase()}",
                VersionSanitized = versionSanitized,
                FolderName = $"{realm.ToPascalCase()}{name.ToPascalCase()}_{versionSanitized}",
            };

            _packageDataByDirective.Add(directive, pd);

            return pd;
        }

        pd = new()
        {
            Key = packageId + "@" + packageVersion,
            PackageId = packageId,
            PackageVersion = packageVersion,
            Namespace = $"Hl7.Fhir.Packages.{packageId.ToPascalCase()}_{versionSanitized}",
            ClassPrefix = packageId.ToPascalCase(),
            VersionSanitized = versionSanitized,
            FolderName = $"{packageId.ToPascalCase()}_{versionSanitized}",
        };

        _packageDataByDirective.Add(directive, pd);

        return pd;
    }

    private StructureDefinitionWalker GetWalker(StructureDefinition sd)
    {
        string key = string.IsNullOrEmpty(sd.Url) ? sd.Id : sd.Url;

        if (_walkers.TryGetValue(key, out StructureDefinitionWalker? walker))
        {
            return walker;
        }

        walker = new(sd, _info);
        _walkers.Add(key, walker);

        return walker;
    }

    private ExportStreamWriter GetCanonicalUrlWriter(PackageData packageData)
    {
        if (_definitionWriters.TryGetValue(packageData.Key, out ExportStreamWriter? writer))
        {
            return writer;
        }

        string directory = Path.Combine(_options.OutputDirectory, packageData.FolderName);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        //string filename = Path.Combine(_options.OutputDirectory, $"{packageData.FilenamePrefix}.cs");
        string filename = Path.Combine(_options.OutputDirectory, packageData.FolderName, $"{_classNameDefinitions}.cs");

        FileStream stream = new(filename, FileMode.Create);
        writer = new ExportStreamWriter(stream);
        _definitionWriters[packageData.Key] = writer;

        WriteHeader(writer);

        WriteNamespaceOpen(writer, packageData.Namespace);

        WriteIndentedComment(writer, $"Definitions common to the {packageData.Key} package");

        writer.WriteLineIndented($"public static class {_classNameDefinitions}");
        writer.OpenScope();

        return writer;
    }

    private ExportStreamWriter GetExtensionWriter(PackageData packageData)
    {
        if (_extensionWriters.TryGetValue(packageData.Key, out ExportStreamWriter? writer))
        {
            return writer;
        }

        string directory = Path.Combine(_options.OutputDirectory, packageData.FolderName);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string filename = Path.Combine(_options.OutputDirectory, packageData.FolderName, $"{_classNameExtensions}.cs");

        FileStream stream = new(filename, FileMode.Create);
        writer = new ExportStreamWriter(stream);
        _extensionWriters[packageData.Key] = writer;

        WriteHeader(writer);

        WriteNamespaceOpen(writer, packageData.Namespace);

        WriteIndentedComment(writer, $"Extension accessors for the {packageData.Key} package");

        writer.WriteLineIndented($"public static class {_classNameExtensions}");
        writer.OpenScope();

        return writer;
    }

    private ExportStreamWriter GetValueSetWriter(PackageData packageData, string name)
    {
        //if (_valueSetWriters.TryGetValue(packageData.Key, out ExportStreamWriter? writer))
        //{
        //    return writer;
        //}

        string directory = Path.Combine(_options.OutputDirectory, packageData.FolderName);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        //string filename = Path.Combine(_options.OutputDirectory, packageData.FolderName, $"{_classNameValueSets}.cs");
        string filename = Path.Combine(_options.OutputDirectory, packageData.FolderName, $"{name}.cs");

        FileStream stream = new(filename, FileMode.Create);
        ExportStreamWriter? writer = new ExportStreamWriter(stream);
        //_valueSetWriters[packageData.Key] = writer;

        WriteHeader(writer);

        WriteNamespaceOpen(writer, packageData.Namespace);

        //if (string.IsNullOrEmpty(name))
        //{
        //    WriteIndentedComment(writer, $"ValueSet definitions and utility functions for the {packageData.Key} package");

        //    writer.WriteLineIndented($"public static class {_classNameValueSets}");
        //}
        //else
        //{
        //    WriteIndentedComment(writer, $"Definitions for the {name} ValueSet in the {packageData.Key} package");

        //    writer.WriteLineIndented($"public static class {name}");
        //}

        //writer.OpenScope();

        return writer;
    }


    /// <summary>Writes an extension.</summary>
    /// <param name="sd">The SD.</param>
    private void WriteExtension(
        StructureDefinition sd,
        CanonicalInfo ci,
        Dictionary<string, HashSet<string>> writtenExtensionDefinitions)
    {
        // get a definition writer
        ExportStreamWriter canonicalUrlWriter = GetCanonicalUrlWriter(ci.Package);

        // build a component for our extension
        ComponentDefinition cd = new(sd);

        // get our extension data
        ExtensionData extData = GetExtensionData(cd, ci);

        // recursively write our extension urls into definitions
        WriteCanonicalUrl(extData, canonicalUrlWriter, writtenExtensionDefinitions, ci.Name);

        // get an extension writer
        ExportStreamWriter extensionWriter = GetExtensionWriter(ci.Package);

        WriteExtensionAccessorsRecords(extData, extensionWriter);
    }

    /// <summary>Recursively writes extension urls.</summary>
    /// <param name="extData">                    Information describing the extension.</param>
    /// <param name="writer">                     The currently in-use text writer.</param>
    /// <param name="writtenExtensionDefinitions">The written extension definitions for tracking.</param>
    private void WriteCanonicalUrl(
        ExtensionData extData,
        ExportStreamWriter writer,
        Dictionary<string, HashSet<string>> writtenExtensionDefinitions,
        string rootExtensionName)
    {
        string indexKey = extData.PackageNamespace + "." + _classNameDefinitions;

        string name = string.IsNullOrEmpty(extData.ParentName) ? rootExtensionName : rootExtensionName + extData.Name;

        if (writtenExtensionDefinitions.TryGetValue(name, out HashSet<string>? writtenContexts) &&
            writtenContexts.Contains(indexKey))
        {
            return;
        }

        if (writtenContexts == null)
        {
            writtenContexts = new() { indexKey };
            writtenExtensionDefinitions.Add(name, writtenContexts);
        }
        else
        {
            writtenContexts.Add(indexKey);
        }

        writer.WriteIndentedComment(extData.Summary, isSummary: true);
        writer.WriteIndentedComment(extData.Remarks, isSummary: false, isRemarks: true);

        writer.WriteLineIndented($"public const string {name} = \"{extData.Url}\";");

        writer.WriteLine();

        // write any sub-extensions
        foreach (ExtensionData childData in extData.Children)
        {
            WriteCanonicalUrl(childData, writer, writtenExtensionDefinitions, rootExtensionName);
        }
    }

    private bool TryGetExtensionData(string url, [NotNullWhen(true)] out ExtensionData? extensionData)
    {
        // try to resolve this extension
        if (_info.TryResolveByCanonicalUri(url, out Resource? extR) &&
            _canonicalsByUrl.TryGetValue(url, out CanonicalInfo? ci) &&
            (extR is StructureDefinition extSd))
        {
            // build a component definition for this extension
            ComponentDefinition extCd = new(extSd);

            // get extension data for this extension
            extensionData = GetExtensionData(extCd, ci);

            return true;
        }

        extensionData = null;
        return false;
    }


    internal CSharpFirely2.WrittenElementInfo BuildElementInfo(
        string exportedComplexName,
        ElementDefinition element)
    {
        return CSharpFirely2.BuildElementInfo(_info, exportedComplexName, element, _valueSetInfoByUrl);
        //CSharpFirely2.WrittenElementInfo ei = CSharpFirely2.BuildElementInfo(_info, exportedComplexName, element, _valueSetInfoByUrl);

        //if (ei.PropertyType.PropertyTypeString.EndsWith("Reference"))
        //{
        //    Console.Write("");
        //}

        //return ei;
    }

    private void BuildElementOptionalFlags(
        ElementDefinition element,
        out string summary,
        out string isModifier,
        out string choice,
        out string allowedTypes,
        out string resourceReferences)
    {
        CSharpFirely2.BuildElementOptionalFlags(
            _info,
            element,
            GenSubset.Satellite,
            out summary,
            out isModifier,
            out choice,
            out allowedTypes,
            out resourceReferences);
    }

    /// <summary>
    /// Builds the component comment based on the provided ComponentDefinition.
    /// </summary>
    /// <param name="cd">The ComponentDefinition to build the comment for.</param>
    /// <returns>A tuple containing the summary and remarks of the component comment.</returns>
    private static (string summary, string? remarks) BuildComponentComment(ComponentDefinition cd)
    {
        List<string> strings = [];

        if (!string.IsNullOrEmpty(cd.Element.Short))
        {
            strings.Add(cd.Element.Short);
        }

        if (!string.IsNullOrEmpty(cd.Element.Definition) &&
            !cd.Element.Definition.Equals(cd.Element.Short, StringComparison.Ordinal) &&
            !cd.Element.Definition.Equals(cd.Element.Short + ".", StringComparison.Ordinal) &&
            (cd.Element.Definition.Count(c => c == '|') < 2))
        {
            strings.Add(cd.Element.Definition);
        }

        if (!string.IsNullOrEmpty(cd.Element.Comment) &&
            !cd.Element.Comment.Equals(cd.Element.Short, StringComparison.Ordinal) &&
            !cd.Element.Comment.Equals(cd.Element.Definition, StringComparison.Ordinal))
        {
            strings.Add(cd.Element.Comment);
        }

        switch (strings.Count)
        {
            case 0:
                return ("MISSING DESCRIPTION", null);

            case 1:
                return (strings[0], null);

            case 2:
                return (strings[0], strings[1]);

            default:
                return (strings[0], string.Join("\n", strings.Skip(1)));
        }
    }

    /// <summary>Gets extension data based on a component definition.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="cd">          The component definition for a root extension or slice (sub-extension).</param>
    /// <param name="parentExtUrl">(Optional) URL of the parent extent.</param>
    /// <returns>The extension data.</returns>
    private ExtensionData GetExtensionData(
        ComponentDefinition cd,
        CanonicalInfo rootCanonicalInfo,
        string? parentExtUrl = null,
        ComponentDefinition? parentCd = null)
    {
        (string summary, string? remarks) = BuildComponentComment(cd);
        string url;
        ElementDefinition? valueElement = null;
        List<ExtensionData> childExtensions = [];
        List<ExternalExtensionReferenceData> externalChildren = [];

        string contextString = cd.IsRootOfStructure
            ? "Allowed Contexts: " + string.Join(", ", cd.Structure.Context.Select(GetContextString))
            : "Allowed Contexts: " + (parentExtUrl ?? "Parent extension");

        ElementDefinition[] children = cd.cgGetChildren(includeDescendants: false, skipSlices: false).ToArray();
        ElementDefinition[] extSlices = children.Where(ced => ced.ElementId.StartsWith(cd.Element.Path + ".extension:", StringComparison.Ordinal)).ToArray();

        if (cd.IsRootOfStructure)
        {
            url = cd.Structure.Url;
        }
        else
        {
            ElementDefinition? urlElement = children.FirstOrDefault(ced => ced.Path.EndsWith(".url", StringComparison.Ordinal));

            if ((urlElement?.Fixed != null) &&
                (urlElement.Fixed is FhirUri extUri))
            {
                url = extUri.Value;
            }
            else
            {
                url = cd.Element.ElementId.Substring(cd.Element.ElementId.LastIndexOf(":") + 1);
            }
        }

        if (_extensionDataByUrl.TryGetValue(url, out ExtensionData? extData))
        {
            return extData;
        }

        // check for being a 'simple' extension (no sub-extensions)
        if (extSlices.Length == 0)
        {
            // get the value element
            valueElement = children.Where(ed => ed.Path.EndsWith("value[x]", StringComparison.Ordinal)).FirstOrDefault();

            // check for no value element
            if (valueElement == null)
            {
                // determine if this is a shorthand slice into another extension
                if ((cd.Element.Type.Count == 1) &&
                    (cd.Element.Type.First().Code == "Extension"))
                {
                    string externalRemarks = (remarks == null ? string.Empty : remarks + "\n") +
                        $"{contextString}\nExternal sub-extensions:\n - {string.Join("\n - ", cd.Element.Type.First().Profile)}";

                    // add a cardinality into remarks
                    externalRemarks = (externalRemarks == null ? string.Empty : externalRemarks + "\n") +
                        $"Cardinality: {cd.Element.cgCardinality()}";

                    // add our profiles
                    foreach (string profileUrl in cd.Element.Type.First().Profile)
                    {
                        string externalChildName = CleanName(cd.Element.SliceName);

                        if (string.IsNullOrEmpty(externalChildName))
                        {
                            externalChildName = GetCanonicalNameLiteral(
                                cd.Element.ElementId,
                                cd.Element.SliceName,
                                profileUrl,
                                "Extension",
                                FhirArtifactClassEnum.Extension,
                                false);
                        }

                        externalChildren.Add(new()
                        {
                            Name = externalChildName,
                            Url = profileUrl,
                            Summary = summary,
                            Remarks = externalRemarks,
                            IsRequired = !cd.Element.cgIsOptional(),
                            IsList = cd.Element.cgIsArray(),
                            ParentName = rootCanonicalInfo.Name,
                            ValueTypeName = _firelyNamespace + ".Extension",
                        });
                    }
                }
                else
                {
                    throw new Exception($"Simple extension does not have a value element: {cd.Structure.Url}:{cd.Element.ElementId}");
                }
            }
            else
            {
                // grab our types
                ElementDefinition.TypeRefComponent[] valueTypes = valueElement.cgTypes().Values.ToArray();

                string types;

                if (valueTypes.Length == 1)
                {
                    types = "Allowed Type: " + valueTypes[0].cgName();
                }
                else
                {
                    types = "Allowed Types: " + string.Join(", ", valueTypes.Select(t => t.cgName()).Distinct());
                }

                remarks = (remarks == null ? string.Empty : remarks + "\n") +
                    $"{contextString}\n{types}";
            }
        }
        else
        {
            // traverse our child elements and look for extension slices
            foreach (ElementDefinition child in extSlices)
            {
                ExtensionData childExtData = GetExtensionData(
                    new ComponentDefinition()
                    {
                        Structure = cd.Structure,
                        Element = child,
                        IsRootOfStructure = false,
                    },
                    rootCanonicalInfo,
                    parentExtUrl: cd.Structure.Url,
                    parentCd: cd);

                // check to see if this was an external reference
                if (childExtData.ExternalChildren.Length > 0)
                {
                    externalChildren.AddRange(childExtData.ExternalChildren);
                }
                else
                {
                    childExtensions.Add(childExtData);
                }
            }

            remarks = (remarks == null ? string.Empty : remarks + "\n") +
                $"{contextString}\nSub-extensions:\n - {string.Join("\n - ", childExtensions.Select(child => child.Url))}";
        }

        // add a cardinality into remarks
        remarks = (remarks == null ? string.Empty : remarks + "\n") +
            $"Cardinality: {cd.Element.cgCardinality()}";

        // add the name into remarks, if this is the root of a structure
        if (cd.IsRootOfStructure)
        {
            remarks = (remarks == null ? string.Empty : remarks + "\n") +
                $"Structure Definition Name: {cd.Structure.Name}";
        }

        string directive;
        if (_info.TryGetPackageSource(cd.Structure, out string packageId, out string packageVersion))
        {
            directive = $"{packageId}@{packageVersion}";
        }
        else
        {
            directive = $"{_info.MainPackageId}@{_info.MainPackageVersion}";
        }

        _ = TryGetPackageData(directive, out PackageData packageData);

        string name;
        string parentName;

        // check for root-level extension name conflicts
        if (cd.IsRootOfStructure)
        {
            name = rootCanonicalInfo.Name;  // cd.Structure.cgName().ToPascalCase(removeDelimiters: true);
            parentName = string.Empty;

            if (!_extensionNamesByPackageDirective.TryGetValue(directive, out HashSet<string>? usedNames))
            {
                usedNames = [];
            }

            if (usedNames.Contains(name))
            {
                int i = 1;
                string newName = name + i;

                while (usedNames.Contains(newName))
                {
                    i++;
                    newName = name + "_" + i;
                }

                name = newName;
            }

            usedNames.Add(name);
        }
        else
        {
            name = url.ToPascalCase(removeDelimiters: true);

            // we never want a choice type '[x]' suffix in our name
            if (name.EndsWith("[x]", StringComparison.Ordinal))
            {
                name = name[..^3];
            }

            parentName = rootCanonicalInfo.Name;  // cd.Structure.cgName().ToPascalCase(removeDelimiters: true);
        }

        List<ExtensionContextRec> extensionContexts = [];

        if (cd.IsRootOfStructure)
        {
            // build context information based on extension contexts
            StructureDefinition.ContextComponent[] contexts = cd.Structure.Context?.ToArray() ?? [];

            // iterate over all the allowed contexts
            foreach (StructureDefinition.ContextComponent ctx in contexts)
            {
                if (ctx.Type == StructureDefinition.ExtensionContextType.Extension)
                {
                    extensionContexts.Add(new()
                    {
                        AllowedContext = ctx,
                        ContextTarget = ctx.Expression == parentExtUrl ? parentCd : null,
                        ContextElementInfo = BuildElementInfo(string.Empty, parentCd == null ? cd.Element : parentCd.Element),
                        ContextTypeName = "Hl7.Fhir.Model.Extension",
                    });

                    continue;
                }

                switch (ctx.Type)
                {
                    case StructureDefinition.ExtensionContextType.Element:
                        // do nothing - fall through to longer processing
                        break;

                    case StructureDefinition.ExtensionContextType.Fhirpath:
                        {
                            // TODO(ginoc): need to figure out if this is worth supporting in more detail
                            Console.WriteLine($"Using Element for FHIRPath context: {cd.Structure.Name} ({cd.Structure.Url}): \"{ctx.Expression}\"");

                            //{
                            //    // get a StructureDefinition walker so we can follow the context
                            //    StructureDefinitionWalker walker = GetWalker(cd.Structure);
                            //    StructureDefinitionWalker? walkedContext = walker.Walk(ctx.Expression).FirstOrDefault();

                            //    ElementDefinition? contextEd = walkedContext?.Current.Current;

                            //    if (contextEd == null)
                            //    {
                            //        Console.WriteLine($"Could not resolve context for {cd.Structure.Url} context {ctx.Type}:{ctx.Expression}");
                            //        continue;
                            //    }
                            //}

                            ComponentDefinition? targetCd = null;

                            if (_info.ComplexTypesByName.TryGetValue("Element", out StructureDefinition? elementSd))
                            {
                                targetCd = new(elementSd);
                            }

                            extensionContexts.Add(new()
                            {
                                AllowedContext = new StructureDefinition.ContextComponent()
                                {
                                    Type = StructureDefinition.ExtensionContextType.Element,
                                    Expression = "Element",
                                },
                                ContextTarget = targetCd,
                                ContextElementInfo = BuildElementInfo(string.Empty, parentCd == null ? cd.Element : parentCd.Element),
                                ContextTypeName = "Hl7.Fhir.Model.Element",
                            });

                            continue;
                        }
                        //break;

                    case StructureDefinition.ExtensionContextType.Extension:
                        {
                            ComponentDefinition? targetCd = null;

                            if (_info.TryResolveByCanonicalUri(ctx.Expression, out Resource? targetResolved) &&
                                targetResolved is StructureDefinition targetExtension)
                            {
                                targetCd = new(targetExtension);
                            }

                            CSharpFirely2.WrittenElementInfo? ei = BuildElementInfo(string.Empty, targetCd == null ? cd.Element : targetCd.Element);

                            extensionContexts.Add(new()
                            {
                                AllowedContext = new StructureDefinition.ContextComponent()
                                {
                                    Type = StructureDefinition.ExtensionContextType.Extension,
                                    Expression = ctx.Expression,
                                },
                                ContextTarget = targetCd,
                                ContextElementInfo = ei,
                                ContextTypeName = targetCd?.IsRootOfStructure == true
                                    ? targetCd.Element.Path
                                    : ei?.PropertyType.PropertyTypeString ?? "Hl7.Fhir.Model.Extension",
                            });

                            // nothing more to do for this context
                            continue;
                        }
                }

                // for now, only handle element contexts
                if (ctx.Type != StructureDefinition.ExtensionContextType.Element)
                {
                    Console.WriteLine($"Skipping {cd.Structure.Url} context {ctx.Type}:{ctx.Expression}");
                    continue;
                }

                // resolve the path the extension is attached to
                if (_info.TryResolveElementTree(ctx.Expression, out StructureDefinition? baseSd, out ElementDefinition[] elements) &&
                    (elements.Length > 0))
                {
                    ElementDefinition contextElement = elements.Last();

                    ComponentDefinition contextCd = new()
                    {
                        Structure = baseSd,
                        Element = contextElement,
                        IsRootOfStructure = !contextElement.Path.Contains('.') , // elements.Length == 1,
                    };

                    CSharpFirely2.WrittenElementInfo? ei = BuildElementInfo(contextCd.Structure.cgName(), contextCd.Element);

                    extensionContexts.Add(new()
                    {
                        AllowedContext = ctx,
                        ContextTarget = contextCd,
                        ContextElementInfo = ei,
                        ContextTypeName = contextCd?.IsRootOfStructure == true
                            ? contextCd.Element.Path
                            : ei?.PropertyType.PropertyTypeString ?? "Hl7.Fhir.Model.Element",
                    });
                }
            }

            // if there is a context for 'DataType' (forced by choice type), we want to add a single context for all 'DataType' types
            if (extensionContexts.Any(ExtensionContextIsExplicitlyDataType))
            {
                extensionContexts.RemoveAll(ExtensionContextDerivesFromDataType);

                // generate a fake element information
                extensionContexts.Add(new()
                {
                    AllowedContext = new StructureDefinition.ContextComponent()
                    {
                        Type = StructureDefinition.ExtensionContextType.Element,
                        Expression = "DataType",
                    },
                    ContextTarget = null,
                    ContextElementInfo = new("", "", "", ComplexTypeReference.DataTypeReference, null),
                    //ContextElementInfo = new CSharpFirely2.WrittenElementInfo()
                    //{
                    //    ElementType = "Hl7.Fhir.Model.DataType",
                    //    PropertyType = "Hl7.Fhir.Model.DataType",
                    //    IsPrimitive = false,
                    //    IsChoice = true,
                    //    FhirElementName = "",
                    //    IsList = false,
                    //    IsCodedEnum = false,
                    //},
                    ContextTypeName = "Hl7.Fhir.Model.DataType",
                });
            }

            // if there is a context for 'Element' (e.g., all elements), we want to remove any other element-based contexts
            if (extensionContexts.Any(ExtensionContextIsExplicitlyElement))
            {
                extensionContexts.RemoveAll(ExtensionContextDerivesFromDataType);

                ComponentDefinition? targetCd = null;

                if (_info.ComplexTypesByName.TryGetValue("Element", out StructureDefinition? elementSd))
                {
                    targetCd = new(elementSd);
                }

                extensionContexts.Add(new()
                {
                    AllowedContext = new StructureDefinition.ContextComponent()
                    {
                        Type = StructureDefinition.ExtensionContextType.Element,
                        Expression = "Element",
                    },
                    ContextTarget = targetCd,
                    ContextElementInfo = BuildElementInfo(string.Empty, parentCd == null ? cd.Element : parentCd.Element),
                    ContextTypeName = "Hl7.Fhir.Model.Element",
                });
            }

            // check for contexts that use element linking, which result in the same element
            IEnumerable<IGrouping<string, ExtensionContextRec>> groupedContexts = extensionContexts.GroupBy(ctx => ctx.ContextTypeName);
            foreach (var groupedContext in groupedContexts)
            {
                if (groupedContext.Count() > 1)
                {
                    // remove all but the first context
                    foreach (ExtensionContextRec ctx in groupedContext.Skip(1))
                    {
                        extensionContexts.Remove(ctx);
                    }
                }
            }

            // if there were no contexts, add an "Element" context
            if (contexts.Length == 0)
            {
                ComponentDefinition? targetCd = null;

                if (_info.ComplexTypesByName.TryGetValue("Element", out StructureDefinition? elementSd))
                {
                    targetCd = new(elementSd);
                }

                extensionContexts.Add(new()
                {
                    AllowedContext = new StructureDefinition.ContextComponent()
                    {
                        Type = StructureDefinition.ExtensionContextType.Element,
                        Expression = "Element",
                    },
                    ContextTarget = targetCd,
                    ContextElementInfo = BuildElementInfo(string.Empty, parentCd == null ? cd.Element : parentCd.Element),
                    ContextTypeName = "Hl7.Fhir.Model.Element",
                });
            }
        }
        else
        {
            // context is parent extension
            extensionContexts.Add(new()
            {
                AllowedContext = new StructureDefinition.ContextComponent()
                {
                    Type = StructureDefinition.ExtensionContextType.Extension,
                    Expression = parentExtUrl,
                },
                ContextTarget = parentCd,
                ContextElementInfo = BuildElementInfo(string.Empty, parentCd == null ? cd.Element : parentCd.Element),
                ContextTypeName = "Hl7.Fhir.Model.Extension",
            });
        }

        CSharpFirely2.WrittenElementInfo? valueElementInfo = valueElement == null ? null : BuildElementInfo(string.Empty, valueElement);

        string valueTypeName;

        if (valueElementInfo != null)
        {
            ListTypeReference? extLTR = valueElementInfo.PropertyType is ListTypeReference ltr ? ltr : null;
            _ = CSharpFirely2.TryGetPrimitiveType(valueElementInfo.PropertyType, out PrimitiveTypeReference? extPTR);

            //valueTypeName = valueElementInfo.IsPrimitive ? valueElementInfo.PrimitiveHelperType! : valueElementInfo.ElementType!;
            //valueTypeName = extPTR?.ConveniencePropertyTypeString ?? valueElementInfo.PropertyType.PropertyTypeString;
            valueTypeName = extLTR?.Element.PropertyTypeString ?? valueElementInfo.PropertyType.PropertyTypeString;
        }
        else
        {
            valueTypeName = string.IsNullOrEmpty(parentName)
                ? name + _recordClassSuffix
                : parentName + name + _recordClassSuffix;
        }

        // create our extension data
        extData = new ExtensionData()
        {
            Component = cd,
            Name = name,
            Url = url,
            Summary = summary,
            Remarks = remarks,
            IsList = cd.Element.cgCardinalityMax() != 1,
            ParentName = parentName,
            DefinitionDirective = directive,
            PackageNamespace = packageData.Namespace,
            Contexts = extensionContexts.ToArray(),
            ValueElement = valueElement,
            ElementInfo = valueElementInfo,
            Children = childExtensions.ToArray(),
            ExternalChildren = externalChildren.ToArray(),
            ValueTypeName = valueTypeName,
        };

        _extensionDataByUrl.Add(url, extData);

        return extData;
    }

    private bool ExtensionContextIsExplicitlyDataType(ExtensionContextRec ctx) => ctx.ContextElementInfo?.PropertyType.PropertyTypeString switch
    {
        "Hl7.Fhir.Model.DataType" => true,
        "DataType" => true,
        _ => false,
    };

    private bool ExtensionContextDerivesFromDataType(ExtensionContextRec ctx)
    {
        if (ExtensionContextIsExplicitlyDataType(ctx))
        {
            return true;
        }

        if (ctx.ContextTarget == null)
        {
            return false;
        }

        string btName = ctx.ContextTarget.cgBaseTypeName(_info, false);

        return _info.PrimitiveTypesByName.ContainsKey(btName) || _info.ComplexTypesByName.ContainsKey(btName);
    }

    private bool ExtensionContextIsExplicitlyElement(ExtensionContextRec ctx) => ctx.ContextElementInfo?.PropertyType.PropertyTypeString switch
    {
        "Hl7.Fhir.Model.Element" => true,
        "Element" => true,
        _ => false,
    };

    private bool ExtensionContextDerivesFromElement(ExtensionContextRec ctx)
    {
        if (ExtensionContextIsExplicitlyElement(ctx))
        {
            return true;
        }

        if (ctx.ContextTarget == null)
        {
            return false;
        }

        string btName = ctx.ContextTarget.cgBaseTypeName(_info, false);

        return !_info.PrimitiveTypesByName.ContainsKey(btName) && !_info.ComplexTypesByName.ContainsKey(btName);
    }


    /// <summary>Gets a comment-formatted string for an Extension context.</summary>
    /// <param name="ctx">The context.</param>
    /// <returns>The context string.</returns>
    private string GetContextString(StructureDefinition.ContextComponent ctx) => ctx switch
    {
        { Type: StructureDefinition.ExtensionContextType.Fhirpath } => $"FhirPath({ctx.Expression})",
        { Type: StructureDefinition.ExtensionContextType.Element } => ctx.Expression,
        { Type: StructureDefinition.ExtensionContextType.Extension } => $"Extension({ctx.Expression})",
        _ => $"{ctx.Type}({ctx.Expression})"
    };

    /// <summary>Sanitize a version string.</summary>
    /// <param name="version">The version.</param>
    /// <returns>A string.</returns>
    private string SanitizeVersion(string version)
    {
        return version.Replace('.', '_').Replace('-', '_');
    }

    /// <summary>Writes the namespace open.</summary>
    private void WriteNamespaceOpen(ExportStreamWriter writer, string ns)
    {
        writer.WriteLineIndented("namespace " + ns);
        OpenScope(writer);
    }

    /// <summary>Writes the namespace close.</summary>
    private void WriteNamespaceClose(ExportStreamWriter writer)
    {
        CloseScope(writer);
    }

    private void WriteFooter(ExportStreamWriter writer)
    {
        // do not override nullability settings of project
        writer.WriteLine("#nullable restore");

        writer.WriteIndentedComment("end of file", singleLine: true);
    }

    private void WriteHeader(ExportStreamWriter writer, string[]? additionalUsingLibs = null)
    {
        WriteGenerationComment(writer);

        writer.WriteLineIndented("using System;");
        writer.WriteLineIndented("using System.Collections.Generic;");
        writer.WriteLineIndented("using System.Linq;");
        writer.WriteLineIndented("using System.Runtime.Serialization;");
        writer.WriteLineIndented("using Hl7.Fhir.Model;");

        if (additionalUsingLibs != null)
        {
            foreach (string usingStatement in additionalUsingLibs)
            {
                writer.WriteLineIndented("using " + usingStatement + ";");
            }
        }

        writer.WriteLine(string.Empty);

        //WriteCopyright(writer);
    }

    /// <summary>Writes the generation comment.</summary>
    private void WriteGenerationComment(ExportStreamWriter writer)
    {
        writer.WriteLineIndented("// <auto-generated/>");
        writer.WriteLineIndented($"// Generated via export of: {string.Join(", ", _info.Manifests.Select(kvp => kvp.Key))}");

        if (_options.ExportKeys.Count != 0)
        {
            string restrictions = string.Join("|", _options.ExportKeys);
            writer.WriteLine($"  // Restricted to: {restrictions}");
        }

        // we want to use nullable annotations
        writer.WriteLine("#nullable enable");

        writer.WriteLine(string.Empty);
    }

    /// <summary>Writes the copyright.</summary>
    private void WriteCopyright(ExportStreamWriter writer)
    {
        writer.WriteLineIndented("/*");
        writer.WriteLineIndented("  Copyright (c) 2011+, HL7, Inc.");
        writer.WriteLineIndented("  All rights reserved.");
        writer.WriteLineIndented("  ");
        writer.WriteLineIndented("  Redistribution and use in source and binary forms, with or without modification, ");
        writer.WriteLineIndented("  are permitted provided that the following conditions are met:");
        writer.WriteLineIndented("  ");
        writer.WriteLineIndented("   * Redistributions of source code must retain the above copyright notice, this ");
        writer.WriteLineIndented("     list of conditions and the following disclaimer.");
        writer.WriteLineIndented("   * Redistributions in binary form must reproduce the above copyright notice, ");
        writer.WriteLineIndented("     this list of conditions and the following disclaimer in the documentation ");
        writer.WriteLineIndented("     and/or other materials provided with the distribution.");
        writer.WriteLineIndented("   * Neither the name of HL7 nor the names of its contributors may be used to ");
        writer.WriteLineIndented("     endorse or promote products derived from this software without specific ");
        writer.WriteLineIndented("     prior written permission.");
        writer.WriteLineIndented("  ");
        writer.WriteLineIndented("  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\" AND ");
        writer.WriteLineIndented("  ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED ");
        writer.WriteLineIndented("  WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. ");
        writer.WriteLineIndented("  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, ");
        writer.WriteLineIndented("  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT ");
        writer.WriteLineIndented("  NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR ");
        writer.WriteLineIndented("  PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, ");
        writer.WriteLineIndented("  WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ");
        writer.WriteLineIndented("  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE ");
        writer.WriteLineIndented("  POSSIBILITY OF SUCH DAMAGE.");
        writer.WriteLineIndented("  ");
        writer.WriteLineIndented("*/");
        writer.WriteLine(string.Empty);
    }

}
