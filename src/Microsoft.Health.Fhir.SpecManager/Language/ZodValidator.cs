// <copyright file="TypeScript.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Health.Fhir.CodeGenCommon.Extensions;
using Microsoft.Health.Fhir.SpecManager.Manager;
using Microsoft.Health.Fhir.SpecManager.Models;

namespace Microsoft.Health.Fhir.SpecManager.Language
{
    /// <summary>Export to TypeScript - serializable to/from JSON.</summary>
    public sealed class ZodValidator : ILanguage
    {
        /// <summary>The systems named by display.</summary>
        private static HashSet<string> _systemsNamedByDisplay = new HashSet<string>()
        {
            /// <summary>Units of Measure have incomprehensible codes after naming substitutions.</summary>
            "http://unitsofmeasure.org",
        };

        private static HashSet<string> _systemsNamedByCode = new HashSet<string>()
        {
            /// <summary>Operation Outcomes include c-style string formats in display.</summary>
            "http://terminology.hl7.org/CodeSystem/operation-outcome",

            /// <summary>Descriptions have quoted values.</summary>
            "http://terminology.hl7.org/CodeSystem/smart-capabilities",

            /// <summary>Descriptions have quoted values.</summary>
            "http://hl7.org/fhir/v2/0301",

            /// <summary>Display values are too long to be useful.</summary>
            "http://terminology.hl7.org/CodeSystem/v2-0178",

            /// <summary>Display values are too long to be useful.</summary>
            "http://terminology.hl7.org/CodeSystem/v2-0277",

            /// <summary>Display values are too long to be useful.</summary>
            "http://terminology.hl7.org/CodeSystem/v3-VaccineManufacturer",

            /// <summary>Display values are too long to be useful.</summary>
            "http://hl7.org/fhir/v2/0278",

            /// <summary>Display includes operation symbols: $.</summary>
            "http://terminology.hl7.org/CodeSystem/testscript-operation-codes",

            /// <summary>Display are often just symbols.</summary>
            "http://hl7.org/fhir/v2/0290",

            /// <summary>Display includes too many Unicode characters (invalid export names).</summary>
            "http://hl7.org/fhir/v2/0255",

            /// <summary>Display includes too many Unicode characters (invalid export names).</summary>
            "http://hl7.org/fhir/v2/0256",
        };

        /// <summary>FHIR information we are exporting.</summary>
        private FhirVersionInfo _info;

        /// <summary>Options for controlling the export.</summary>
        private ExporterOptions _options;

        /// <summary>
        /// True if we should write a namespace directive
        /// </summary>
        private bool _includeNamespace = false;

        /// <summary>
        /// The namespace to use.
        /// </summary>
        private string _namespace = string.Empty;

        /// <summary>The exported codes.</summary>
        private HashSet<string> _exportedCodes = new HashSet<string>();

        /// <summary>The exported resources.</summary>
        private List<string> _exportedResources = new List<string>();

        /// <summary>The currently in-use text writer.</summary>
        private ExportStreamWriter _writer;

        /// <summary>Name of the language.</summary>
        private const string _languageName = "Zod";

        /// <summary>The single file export extension.</summary>
        private const string _singleFileExportExtension = ".ts";

        /// <summary>The minimum type script version.</summary>
        private string _minimumTypeScriptVersion = "3.7";

        /// <summary>Dictionary mapping FHIR primitive types to language equivalents.</summary>
        private static readonly Dictionary<string, string> _primitiveTypeMap = new Dictionary<string, string>()
        {
            { "base", "Object" },
            { "base64Binary", "string" },
            { "boolean", "boolean" },
            { "canonical", "string" },
            { "code", "string" },
            { "date", "string" },
            { "dateTime", "string" },
            { "decimal", "number" },
            { "id", "string" },
            { "instant", "string" },
            { "integer", "number" },
            { "integer64", "string" },       // int64 serializes as string, need to add custom handling here
            { "markdown", "string" },
            { "oid", "string" },
            { "positiveInt", "number" },
            { "string", "string" },
            { "time", "string" },
            { "unsignedInt", "number" },
            { "uri", "string" },
            { "url", "string" },
            { "uuid", "string" },
            { "xhtml", "string" },
        };

        /// <summary>Gets the reserved words.</summary>
        /// <value>The reserved words.</value>
        private static readonly HashSet<string> _reservedWords = new HashSet<string>()
        {
            "const",
            "enum",
            "export",
            "interface",
            "z",
        };

        /// <summary>The generics and type hints.</summary>
        private static readonly Dictionary<string, GenericTypeHintInfo> _genericsAndTypeHints = new Dictionary<string, GenericTypeHintInfo>()
        {
            {
                "Bundle",
                new GenericTypeHintInfo()
                {
                    Alias = "BundleContentType",
                    GenericHint = "FhirResource",
                    IncludeBase = true,
                }
            },
            {
                "Bundle.entry",
                new GenericTypeHintInfo()
                {
                    Alias = "BundleContentType",
                    GenericHint = "FhirResource",
                    IncludeBase = true,
                }
            },
            {
                "Bundle.entry.resource",
                new GenericTypeHintInfo()
                {
                    Alias = "BundleContentType",
                    GenericHint = string.Empty,
                    IncludeBase = false,
                }
            },
        };

        /// <summary>Gets the name of the language.</summary>
        /// <value>The name of the language.</value>
        string ILanguage.LanguageName => _languageName;

        /// <summary>
        /// Gets the single file extension for this language - null or empty indicates a multi-file
        /// export (exporter should copy the contents of the directory).
        /// </summary>
        string ILanguage.SingleFileExportExtension => _singleFileExportExtension;

        /// <summary>Gets the FHIR primitive type map.</summary>
        /// <value>The FHIR primitive type map.</value>
        Dictionary<string, string> ILanguage.FhirPrimitiveTypeMap => _primitiveTypeMap;

        /// <summary>Gets the reserved words.</summary>
        /// <value>The reserved words.</value>
        HashSet<string> ILanguage.ReservedWords => _reservedWords;

        /// <summary>
        /// Gets a list of FHIR class types that the language WILL export, regardless of user choices.
        /// Used to provide information to users.
        /// </summary>
        List<ExporterOptions.FhirExportClassType> ILanguage.RequiredExportClassTypes => new List<ExporterOptions.FhirExportClassType>()
        {
            ExporterOptions.FhirExportClassType.ComplexType,
            ExporterOptions.FhirExportClassType.Resource,
        };

        /// <summary>
        /// Gets a list of FHIR class types that the language CAN export, depending on user choices.
        /// </summary>
        List<ExporterOptions.FhirExportClassType> ILanguage.OptionalExportClassTypes => new List<ExporterOptions.FhirExportClassType>()
        {
        };

        /// <summary>Gets language-specific options and their descriptions.</summary>
        Dictionary<string, string> ILanguage.LanguageOptions => new Dictionary<string, string>()
        {
            { "namespace", "Base namespace for TypeScript files (default: fhir{VersionNumber})." },
            { "min-ts-version", "Minimum TypeScript version (default: 3.7, use '-' for none)." }
        };

        /// <summary>Export the passed FHIR version into the specified directory.</summary>
        /// <param name="info">           The information.</param>
        /// <param name="serverInfo">     Information describing the server.</param>
        /// <param name="options">        Options for controlling the operation.</param>
        /// <param name="exportDirectory">Directory to write files.</param>
        void ILanguage.Export(
            FhirVersionInfo info,
            FhirCapabiltyStatement serverInfo,
            ExporterOptions options,
            string exportDirectory)
        {
            // set internal vars so we don't pass them to every function
            // this is ugly, but the interface patterns get bad quickly because we need the type map to copy the FHIR info
            _info = info;
            _options = options;

            _includeNamespace = _options.GetParam("namespace", false);

            _namespace = $"fhir{FhirPackageCommon.RForSequence(_info.FhirSequence).Substring(1).ToLowerInvariant()}.zod";

            _minimumTypeScriptVersion = _options.GetParam("min-ts-version", "3.7");

            _exportedCodes = new HashSet<string>();
            _exportedResources = new List<string>();

            // create a filename for writing (single file for now)
            string filename = Path.Combine(exportDirectory, $"zod{info.FhirSequence}.ts");

            using (FileStream stream = new FileStream(filename, FileMode.Create))
            using (ExportStreamWriter writer = new ExportStreamWriter(stream))
            {
                _writer = writer;

                WriteHeader();

                WriteComplexes(_info.ComplexTypes.Values, false);
                WriteComplexes(_info.Resources.Values, true);
                WriteFHIRResourceSchema();
                WriteExports();
                WriteFooter();
            }
        }

        /// <summary>Writes the complexes.</summary>
        /// <param name="complexes"> The complexes.</param>
        /// <param name="isResource">(Optional) True if is resource, false if not.</param>
        private void WriteComplexes(
            IEnumerable<FhirComplex> complexes,
            bool isResource = false)
        {
            foreach (FhirComplex complex in complexes.OrderBy(c => c.Name))
            {
                WriteComplex(complex, isResource);
            }
        }

        /// <summary>Writes a complex.</summary>
        /// <param name="complex">   The complex.</param>
        /// <param name="isResource">True if is resource, false if not.</param>
        private void WriteComplex(
            FhirComplex complex,
            bool isResource)
        {
            // check for nested components
            if (complex.Components != null)
            {
                foreach (FhirComplex component in complex.Components.Values)
                {
                    WriteComplex(component, false);
                }
            }

            // zod schema name
            string schemaName;
            if (string.IsNullOrEmpty(complex.BaseTypeName) ||
                complex.Name.Equals("Element", StringComparison.Ordinal))
            {
                schemaName = complex.NameForExport(FhirTypeBase.NamingConvention.PascalCase);
                _writer.WriteLineIndented($"const {schemaName}Schema = z.lazy<z.ZodObject<Record<string, any>>>((): z.ZodObject<Record<string, any>> => {{");
                _writer.IncreaseIndent();
                _writer.WriteLineIndented($"return z.object({{");
            }
            else if (complex.Name.Equals(complex.BaseTypeName, StringComparison.Ordinal))
            {
                schemaName = complex.NameForExport(FhirTypeBase.NamingConvention.PascalCase, true);
                _writer.WriteLineIndented($"const {schemaName}Schema = z.lazy<z.ZodObject<Record<string, any>>>((): z.ZodObject<Record<string, any>> => {{");
                _writer.IncreaseIndent();
                _writer.WriteLineIndented($"return z.object({{");
            }
            else
            {
                schemaName = complex.NameForExport(FhirTypeBase.NamingConvention.PascalCase, true);
                string typeName = complex.TypeForExport(FhirTypeBase.NamingConvention.PascalCase, _primitiveTypeMap, false);
                _writer.WriteLineIndented($"const {schemaName}Schema = z.lazy<z.ZodObject<Record<string, any>>>((): z.ZodObject<Record<string, any>> => {{");
                _writer.IncreaseIndent();
                _writer.WriteLineIndented($"return {typeName}Schema.schema.extend({{");
            }

            _writer.IncreaseIndent();

            if (isResource)
            {
                if (ShouldWriteResourceType(complex.Name))
                {
                    _exportedResources.Add(schemaName);
                    _writer.WriteLineIndented($"resourceType: z.literal('{complex.Name}'),");
                }
                else
                {
                    _writer.WriteLineIndented($"resourceType: z.string(),");
                }
            }

            // write elements
            WriteElements(complex, out List<FhirElement> elementsWithCodes);

            _writer.DecreaseIndent();

            // close interface (type)
            _writer.WriteLineIndented("});");


            _writer.DecreaseIndent();
            _writer.WriteLine("});");
        }

        /// <summary>Writes the expanded resource interface binding.</summary>
        private void WriteExports()
        {
            _exportedResources.Sort();

            _writer.WriteLine("export {");
            int index = 0;
            int last = _exportedResources.Count - 1;
            _writer.IncreaseIndent();
            foreach (string exportedName in _exportedResources)
            {
                _writer.WriteLineIndented($"{exportedName}Schema{(index != last ? "," : "")}");
                index++;
            }
            _writer.DecreaseIndent();
            _writer.WriteLine("};");
        }

        private void WriteFHIRResourceSchema()
        {
            _exportedResources.Sort();

            _writer.WriteLine("FhirResourceSchema = z.union([");
            int index = 0;
            int last = _exportedResources.Count - 1;
            _writer.IncreaseIndent();
            foreach (string exportedName in _exportedResources)
            {
                _writer.WriteLineIndented($"{exportedName}Schema{(index != last ? "," : "")}");
                index++;
            }
            _writer.DecreaseIndent();
            _writer.WriteLine("]);");
        }

        /// <summary>Determine if we should write resource name.</summary>
        /// <param name="name">The name.</param>
        /// <returns>True if it succeeds, false if it fails.</returns>
        private static bool ShouldWriteResourceType(string name)
        {
            switch (name)
            {
                case "Resource":
                case "DomainResource":
                case "MetadataResource":
                case "CanonicalResource":
                    return false;
            }

            return true;
        }

        /// <summary>Writes the elements.</summary>
        /// <param name="complex">          The complex.</param>
        /// <param name="elementsWithCodes">[out] The elements with codes.</param>
        private void WriteElements(
            FhirComplex complex,
            out List<FhirElement> elementsWithCodes)
        {
            elementsWithCodes = new List<FhirElement>();

            foreach (FhirElement element in complex.Elements.Values.OrderBy(s => s.Name))
            {
                if (element.IsInherited)
                {
                    continue;
                }

                WriteElement(complex, element);

                if ((element.Codes != null) && (element.Codes.Count > 0))
                {
                    elementsWithCodes.Add(element);
                }
            }
        }

        /// <summary>Writes an element.</summary>
        /// <param name="complex">The complex.</param>
        /// <param name="element">The element.</param>
        private void WriteElement(
            FhirComplex complex,
            FhirElement element)
        {
            HashSet<string> primitives = new HashSet<string>();
            primitives.Add("string");
            primitives.Add("boolean");
            primitives.Add("number");
            Dictionary<string, string> values = element.NamesAndTypesForExport(
                FhirTypeBase.NamingConvention.CamelCase,
                FhirTypeBase.NamingConvention.PascalCase,
                false,
                string.Empty,
                complex.Components.ContainsKey(element.Path));

            foreach (KeyValuePair<string, string> kvp in values)
            {
                bool isPrimative = primitives.Contains(kvp.Value);
                string result = $"{kvp.Key}: ";

                // Use generated enum for codes when required strength
                // EXCLUDE the MIME type value set - those should be bound to strings
                if (element.Codes != null
                        && element.Codes.Any()
                        && !string.IsNullOrEmpty(element.ValueSet)
                        && !string.IsNullOrEmpty(element.BindingStrength)
                        && string.Equals(element.BindingStrength, "required", StringComparison.Ordinal)
                        && (element.ValueSet != "http://www.rfc-editor.org/bcp/bcp13.txt")
                        && (!element.ValueSet.StartsWith("http://hl7.org/fhir/ValueSet/mimetypes", StringComparison.Ordinal)))
                {
                    if (_info.TryGetValueSet(element.ValueSet, out FhirValueSet vs))
                    {
                        if (element.IsArray)
                        {
                            result += $"z.array(z.enum([{string.Join(",", vs.Concepts.Select(c => $"'{c.Code}'"))}]))";
                        }
                        else
                        {
                            result += $"z.enum([{string.Join(",", vs.Concepts.Select(c => $"'{c.Code}'"))}])";
                        }
                    }
                    else
                    {
                        if (element.IsArray)
                        {
                            result += $"z.array(z.enum([{string.Join(",", element.Codes.Select(c => $"'{c}'"))}]))";
                        }
                        else
                        {
                            result += $"z.enum([{string.Join(",", element.Codes.Select(c => $"'{c}'"))}])";
                        }
                    }
                }
                else if (kvp.Value.Equals("Resource", StringComparison.Ordinal))
                {
                    if (element.IsArray)
                    {
                        result += $"z.array(FhirResourceSchema)";
                    }
                    else
                    {
                        result += "FhirResourceSchema";
                    }
                }
                else
                {
                    if (element.IsArray)
                    {
                        result += $"z.array({(isPrimative ? "z." : "")}{kvp.Value}{(isPrimative ? "()" : "Schema")})";
                    }
                    else
                    {
                        result += $"{(isPrimative ? "z." : "")}{kvp.Value}{(isPrimative ? "()" : "Schema")}";
                    }
                }

                // TODO various fields in the fhir spec are mutually exclusive ors (xors)
                // but zod does not have an out of the box xor method, so using nullish
                result+= ".nullish()";

                if (element.IsOptional)
                {
                    result += ".optional()";
                }

                /* worry about this later
                if (!string.IsNullOrEmpty(element.Comment))
                {
                    result += $".description('{element.Comment}')";
                }
                */

                result += ",";
                _writer.WriteLineIndented(result);
            }
        }

        /// <summary>Writes a header.</summary>
        private void WriteHeader()
        {
            _writer.WriteLineIndented("// <auto-generated/>");
            _writer.WriteLineIndented($"// Contents of: {_info.PackageName} version: {_info.VersionString}");
            _writer.WriteLineIndented($"  // Primitive Naming Style: {FhirTypeBase.NamingConvention.None}");
            _writer.WriteLineIndented($"  // Complex Type / Resource Naming Style: {FhirTypeBase.NamingConvention.PascalCase}");
            _writer.WriteLineIndented($"  // Interaction Naming Style: {FhirTypeBase.NamingConvention.None}");
            _writer.WriteLineIndented($"  // Extension Support: {_options.ExtensionSupport}");

            if ((_options.ExportList != null) && _options.ExportList.Any())
            {
                string restrictions = string.Join("|", _options.ExportList);
                _writer.WriteLineIndented($"  // Restricted to: {restrictions}");
            }

            if ((_options.LanguageOptions != null) && (_options.LanguageOptions.Count > 0))
            {
                foreach (KeyValuePair<string, string> kvp in _options.LanguageOptions)
                {
                    _writer.WriteLineIndented($"  // Language option: \"{kvp.Key}\" = \"{kvp.Value}\"");
                }
            }

            if (!_minimumTypeScriptVersion.Equals("-"))
            {
                _writer.WriteLine($"// Minimum TypeScript Version: {_minimumTypeScriptVersion}");
            }

            // import zod as z
            _writer.WriteLine("import { z } from 'zod';");
            _writer.WriteLine();
            // zod does not support inheritance so we need to use composition via union
            // we also have a hoisting problem 
            _writer.WriteLine("let FhirResourceSchema: z.ZodTypeAny;");

            if (_includeNamespace)
            {
                _writer.WriteLineIndented($"export as namespace {_namespace};");
            }
        }

        /// <summary>Writes a footer.</summary>
        private void WriteFooter()
        {
            return;
        }

        /// <summary>Writes an indented comment.</summary>
        /// <param name="value">The value.</param>
        private void WriteIndentedComment(string value)
        {
            _writer.WriteLineIndented($"/**");

            string comment = value.Replace('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n\n", "\n", StringComparison.Ordinal);

            string[] lines = comment.Split('\n');
            foreach (string line in lines)
            {
                _writer.WriteIndented(" * ");
                _writer.WriteLine(line);
            }

            _writer.WriteLineIndented($" */");
        }

        /// <summary>Information about the generic type hint.</summary>
        private struct GenericTypeHintInfo
        {
            internal string Alias;
            internal bool IncludeBase;
            internal string GenericHint;
        }
    }
}
