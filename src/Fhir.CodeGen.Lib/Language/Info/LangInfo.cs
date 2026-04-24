// <copyright file="LangInfo.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Extensions;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.Packaging;
using static Fhir.CodeGen.Lib.Language.Info.LangInfo;
using static Fhir.CodeGen.Common.Extensions.FhirNameConventionExtensions;
using System.Collections.Generic;


#if NETSTANDARD2_0
using Fhir.CodeGen.Common.Polyfill;
#endif

namespace Fhir.CodeGen.Lib.Language.Info;

/// <summary>Class used to export package/specification information.</summary>
public class LangInfo : ILanguage
{
    /// <summary>Values that represent Information formats.</summary>
    public enum InfoFormat
    {
        Text,
        Json,
    }

    /// <summary>Gets the type of the configuration.</summary>
    public Type ConfigType => typeof(InfoOptions);

    /// <summary>An information options.</summary>
    public class InfoOptions : ConfigGenerate
    {
        /// <summary>Gets or sets the file format.</summary>
        [ConfigOption(
            ArgName = "--format",
            Description = "File format to export.")]
        public LangInfo.InfoFormat FileFormat { get; set; } = LangInfo.InfoFormat.Text;

        private static ConfigurationOption FileFormatParameter { get; } = new()
        {
            Name = "FileFormat",
            DefaultValue = LangInfo.InfoFormat.Text,
            CliOption = new System.CommandLine.Option<LangInfo.InfoFormat>("--format", "File format to export.")
            {
                Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
                Required = false,
            },
        };

        private static readonly ConfigurationOption[] _options = 
        [
            FileFormatParameter,
        ];

        /// <summary>
        /// Gets the configuration options for the current instance and its base class.
        /// </summary>
        /// <returns>An array of configuration options.</returns>
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
                    case "FileFormat":
                        FileFormat = GetOpt(parseResult, opt, FileFormat);
                        break;
                }
            }
        }

        /// <summary>Gets or sets the write stream to use.</summary>
        public Stream? WriteStream { get; set; } = null;
    }

    /// <summary>Gets the language name.</summary>
    public string Name => "Info";

    /// <summary>Dictionary mapping FHIR primitive types to language equivalents.</summary>
    private static readonly Dictionary<string, string> _primitiveTypeMap = [];

    /// <summary>Gets the FHIR primitive type map.</summary>
    public Dictionary<string, string> FhirPrimitiveTypeMap => _primitiveTypeMap;

    public bool IsIdempotent => true;

    /// <summary>The currently in-use text writer.</summary>
    private ExportStreamWriter _writer = null!;

    /// <summary>The definitions.</summary>
    private DefinitionCollection _definitions = null!;

    /// <summary>Exports the given configuration.</summary>
    /// <param name="config">     The configuration.</param>
    /// <param name="definitions">The definitions to export.</param>
    /// <param name="writeStream">(Optional) Stream to write data to.</param>
    public void Export(
        object untypedConfig,
        DefinitionCollection definitions)
    {
        if (untypedConfig is not InfoOptions config)
        {
            throw new ArgumentException("Invalid configuration type");
        }

        _definitions = definitions;

        // create a filename for writing (single file for now)
        string filename = string.IsNullOrEmpty(config.OutputFilename)
            ? Path.Combine(config.OutputDirectory, $"Info_{definitions.FhirSequence.ToRLiteral()}.txt")
            : Path.Combine(config.OutputDirectory, config.OutputFilename);

        using (config.WriteStream == null
            ? _writer = new ExportStreamWriter(new FileStream(filename, FileMode.Create), System.Text.Encoding.UTF8, 1024, true)
            : _writer = new ExportStreamWriter(config.WriteStream, System.Text.Encoding.UTF8, 1024, true))
        {
            WriteHeader(config, definitions);

            WriteStructures(definitions.PrimitiveTypesByName.Values, "Primitive Types");
            WriteStructures(definitions.ComplexTypesByName.Values, "Complex Types");
            WriteStructures(definitions.ResourcesByName.Values, "Resources");

            WriteStructures(definitions.InterfacesByName.Values, "Interfaces");

            WriteStructures(definitions.ProfilesByUrl.Values, "Profiles");

            WriteOperations(definitions.SystemOperations.Values, WriteLevelCodes.System, "System Operations");

            WriteSearchParameters(definitions.GlobalSearchParameters.Values.OrderBy(sp => sp.Code), "All Resource Parameters");

            WriteFhirQueryParameters(definitions.SearchResultParameters.Values.OrderBy(sr => sr.Name), "Search Result Parameters");
            WriteFhirQueryParameters(definitions.HttpParameters.Values.OrderBy(qp => qp.Name), "All Interaction (HTTP) Parameters");

            WriteValueSets(definitions.ValueSetsByVersionedUrl.Values, "Value Sets");
            WriteUnresolvedValueSets(definitions.BoundExternalValueSets(), "External Value Set Binding References");

            //WriteFooter();
        }
    }

    /// <summary>Writes a value sets.</summary>
    /// <param name="valueSets"> Sets the value belongs to.</param>
    /// <param name="headerHint">(Optional) The header hint.</param>
    private void WriteValueSets(
        IEnumerable<ValueSet> valueSets,
        string headerHint = "")
    {
        if (!string.IsNullOrEmpty(headerHint))
        {
            _writer.WriteLineIndented($"{headerHint}: {valueSets.Count()}");
        }

        foreach (ValueSet vs in valueSets.OrderBy(v => v.Url))
        {
            string snip = BuildStandardSnippet(vs.cgStandardStatus(), vs.cgMaturityLevel(), null);

            _writer.WriteLineIndented($"- ValueSet: {vs.Url}|{vs.Version}{snip} ({vs.Name})");

            _writer.IncreaseIndent();

            IEnumerable<StructureElementCollection> coreBindings = _definitions.CoreBindingsForVs(vs.Url);
            BindingStrength? strongestBinding = _definitions.StrongestBinding(coreBindings);
            IReadOnlyDictionary<string, BindingStrength> bindingStrengthByType = _definitions.BindingStrengthByType(coreBindings);
            if (coreBindings.Any())
            {
                _writer.WriteLineIndented(
                    $"references ({coreBindings.Count()} structures, {coreBindings.Select(ec => ec.Elements.Count).Sum()} elements): " + ReferenceLiteral(coreBindings) +
                    ", strongest binding: " + strongestBinding!.GetLiteral() +
                    ", by type: " + string.Join(", ", bindingStrengthByType.Select(bs => $"{bs.Key}:{bs.Value}").Order()));
            }

            IEnumerable<StructureElementCollection> extendedBindings = _definitions.ExtendedBindingsForVs(vs.Url);
            strongestBinding = _definitions.StrongestBinding(extendedBindings);

            if (extendedBindings.Any())
            {
                _writer.WriteLineIndented(
                    $"extensions/profiles ({extendedBindings.Count()} structures, {extendedBindings.Select(ec => ec.Elements.Count).Sum()} elements):" +
                    " strongest binding: " + strongestBinding!.GetLiteral() +
                    ", refs: " + ExternalRefLiteral(extendedBindings));
            }

            ValueSet? expanded = _definitions.ExpandVs(vs.Url + "|" + vs.Version).Result;

            if (expanded?.Expansion?.Any() ?? false)
            {
                if (expanded.IsLimitedExpansion())
                {
                    _writer.WriteLineIndented($"! Partial expansion, not displayed");
                }
                else
                {
                    foreach (FhirConcept concept in vs.cgGetFlatConcepts(_definitions).OrderBy(c => c.Key))
                    {
                        _writer.WriteLineIndented($"- #{concept.Code}: {concept.Display}");
                    }
                }
            }
            else
            {
                _writer.WriteLineIndented($"! No expansion available");
            }

            _writer.DecreaseIndent();
        }

        return;

    }

    /// <summary>Build a string for the core structure collections that reference a value set.</summary>
    /// <param name="ecs">The ecs.</param>
    /// <returns>A string.</returns>
    private string ReferenceLiteral(IEnumerable<StructureElementCollection> ecs)
    {
        return string.Join(", ", ecs.SelectMany(ec => ec.Elements.Select(ed => ed.Path)).Distinct().Order());
    }

    /// <summary>Build a string for the external structure collections that reference a value set.</summary>
    /// <param name="ecs">The ecs.</param>
    /// <returns>A string.</returns>
    private string ExternalRefLiteral(IEnumerable<StructureElementCollection> ecs)
    {
        return string.Join(", ", ecs.Select(ec => $"{ec.Structure.Id}({ec.Structure.cgArtifactClass()}) [{string.Join(",", ec.Elements.Select(ed => ed.Path).Distinct().Order())}]").Order());
    }

    /// <summary>Writes an unresolved value sets.</summary>
    /// <param name="valueSetUrls">The value set urls.</param>
    /// <param name="headerHint">  (Optional) The header hint.</param>
    private void WriteUnresolvedValueSets(
        IEnumerable<string> valueSetUrls,
        string headerHint = "")
    {
        if (!string.IsNullOrEmpty(headerHint))
        {
            _writer.WriteLineIndented($"{headerHint}: {valueSetUrls.Count()}");
        }

        foreach (string url in valueSetUrls.Order())
        {
            _writer.WriteLineIndented($"- ValueSet: {url}");

            _writer.IncreaseIndent();

            IEnumerable<StructureElementCollection> coreBindings = _definitions.CoreBindingsForVs(url);
            BindingStrength? strongestBinding = _definitions.StrongestBinding(coreBindings);
            IReadOnlyDictionary<string, BindingStrength> bindingStrengthByType = _definitions.BindingStrengthByType(coreBindings);
            if (coreBindings.Any())
            {
                _writer.WriteLineIndented(
                    $"references ({coreBindings.Count()} structures, {coreBindings.Select(ec => ec.Elements.Count).Sum()} elements): " + ReferenceLiteral(coreBindings) +
                    ", strongest binding: " + strongestBinding!.GetLiteral() +
                    ", by type: " + string.Join(", ", bindingStrengthByType.Select(bs => $"{bs.Key}:{bs.Value}").Order()));
            }

            IEnumerable<StructureElementCollection> extendedBindings = _definitions.ExtendedBindingsForVs(url);
            strongestBinding = _definitions.StrongestBinding(extendedBindings);

            if (extendedBindings.Any())
            {
                _writer.WriteLineIndented(
                    $"extensions/profiles ({extendedBindings.Count()} structures, {extendedBindings.Select(ec => ec.Elements.Count).Sum()} elements):" +
                    " strongest binding: " + strongestBinding!.GetLiteral() +
                    ", refs: " + ExternalRefLiteral(extendedBindings));
            }

            _writer.DecreaseIndent();
        }

        return;
    }

    /// <summary>Writes the structures.</summary>
    /// <param name="structures">The structures.</param>
    /// <param name="headerHint">(Optional) The header hint.</param>
    private void WriteStructures(
        IEnumerable<StructureDefinition> structures,
        string headerHint = "")
    {
        if (!string.IsNullOrEmpty(headerHint))
        {
            _writer.WriteLineIndented($"{headerHint}: {structures.Count()}");
        }

        foreach (StructureDefinition sd in structures.OrderBy(c => c.Id))
        {
            WriteStructure(sd);
        }
    }

    /// <summary>Writes a primitive.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="sd">The SD.</param>
    private void WritePrimitive(StructureDefinition sd)
    {
        string snip = BuildStandardSnippet(sd.cgStandardStatus(), sd.cgMaturityLevel(), sd.cgIsExperimental());

        _writer.WriteLineIndented(
            $"- {sd.Name}:" +
                $" {sd.Name.ToCamelCase()}" +
                $"::{sd.cgpTypeForExport(NamingConvention.CamelCase, _primitiveTypeMap)}" +
                $"{snip}");

        _writer.IncreaseIndent();

        // check for regex
        if (!string.IsNullOrEmpty(sd.cgpValidationRegEx()))
        {
            _writer.WriteLineIndented($"[{sd.cgpValidationRegEx()}]");
        }

        if (_definitions.ExtensionsByPath.ContainsKey(sd.Id))
        {
            WriteExtensions(_definitions.ExtensionsByPath[sd.Id].Values);
        }

        // TODO(ginoc)
        //if (_info.ProfilesByBaseType.ContainsKey(primitive.Id))
        //{
        //    WriteProfiles(_info.ProfilesByBaseType[primitive.Id].Values);
        //}

        _writer.DecreaseIndent();
    }

    /// <summary>Writes the extensions.</summary>
    /// <param name="extensions">The extensions.</param>
    private void WriteExtensions(
        IEnumerable<StructureDefinition> extensions)
    {
        _writer.WriteLineIndented($"Extensions: {extensions.Count()}");

        foreach (StructureDefinition extension in extensions.OrderBy(e => e.Id))
        {
            WriteExtension(extension);
        }
    }

    /// <summary>Writes an extension.</summary>
    /// <param name="sd">The extension.</param>
    private void WriteExtension(
        StructureDefinition sd)
    {
        string card = sd.cgRootElement()?.cgCardinality() ?? string.Empty;

        if (string.IsNullOrEmpty(card))
        {
            _writer.WriteLineIndented($"+{sd.Url}");
        }
        else
        {
            _writer.WriteLineIndented($"+{sd.Url} [{card}]");
        }

        _writer.IncreaseIndent();

        // check for a 'simple' extension
        if (((sd.Snapshot?.Element.Count ?? 0) == 5) || (sd.Differential.Element.Count == 4))
        {
            ElementDefinition? eleUrl = (sd.Snapshot != null) && (sd.Snapshot.Element.Count != 0)
                ? sd.Snapshot.Element[3]
                : sd.Differential.Element[2];

            _writer.WriteLineIndented($"- {eleUrl.cgNameForExport(NamingConvention.CamelCase)}[{eleUrl.cgCardinality()}]: fixed to {eleUrl.Fixed}");

            ElementDefinition? eleValue = (sd.Snapshot != null) && (sd.Snapshot.Element.Count != 0)
                ? sd.Snapshot.Element[4]
                : sd.Differential.Element[3];

            string propertyType = string.Empty;

            if (eleValue.Type.Count != 0)
            {
                IReadOnlyDictionary<string, ElementDefinition.TypeRefComponent> types = eleValue.cgTypes();

                foreach ((string name, ElementDefinition.TypeRefComponent et) in types.OrderBy(r => r.Key))
                {
                    string joiner = string.IsNullOrEmpty(propertyType) ? string.Empty : "|";

                    string profiles = string.Empty;
                    if (et.Profile.Any())
                    {
                        profiles = "(" + string.Join("|", et.cgTypeProfiles().Keys.Order()) + ")";
                    }

                    string targets = string.Empty;
                    if (et.TargetProfile.Any())
                    {
                        targets = "(" + string.Join("|", et.cgTargetProfiles().Keys.Order()) + ")";
                    }
                    propertyType = $"{propertyType}{joiner}{name}{profiles}{targets}";
                }
            }

            _writer.WriteLineIndented($"- {eleValue.cgNameForExport(NamingConvention.CamelCase)}[{eleValue.cgCardinality()}]: {propertyType}");
        }
        else
        {
            //WriteStructure(sd);
        }

        _writer.DecreaseIndent();
    }

    /// <summary>Writes a complex.</summary>
    /// <param name="sd">The complex.</param>
    private void WriteStructure(StructureDefinition sd)
    {
        bool indented = false;

        // check for artifacts that have alternative renderings
        switch (sd.cgArtifactClass())
        {
            case Common.Models.FhirArtifactClassEnum.Extension:
            {
                WriteExtension(sd);
                return;
            }

            case Common.Models.FhirArtifactClassEnum.PrimitiveType:
            {
                WritePrimitive(sd);
                return;
            }
        }

        ElementDefinition? rootElement = sd.cgRootElement();

        // write this type's line, if it's a root element
        // (sub-properties are written with cardinality in the prior loop)
        if (_writer.Indentation == 0)
        {
            string snip = BuildStandardSnippet(sd.cgStandardStatus(), sd.cgMaturityLevel(), sd.cgIsExperimental());

            if (sd.cgArtifactClass() == Common.Models.FhirArtifactClassEnum.Profile)
            {
                _writer.WriteLine($"- {sd.Name}: ({sd.Id}) {sd.cgBaseTypeName()}{snip}");
            }
            else
            {
                _writer.WriteLine($"- {sd.Name}: {sd.cgBaseTypeName()}{snip} (abstract: {sd.Abstract == true})");
            }

            if (rootElement != null)
            {
                WriteElement(sd, rootElement, true);
            }

            _writer.IncreaseIndent();
            indented = true;

            IEnumerable<ElementDefinition.ConstraintComponent> constraints = sd.cgConstraints(includeInherited: true);
            if (constraints.Any() == true)
            {
                WriteConstraints(sd, constraints);
            }
        }
        else if (rootElement != null)
        {
            WriteElement(sd, rootElement, true);
        }

        if (sd.cgArtifactClass() == Common.Models.FhirArtifactClassEnum.Interface)
        {
            foreach (StructureDefinition resourceSd in _definitions.ResourcesForInterface(sd).OrderBy(s => s.Name))
            {
                _writer.WriteLineIndented($"+ Implemented by: {resourceSd.Name}: {resourceSd.Url}");
            }
        }

        // write elements
        WriteElements(sd, sd.cgElements(topLevelOnly: true));

        // check for extensions if this is the first time we write this type
        if (indented &&
            _definitions.ExtensionsByPath.TryGetValue(sd.Type, out Dictionary<string, StructureDefinition>? extDict) && (extDict != null))
        {
            WriteExtensions(extDict.Values);
        }

        // check for search parameters on this object
        if (_definitions.SearchParametersForBase(sd.Type).Any())
        {
            WriteSearchParameters(_definitions.SearchParametersForBase(sd.Type).Values);
        }

        // check for type operations
        if (_definitions.TypeOperationsForResource(sd.Type).Any())
        {
            WriteOperations(_definitions.TypeOperationsForResource(sd.Type).Values, WriteLevelCodes.Type, typeHint: sd.Type);
        }

        // check for instance operations
        if (_definitions.InstanceOperationsForResource(sd.Type).Any())
        {
            WriteOperations(_definitions.InstanceOperationsForResource(sd.Type).Values, WriteLevelCodes.Instance, typeHint: sd.Type);
        }

        // check for profiles for this type - note they are written at the root level as profiles, so we just put a line here for reference
        if (_definitions.ProfilesForBase(sd.Type).Any())
        {
            _writer.WriteLineIndented($"Profiles[{sd.Type}]: {_definitions.ProfilesForBase(sd.Type).Count}");

            _writer.IncreaseIndent();
            foreach (StructureDefinition profile in _definitions.ProfilesForBase(sd.Type).Values.OrderBy(s => s.Id))
            {
                _writer.WriteLineIndented($"-{profile.Id}: {profile.Url} ({profile.Name})");
            }
            _writer.DecreaseIndent();
        }

        if (indented)
        {
            _writer.DecreaseIndent();
        }
    }

    /// <summary>Values that represent write level codes.</summary>
    private enum WriteLevelCodes : int
    {
        System = 0,
        Type,
        Instance,
    }

    /// <summary>Writes the operations.</summary>
    /// <param name="operations">The operations.</param>
    /// <param name="writeLevel">True if is type level, false if not.</param>
    /// <param name="headerHint">(Optional) The header hint.</param>
    /// <param name="typeHint">  (Optional) The type hint.</param>
    private void WriteOperations(
        IEnumerable<OperationDefinition> operations,
        WriteLevelCodes writeLevel,
        string headerHint = "",
        string typeHint = "")
    {
        bool indented = false;

        if (!string.IsNullOrEmpty(headerHint))
        {
            _writer.WriteLineIndented($"{headerHint}: {operations.Count()}");
            _writer.IncreaseIndent();
            indented = true;
        }

        foreach (OperationDefinition operation in operations.OrderBy(o => o.Code))
        {
            string snip = BuildStandardSnippet(operation.cgStandardStatus(), operation.cgMaturityLevel(), operation.cgIsExperimental());

            switch (writeLevel)
            {
                case WriteLevelCodes.System:
                    _writer.WriteLineIndented($"${operation.Code}{snip}");
                    break;
                case WriteLevelCodes.Type:
                    _writer.WriteLineIndented($"{typeHint}/${operation.Code}{snip}");
                    break;
                case WriteLevelCodes.Instance:
                    _writer.WriteLineIndented($"{typeHint}/{{id}}/${operation.Code}{snip}");
                    break;
            }

            if (operation.Parameter.Count != 0)
            {
                _writer.IncreaseIndent();

                // write input operation parameters inline
                foreach (OperationDefinition.ParameterComponent parameter in operation.Parameter.Where(p => p.Use == OperationParameterUse.In).OrderBy(p => p.cgFieldOrder()))
                {
                    string st = (parameter.SearchType == null) ? string.Empty : "<" + parameter.SearchType.GetLiteral() + ">";
                    _writer.WriteLineIndented(
                        $"{parameter.Use}:" +
                        $" {parameter.Name}:" +
                        $" {parameter.Type}{st}[{parameter.cgCardinality()}]");
                }

                // write output operation parameters inline
                foreach (OperationDefinition.ParameterComponent parameter in operation.Parameter.Where(p => p.Use == OperationParameterUse.Out).OrderBy(p => p.cgFieldOrder()))
                {
                    string st = (parameter.SearchType == null) ? string.Empty : "<" + parameter.SearchType.GetLiteral() + ">";
                    _writer.WriteLineIndented(
                        $"{parameter.Use}:" +
                        $" {parameter.Name}:" +
                        $" {parameter.Type}{st}[{parameter.cgCardinality()}]");
                }

                _writer.DecreaseIndent();
            }
        }

        if (indented)
        {
            _writer.DecreaseIndent();
        }
    }

    /// <summary>Writes a FHIR query parameters.</summary>
    /// <param name="parameters">Options for controlling the operation.</param>
    /// <param name="headerHint">(Optional) The header hint.</param>
    private void WriteFhirQueryParameters(
        IEnumerable<FhirQueryParameter> parameters,
        string headerHint = "")
    {
        bool indented = false;

        if (!string.IsNullOrEmpty(headerHint))
        {
            _writer.WriteLineIndented($"{headerHint}: {parameters.Count()}");
            _writer.IncreaseIndent();
            indented = true;
        }

        foreach (FhirQueryParameter param in parameters.OrderBy(s => s.Name))
        {
            _writer.WriteLineIndented($"?{param.Name}: {param.ParamType.GetLiteral()} - {param.Url}");
        }

        if (indented)
        {
            _writer.DecreaseIndent();
        }
    }

    /// <summary>Writes search parameters.</summary>
    /// <param name="searchParameters">Options for controlling the search.</param>
    /// <param name="headerHint">      (Optional) The header hint.</param>
    private void WriteSearchParameters(
        IEnumerable<SearchParameter> searchParameters,
        string headerHint = "")
    {
        bool indented = false;

        if (!string.IsNullOrEmpty(headerHint))
        {
            _writer.WriteLineIndented($"{headerHint}: {searchParameters.Count()}");
            _writer.IncreaseIndent();
            indented = true;
        }

        foreach (SearchParameter searchParam in searchParameters.OrderBy(s => s.Code + s.Name))
        {
            if (searchParam.Component.Count != 0)
            {
                _writer.WriteLineIndented($"?{searchParam.Name}: {searchParam.Code} is composite (resolves: {searchParam.cgCompositeResolves(_definitions.SearchParametersByUrl.Keys)})");

                _writer.IncreaseIndent();

                foreach (SearchParameter.ComponentComponent c in searchParam.Component)
                {
                    if ((!string.IsNullOrEmpty(c.Definition)) &&
                        _definitions.SearchParametersByUrl.TryGetValue(c.Definition, out SearchParameter? compParam))
                    {
                        _writer.WriteLineIndented($"$({c.Definition}):{compParam.Type.GetLiteral()}");
                    }
                    else
                    {
                        _writer.WriteLineIndented($"$({c.Definition}):unresolved");
                    }
                }

                _writer.DecreaseIndent();
            }
            else
            {
                string snip = BuildStandardSnippet(searchParam.cgStandardStatus(), searchParam.cgMaturityLevel(), searchParam.cgIsExperimental());
                _writer.WriteLineIndented($"?{searchParam.Name}: {searchParam.Code}={searchParam.Type.GetLiteral()}{snip}");
            }
        }

        if (indented)
        {
            _writer.DecreaseIndent();
        }
    }

    /// <summary>Writes the constraints.</summary>
    /// <param name="constraints">The constraints.</param>
    private void WriteConstraints(StructureDefinition sd, IEnumerable<ElementDefinition.ConstraintComponent> constraints)
    {
        foreach (ElementDefinition.ConstraintComponent constraint in constraints)
        {
            string inherited = constraint.cgIsInherited(sd) ? "inherited" : "local";
            _writer.WriteLineIndented($"!{constraint.Key}: {inherited} {constraint.Severity.GetLiteral()}: {constraint.Expression}");
        }
    }

    /// <summary>Writes the elements.</summary>
    /// <param name="complex">    The complex.</param>
    private void WriteElements(StructureDefinition sd, IEnumerable<ElementDefinition> elements)
    {
        foreach (ElementDefinition element in elements.OrderBy(s => s.cgFieldOrder()))
        {
            WriteElement(sd, element);
        }
    }

    /// <summary>Writes an element.</summary>
    /// <param name="sd">      The complex.</param>
    /// <param name="ed">      The element.</param>
    /// <param name="writeAsRootElementInfo">(Optional) True if is root element, false if not.</param>
    private void WriteElement(
        StructureDefinition sd,
        ElementDefinition ed,
        bool writeAsRootElementInfo = false)
    {
        string propertyType = string.Empty;

        if (!ed.Path.Contains('.'))
        {
            string baseTypeName = ed.cgBaseTypeName(_definitions, false);

            if (!string.IsNullOrEmpty(baseTypeName))
            {
                propertyType = baseTypeName + "::" + sd.Name;
            }
            else
            {
                propertyType = sd.Name;
            }
        }
        else if (ed.Type.Count != 0)
        {
            IReadOnlyDictionary<string, ElementDefinition.TypeRefComponent> types = ed.cgTypes();

            foreach ((string name, ElementDefinition.TypeRefComponent et) in types.OrderBy(r => r.Key))
            {
                string joiner = string.IsNullOrEmpty(propertyType) ? string.Empty : "|";

                string profiles = string.Empty;
                if (et.Profile.Any())
                {
                    profiles = "(" + string.Join("|", et.cgTypeProfiles().Keys.Order()) + ")";
                }

                string targets = string.Empty;
                if (et.TargetProfile.Any())
                {
                    targets = "(" + string.Join("|", et.cgTargetProfiles().Keys.Order()) + ")";
                }

                propertyType = $"{propertyType}{joiner}{et.cgName()}{profiles}{targets}";
            }
        }

        if (string.IsNullOrEmpty(propertyType))
        {
            if (!string.IsNullOrEmpty(ed.ContentReference))
            {
                propertyType = ed.ContentReference;
            }
            else if (_definitions.HasChildElements(ed.Path))
            {
                propertyType = ed.Path;
            }
            else
            {
                propertyType = ed.cgBaseTypeName(_definitions, false, _primitiveTypeMap);
            }
        }

        if (ed.cgIsSimple())
        {
            propertyType += " *simple*";
        }

        if (ed.Constraint.Any(c => !c.cgIsInherited(sd)))
        {
            propertyType = propertyType +
                " constraints: " +
                string.Join(", ", ed.Constraint.Where(c => !c.cgIsInherited(sd)).Select(c => c.Key).OrderBy(v => v, NaturalComparer.Instance)) +
                "";
        }

        if (ed.Condition.Any())
        {
            propertyType = propertyType +
                " conditions: " +
                string.Join(", ", ed.Condition.OrderBy(v => v, NaturalComparer.Instance)) +
                "";
        }

        if (!string.IsNullOrEmpty(ed.cgBindingName()))
        {
            propertyType = propertyType + " binding name: " + ed.cgBindingName();
        }

        string fiveWs = string.Empty;

        if (!string.IsNullOrEmpty(ed.cgFiveWs()))
        {
            fiveWs = " (W5: " + ed.cgFiveWs() + ")";
        }

        if (writeAsRootElementInfo)
        {
            _writer.IncreaseIndent();
            _writer.WriteLineIndented(
                $"^{ed.cgNameForExport(NamingConvention.CamelCase)}[{ed.cgCardinality()}]:" +
                $" {propertyType}" +
                $"{fiveWs}");
            _writer.DecreaseIndent();
        }
        else
        {
            _writer.WriteLineIndented(
                $"-" +
                $" {ed.cgNameForExport(NamingConvention.CamelCase)}[{ed.cgCardinality()}]:" +
                $" {propertyType}" +
                $"{fiveWs}");
        }

        // check for regex
        if (!string.IsNullOrEmpty(ed.cgValidationRegEx()))
        {
            _writer.WriteLineIndented($"[{ed.cgValidationRegEx()}]");
        }

        // check for default value
        if (ed.DefaultValue != null)
        {
            _writer.WriteLineIndented($".{ed.cgDefaultFieldName()} = {ed.DefaultValue}");
        }

        // check for fixed value
        if (ed.Fixed != null)
        {
            _writer.WriteLineIndented($".{ed.cgFixedFieldName()} = {ed.Fixed}");
        }

        // check for pattern value
        if (ed.Pattern != null)
        {
            _writer.WriteLineIndented($".{ed.cgPatternFieldName()} = {ed.Pattern}");
        }

        IEnumerable<string> codes = ed.cgCodes(_definitions);

        if (codes.Any())
        {
            _writer.IncreaseIndent();
            _writer.WriteLineIndented($"{{{string.Join("|", codes.Order())}}}");
            _writer.DecreaseIndent();
        }

        // check for slicing information defined by the current sd
        if (_definitions.TryGetSliceNames(ed.Path, out string[]? sliceNames, sd) &&
            (sliceNames != null) &&
            (sliceNames.Length != 0))
        {
            WriteSlices(sd, ed, sliceNames);
        }

        if (!writeAsRootElementInfo)
        {
            if (_definitions.ExtensionsByPath.ContainsKey(ed.Path))
            {
                _writer.IncreaseIndent();
                WriteExtensions(_definitions.ExtensionsByPath[ed.Path].Values);
                _writer.DecreaseIndent();
            }

            if (_definitions.HasChildElements(ed.Path))
            {
                _writer.IncreaseIndent();
                WriteElements(sd, sd.cgElements(forBackbonePath: ed.Path, topLevelOnly: true, includeRoot: false));
                _writer.DecreaseIndent();
            }
        }
    }

    /// <summary>Writes the slicings.</summary>
    /// <param name="sliceNames">The slicings.</param>
    private void WriteSlices(
        StructureDefinition sd,
        ElementDefinition ed,
        string[] sliceNames)
    {
        _writer.IncreaseIndent();

        if (ed.Slicing == null)
        {
            _writer.WriteLineIndented($"@Slices present with no defined slicing");
        }
        else
        {
            _writer.WriteLineIndented($"@Slicing: {ed.Slicing.Rules.GetLiteral()}: {string.Join(", ", ed.Slicing.Discriminator.Select(d => $"{d.Type.GetLiteral()}:{d.Path}").Order())}");
        }

        _writer.IncreaseIndent();

        foreach (string sliceName in sliceNames)
        {
            // get the elements for this slice
            IEnumerable<ElementDefinition> sliceElements = sd.cgElementsForSlice(ed, sliceName);

            // get the discriminated values (if defined)
            IEnumerable<SliceDiscriminator> dvs = sd.cgDiscriminatedValues(_definitions, ed, sliceName, sliceElements);

            if (dvs.Any())
            {
                _writer.WriteLineIndented($":{sliceName}");
                _writer.IncreaseIndent();

                foreach (SliceDiscriminator discriminator in dvs)
                {
                    string path = string.IsNullOrEmpty(discriminator.PostResolvePath)
                        ? discriminator.Path
                        : string.Join(".", discriminator.Path, "resolve()", discriminator.PostResolvePath);

                    if (discriminator.IsBinding)
                    {
                        _writer.WriteLineIndented($"- {discriminator.Type} @ {path} from {discriminator.Value} ({discriminator.BindingName})");

                        string vsUrl = discriminator.Value.ToString() ?? string.Empty;

                        if (!string.IsNullOrEmpty(vsUrl))
                        {
                            ValueSet? vs = _definitions.ExpandVs(vsUrl).Result;

                            _writer.IncreaseIndent();

                            int i = 0;
                            foreach (ValueSet.ContainsComponent? cc in vs?.Expansion.Contains ?? Enumerable.Empty<ValueSet.ContainsComponent>())
                            {
                                if (i++ > 10)
                                {
                                    _writer.WriteLineIndented("...");
                                    break;
                                }
                                _writer.WriteLineIndented($"{cc?.System}|{cc?.Code} - {cc?.Display}");
                            }

                            _writer.DecreaseIndent();
                        }

                        continue;
                    }

                    if (discriminator.DiscriminatorType == ElementDefinition.DiscriminatorType.Type)
                    {
                        _writer.WriteLineIndented($"- {discriminator.Type} @ {path} only {discriminator.Value}");
                        continue;
                    }

                    switch (discriminator.Value)
                    {
                        case CodeableConcept fcc:
                            _writer.WriteLineIndented($"- {discriminator.Type} @ {path} = {string.Join(", ", fcc.Coding.Select(c => $"{c.System}|{c.Code}: {c.Display}").Order())}");
                            break;
                        case Coding fcd:
                            _writer.WriteLineIndented($"- {discriminator.Type} @ {path} = {fcd.System}|{fcd.Code}: {fcd.Display}");
                            break;
                        case Quantity fq:
                            _writer.WriteLineIndented($"- {discriminator.Type} @ {path} = {fq.Value} {fq.Code}");
                            break;
                        case Hl7.Fhir.Model.Range fr:
                            _writer.WriteLineIndented($"- {discriminator.Type} @ {path} = {fr.Low.Value} - {fr.High.Value}");
                            break;
                        case Period fp:
                            _writer.WriteLineIndented($"- {discriminator.Type} @ {path} = {fp.Start} - {fp.End}");
                            break;
                        default:
                            _writer.WriteLineIndented($"- {discriminator.Type} @ {path} = {discriminator.Value}");
                            break;
                    }
                }

                _writer.DecreaseIndent();
            }
        }

        _writer.DecreaseIndent();
        _writer.DecreaseIndent();
    }

    /// <summary>Builds standard snippet.</summary>
    /// <param name="standardStatus">The standard status.</param>
    /// <param name="fmmLevel">      The fmm level.</param>
    /// <returns>A string.</returns>
    private static string BuildStandardSnippet(string standardStatus, int? fmmLevel, bool? isExperimental)
    {
        string val = standardStatus;

        if (fmmLevel != null)
        {
            if (string.IsNullOrEmpty(val))
            {
                val = "FMM: " + fmmLevel.ToString();
            }
            else
            {
                val = val + " FMM: " + fmmLevel.ToString();
            }
        }

        if (isExperimental == true)
        {
            if (string.IsNullOrEmpty(val))
            {
                val = "experimental";
            }
            else
            {
                val += " experimental";
            }
        }

        if (string.IsNullOrEmpty(val))
        {
            return string.Empty;
        }

        return " (" + val + ")";
    }


    /// <summary>Writes a header.</summary>
    /// <param name="config">     The configuration.</param>
    /// <param name="definitions">The definitions to export.</param>
    private void WriteHeader(
        InfoOptions config,
        DefinitionCollection definitions)
    {
        _writer.WriteLine($"Contents of: {string.Join(", ", definitions.Manifests.Select(kvp => kvp.Key).Order())}");
        _writer.WriteLine($"  Primitive Naming Style: {NamingConvention.CamelCase}");
        _writer.WriteLine($"  Element Naming Style: {NamingConvention.CamelCase}");
        _writer.WriteLine($"  Complex Type / Resource Naming Style: {NamingConvention.PascalCase}");
        _writer.WriteLine($"  Enum Naming Style: {NamingConvention.FhirDotNotation}");
        _writer.WriteLine($"  Interaction Naming Style: {NamingConvention.PascalCase}");
        //_writer.WriteLine($"  Extension Support: {_options.ExtensionSupport}");

        if (config.LoadStructures.Length != 0)
        {
            string restrictions = string.Join("|", config.LoadStructures.Order());
            _writer.WriteLine($"  Load structures: {restrictions}");
        }

        if (config.ExportStructures.Length != 0)
        {
            string restrictions = string.Join("|", config.ExportStructures.Order());
            _writer.WriteLine($"  Export structures: {restrictions}");
        }

        if (config.ExportKeys.Count != 0)
        {
            string restrictions = string.Join("|", config.ExportKeys.Order());
            _writer.WriteLine($"  Export keys: {restrictions}");
        }

        //if ((_options.LanguageOptions != null) && (_options.LanguageOptions.Count > 0))
        //{
        //    foreach (KeyValuePair<string, string> kvp in _options.LanguageOptions)
        //    {
        //        _writer.WriteLine($"  Language option: \"{kvp.Key}\" = \"{kvp.Value}\"");
        //    }
        //}
    }

}
