// <copyright file="ShorthandIG.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text;
using Fhir.CodeGen.Packages.Models;
using Hl7.Fhir.Model;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Polyfill;
using Fhir.CodeGen.Common.Utils;
using static Fhir.CodeGen.Common.Extensions.FhirNameConventionExtensions;

namespace Fhir.CodeGen.Lib.Language.Shorthand;

public class ShorthandIG : ILanguage
{
    /// <summary>(Immutable) Name of the language.</summary>
    private const string LanguageName = "ShorthandIg";

    /// <summary>Gets the language name.</summary>
    public string Name => LanguageName;

    /// <summary>Gets the FHIR primitive type map.</summary>
    public Dictionary<string, string> FhirPrimitiveTypeMap => [];

    /// <summary>Gets a value indicating whether this language is idempotent.</summary>
    public bool IsIdempotent => true;

    /// <summary>FHIR information we are exporting.</summary>
    private DefinitionCollection _info = null!;

    private ShorthandOptions _options = null!;

    public Type ConfigType => typeof(ShorthandOptions);

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
        public required string Hyphenated { get; init; }
        public required string SnakeCased { get; init; }
        public required string PascalCased { get; init; }
        public required string VersionSanitized { get; init; }
        public required string FolderName { get; init; }
    }

    private Dictionary<string, ExportStreamWriter> _definitionWriters = [];
    private Dictionary<string, CanonicalInfo> _canonicalsByUrl = [];
    private Dictionary<string, PackageData> _packageDataByDirective = [];
    private Dictionary<string, string> _valueSetNameMaps = [];

    private static readonly HashSet<string> _nameComponentsToRemove = new(StringComparer.OrdinalIgnoreCase)
    {
        "extensions",
        "ext",
        "www",
        "org",
        "com",
    };

    /// <summary>Structures to skip generating.</summary>
    private static readonly HashSet<string> _exclusionSet =
    [
        /* UCUM is used as a required binding in a codeable concept. Since we do not
         * use enums in this situation, it is not useful to generate this valueset
         */
        "http://hl7.org/fhir/ValueSet/ucum-units",

        /* R5 made Resource.language a required binding to all-languages, which contains
         * all of bcp:47 and is listed as infinite. This is not useful to generate.
         * Note that in R5, many elements that are required to all-languages also have bound
         * starter value sets.  TODO: consider if we want to generate constants for those.
         */
        "http://hl7.org/fhir/ValueSet/all-languages",

        /* MIME types are infinite, so we do not want to generate these.
         * Note that in R5, many elements that are required to MIME type also have bound
         * starter value sets.  TODO: consider if we want to generate constants for those.
         */
        "http://hl7.org/fhir/ValueSet/mimetypes",
    ];

    private static readonly Dictionary<string, string> _enumNamesOverride = new()
    {
        ["http://hl7.org/fhir/ValueSet/characteristic-combination"] = "CharacteristicCombinationCode",
        ["http://hl7.org/fhir/ValueSet/claim-use"] = "ClaimUseCode",
        ["http://hl7.org/fhir/ValueSet/content-type"] = "ContentTypeCode",
        ["http://hl7.org/fhir/ValueSet/exposure-state"] = "ExposureStateCode",
        ["http://hl7.org/fhir/ValueSet/verificationresult-status"] = "StatusCode",
        ["http://terminology.hl7.org/ValueSet/v3-Confidentiality"] = "ConfidentialityCode",
        ["http://hl7.org/fhir/ValueSet/variable-type"] = "VariableTypeCode",
        ["http://hl7.org/fhir/ValueSet/group-measure"] = "GroupMeasureCode",
        ["http://hl7.org/fhir/ValueSet/coverage-kind"] = "CoverageKindCode",
        ["http://hl7.org/fhir/ValueSet/fhir-types"] = "FHIRAllTypes",
        ["http://loinc.org"] = "LNC",
        ["http://snomed.info/sct"] = "SCT",
        ["http://unitsofmeasure.org"] = "UCUM",
        ["http://hl7.org/fhir/sid/icd-10-cm"] = "ICD10CM",
        ["http://www.cms.gov/Medicare/Coding/ICD10"] = "ICD10PCS",
        ["http://www.nlm.nih.gov/research/umls/rxnorm"] = "RXN",
        ["http://www.ama-assn.org/go/cpt"] = "CPT",
        ["http://terminology.hl7.org/CodeSystem/icd-o-3"] = "ICDO3",
        ["http://terminology.hl7.org/CodeSystem/umls"] = "UMLS",
    };

    private static readonly Dictionary<string, string> _explicitCanonicalAliasNames = new()
    {
        ["http://hl7.org/fhir/ValueSet/fhir-types"] = "FHIRAllTypes",
        ["http://loinc.org"] = "LNC",
        ["http://snomed.info/sct"] = "SCT",
        ["http://unitsofmeasure.org"] = "UCUM",
        ["http://hl7.org/fhir/sid/icd-10-cm"] = "ICD10CM",
        ["http://www.cms.gov/Medicare/Coding/ICD10"] = "ICD10PCS",
        ["http://www.nlm.nih.gov/research/umls/rxnorm"] = "RXN",
        ["http://www.ama-assn.org/go/cpt"] = "CPT",
        ["http://terminology.hl7.org/CodeSystem/icd-o-3"] = "ICDO3",
        ["http://terminology.hl7.org/CodeSystem/umls"] = "UMLS",
    };

    private const int _aliasNameWidth = -70;
    private const int _urlWidth = -100;

    public void Export(object untypedOptions, DefinitionCollection info)
    {
        if (untypedOptions is not ShorthandOptions options)
        {
            throw new ArgumentException("Options must be of type ShorthandOptions", nameof(untypedOptions));
        }

        // check to see if there are multiple versions of FHIR loaded - IG generation can only work with a single one
        (string id, FhirSemVer version)[] fhirCorePackageIds = info.Manifests.Keys
            .Where(key => FhirPackageUtils.PackageIsFhirRelease(key.id))
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

        // traverse our canonicals, sorted by name
        foreach (CanonicalInfo ci in canonicalsByName.Values.OrderBy(ci => ci.Name))
        {
            // write the canonical URL
            WriteCanonicalUrl(ci.Conformance, ci.Package, ci.Name);
            writtenCanonicalUrls.Add(ci.Url);
        }

        CloseWriters();
    }

    private void CloseWriters()
    {
        foreach (ExportStreamWriter writer in _definitionWriters.Values)
        {
            CloseAndDispose(writer);
        }
        _definitionWriters.Clear();

        return;

        void CloseAndDispose(ExportStreamWriter writer)
        {
            writer.Flush();
            writer.Dispose();
        }
    }


    /// <summary>Writes the canonical URL of a conformance resource.</summary>
    /// <param name="sd">The SD.</param>
    private void WriteCanonicalUrl(IConformanceResource cr, PackageData packageData, string name, ExportStreamWriter? writer = null)
    {
        // get a definition writer
        writer ??= GetCanonicalWriter(packageData);

        string summary = string.IsNullOrEmpty(cr.Description)
            ? "Name: " + cr.Name
            : cr.Description.Replace("\r", string.Empty).Replace("\n", string.Empty);

        string remarks = cr.Purpose?.Replace("\r", string.Empty).Replace("\n", string.Empty) ?? string.Empty;

        if (cr is ValueSet vs)
        {
            string[] referencedCodeSystems = vs.cgReferencedCodeSystems().ToArray();
            if (referencedCodeSystems.Length == 1)
            {
                remarks = (string.IsNullOrEmpty(remarks) ? string.Empty : remarks + " ") +
                    "System: " + referencedCodeSystems.First();
            }
            else
            {
                remarks += (string.IsNullOrEmpty(remarks) ? string.Empty : remarks + " ") +
                    "Systems: " + referencedCodeSystems.Length + " -" + string.Join(" -", referencedCodeSystems);
            }
        }

        writer.WriteLineIndented($"Alias: ${name,_aliasNameWidth} = {cr.Url,_urlWidth}\t// {summary} {remarks}");

        if (_explicitCanonicalAliasNames.TryGetValue(cr.Url, out string? additionalName))
        {
            writer.WriteLineIndented($"Alias: ${additionalName,_aliasNameWidth} = {cr.Url,_urlWidth}\t\t// {summary} {remarks}");
        }
    }

    /// <summary>
    /// Gets the export stream writer for writing canonical URLs.
    /// </summary>
    /// <param name="packageData">The package data.</param>
    /// <param name="fileKey">The file key.</param>
    /// <param name="name">The name of the export stream writer.</param>
    /// <param name="description">The description of the export stream writer.</param>
    /// <returns>The export stream writer.</returns>
    private ExportStreamWriter GetCanonicalWriter(
        PackageData packageData,
        string? fileKey = null,
        string? name = null,
        string? description = null)
    {
        string key = packageData.Key + (fileKey ?? string.Empty);

        if (_definitionWriters.TryGetValue(packageData.Key, out ExportStreamWriter? writer))
        {
            return writer;
        }

        string directory;
        string filename;

        if (string.IsNullOrEmpty(fileKey))
        {
            filename = Path.Combine(_options.OutputDirectory, packageData.FolderName + ".fsh");
        }
        else
        {
            directory = Path.Combine(_options.OutputDirectory, packageData.FolderName);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            filename = Path.Combine(_options.OutputDirectory, packageData.FolderName, $"{fileKey}.fsh");
        }

        FileStream stream = new(filename, FileMode.Create);
        writer = new ExportStreamWriter(stream);
        _definitionWriters[packageData.Key] = writer;

        // default to info about the package if we do not have a name and description
        if (string.IsNullOrEmpty(fileKey))
        {
            name ??= $"{packageData.PackageId} Canonical URL aliases";
            description ??= $"Canonical URL aliases for {packageData.PackageId} version {packageData.PackageVersion}";
        }

        WriteHeader(writer, name, description);

        return writer;
    }

    private void WriteHeader(ExportStreamWriter writer, string? name = null, string? description = null)
    {
        string generationNote = $"Generated {DateTime.Now.ToString("yyyy-mm-dd HH:mm")} by fhir-codegen: https://github.com/FHIR/fhir-codegen";

        if (!string.IsNullOrEmpty(name))
        {
            writer.WriteLineIndented($"// @Name: {name}");
        }

        if (!string.IsNullOrEmpty(description))
        {
            writer.WriteLineIndented($"// @Description: {description} {generationNote}");
        }
        else
        {
            writer.WriteLineIndented($"// {generationNote}");
        }

        writer.WriteLine(string.Empty);
    }

    /// <summary>
    /// Writes an indented comment to the export stream writer.
    /// </summary>
    /// <param name="writer">The export stream writer.</param>
    /// <param name="value">The comment value.</param>
    /// <param name="singleLine">Indicates whether the comment should be written as a single line.</param>
    public static void WriteIndentedComment(
        ExportStreamWriter writer,
        string value,
        bool singleLine = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        string comment = value
            .Replace('\r', '\n')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n\n", "\n", StringComparison.Ordinal);

        string[] lines = comment.Split('\n');

        if ((!singleLine) && (lines.Length > 1))
        {
            writer.WriteLineIndented("/*");
            foreach (string line in lines)
            {
                writer.WriteIndented(" * ");
                writer.WriteLine(line);
            }
            writer.WriteLineIndented(" */");
        }
        else
        {
            foreach (string line in lines)
            {
                writer.WriteIndented("// ");
                writer.WriteLine(line);
            }
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
                Hyphenated = $"{realm}-{name}-{versionSanitized}",
                SnakeCased = $"{realm}_{name}_{versionSanitized}",
                PascalCased = $"{realm.ToPascalCase()}{name.ToPascalCase()}",
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
            Hyphenated = $"{packageId.ToPascalCase()}-{versionSanitized}",
            SnakeCased = $"{packageId.ToPascalCase()}_{versionSanitized}",
            PascalCased = packageId.ToPascalCase(),
            VersionSanitized = versionSanitized,
            FolderName = $"{packageId.ToPascalCase()}_{versionSanitized}",
        };

        _packageDataByDirective.Add(directive, pd);

        return pd;
    }

    /// <summary>Sanitize a version string.</summary>
    /// <param name="version">The version.</param>
    /// <returns>A string.</returns>
    private string SanitizeVersion(string version)
    {
        return version.Replace('.', '_').Replace('-', '_');
    }

    private static string ConvertEnumValue(string name)
    {
        // remove a leading underscore
        if (name.StartsWith('_'))
        {
            name = name.Substring(1);
        }

        // expand common literals
        switch (name)
        {
            case "=":
                return "Equal";
            case "!=":
                return "NotEqual";
            case "<":
                return "LessThan";
            case "<=":
                return "LessOrEqual";
            case ">=":
                return "GreaterOrEqual";
            case ">":
                return "GreaterThan";
        }

        string[] bits = name.Split([' ', '-']);
        string result = string.Empty;
        foreach (string bit in bits)
        {
            result += bit.Substring(0, 1).ToUpperInvariant();
            result += bit.Substring(1);
        }

        result = result
            .Replace('.', '_')
            .Replace(')', '_')
            .Replace('(', '_')
            .Replace('/', '_')
            .Replace('+', '_');

        if (char.IsDigit(result[0]))
        {
            result = "N" + result;
        }

        return result;
    }
    private static string CleanName(string name)
    {
        return FhirSanitizationUtils.SanitizeForProperty(name, [], NamingConvention.PascalCase);
    }

}
