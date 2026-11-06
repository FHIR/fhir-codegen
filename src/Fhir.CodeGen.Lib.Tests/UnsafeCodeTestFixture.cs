// <copyright file="UnsafeCodeTestFixture.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

extern alias r4;
extern alias r4b;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Lib.Configuration;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Hl7.FhirPath.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using BindingStrength = Hl7.Fhir.Model.BindingStrength;
using ConceptMap = Hl7.Fhir.Model.ConceptMap;
using FhirBoolean = Hl7.Fhir.Model.FhirBoolean;
using PublicationStatus = Hl7.Fhir.Model.PublicationStatus;

namespace Fhir.CodeGen.Lib.Tests;

internal sealed class UnsafeCodeTestFixture : IDisposable
{
    internal const string FilterSystem = "http://hl7.org/fhir/filter-operator";
    internal const string FilterValueSet = "http://hl7.org/fhir/ValueSet/filter-operator";
    internal const string FilterElement = "ValueSet.compose.include.filter.op";
    internal const string ImportedNativeElement = "ValueSet.jurisdiction";
    internal const string ImportedSourceSystem = "urn:example:source";
    internal const string ImportedSystemVersion = "system-version-7";

    internal enum ImportedNativeScenario
    {
        IdentityChanging,
        CodeOnly,
        SystemOnly,
        SystemVersionOnly,
        IdentityPreserving,
        SourceNonmember,
        TargetNonmember,
        SourceValueSetVersionMismatch,
        TargetValueSetVersionMismatch,
        SourceSystemVersionMismatch,
        TargetSystemVersionMismatch,
    }

    private readonly bool _persistDatabase;

    internal SqliteConnection Connection { get; private set; }
    internal int ConnectionReopens { get; private set; }
    internal string SourceRoot { get; }
    internal string ConfigurationFilename => Path.Combine(SourceRoot, "input", "ig-support", "unsafe-code-mappings.json");
    internal List<DbFhirPackage> Packages { get; } = [];
    internal DbUnsafeCodeInputState InputState => DbUnsafeCodeInputState.SelectSingle(Connection, Key: 1)!;

    internal UnsafeCodeTestFixture(bool initializeIndices = false, bool persistDatabase = false)
    {
        _persistDatabase = persistDatabase;
        SourceRoot = Path.Combine(Path.GetTempPath(), "fhir-codegen-unsafe-" + Guid.NewGuid().ToString("N"));
        Connection = new(persistDatabase
            ? new SqliteConnectionStringBuilder { DataSource = Path.Combine(SourceRoot, "snapshot.sqlite"), Pooling = false }.ToString()
            : "Data Source=:memory:");
        try
        {
            Directory.CreateDirectory(SourceRoot);
            Connection.Open();
            string input = Path.Combine(AppContext.BaseDirectory, "TestData", "XverUnsafeCodes");
            foreach (string filename in Directory.GetFiles(Path.Combine(input, "input"), "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(SourceRoot, Path.GetRelativePath(input, filename));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(filename, destination);
            }

            if (initializeIndices)
            {
                DbContentClasses.LoadIndices(Connection);
                DbMappingClasses.LoadIndices(Connection);
                DbComparisonClasses.LoadIndices(Connection);
                DbOutcomeClasses.LoadIndices(Connection);
            }

            DbContentClasses.CreateTables(Connection);
            DbMappingClasses.CreateTables(Connection);
            FhirReleases.FhirSequenceCodes[] sequences = [FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R4B, FhirReleases.FhirSequenceCodes.R5];
            foreach (FhirReleases.FhirSequenceCodes sequence in sequences)
            {
                DbFhirPackage package = new()
                {
                    Name = sequence.ToCorePackageId(),
                    PackageId = sequence.ToCorePackageId(),
                    PackageVersion = sequence.ToLongVersion(),
                    FhirVersionShort = sequence.ToShortVersion(),
                    CanonicalUrl = "http://hl7.org/fhir",
                    ShortName = sequence.ToRLiteral(),
                    Dependencies = null,
                    DefinitionFhirSequence = sequence,
                };
                package.Key = DbFhirPackage.Insert(Connection, package);
                Packages.Add(package);
                InsertStructure(package);
                InsertValueSet(package);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool Load() => new MappingLoader(Connection, NullLoggerFactory.Instance).TryLoadCrossVersionSourceMaps(SourceRoot, false);

    internal const string MixedChoiceElement = "Parameters.parameter.value[x]";
    internal const string MixedCodingSystem = "urn:example:codes";
    internal const string MixedPrimitiveSystem = "urn:example:primitive";
    internal const string MixedPrimitiveVersion = "primitive-v1";

    internal static UnsafeCodeTestFixture CreateMixedChoiceInputs(
        int targetIndex, string primitiveAlternative, string primitiveQualification, bool enableRules)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetIndex, 1);
        if (primitiveAlternative is not ("boolean" or "code") ||
            primitiveQualification is not ("qualified" or "missing-binding" or "multi-system" or "unknown-version" or "nonmember" or "version-nonmatch"))
        {
            throw new ArgumentException("Unknown mixed-choice input variant.");
        }

        UnsafeCodeTestFixture fixture = new(initializeIndices: true, persistDatabase: true);
        Directory.Delete(Path.Combine(fixture.SourceRoot, "input"), recursive: true);
        DbStructureDefinition.Delete(fixture.Connection);
        DbValueSet.Delete(fixture.Connection);
        DbOutcomeClasses.CreateTables(fixture.Connection, forValueSets: true, forStructures: false);
        foreach (DbFhirPackage package in fixture.Packages)
        {
            foreach (string primitive in new[] { "boolean", "code", "string", "uri" })
            {
                DbStructureDefinition structure = fixture.AddStructure(package, primitive, FhirArtifactClassEnum.PrimitiveType);
                fixture.AddMixedChoiceElement(structure, primitive, [], min: 0);
            }

            DbStructureDefinition coding = fixture.AddStructure(package, "Coding");
            fixture.AddMixedChoiceElement(coding, "Coding", []);
            fixture.AddMixedChoiceElement(coding, "Coding.system", ["uri"]);
            fixture.AddMixedChoiceElement(coding, "Coding.version", ["string"]);
            fixture.AddMixedChoiceElement(coding, "Coding.code", ["code"]);
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddMixedChoiceElement(extension, "Extension", []);
            fixture.AddMixedChoiceElement(extension, "Extension.value[x]", ["Coding", "boolean", "code", "string", "uri"]);
            DbStructureDefinition parameters = fixture.AddStructure(package, "Parameters", FhirArtifactClassEnum.Resource);
            fixture.AddMixedChoiceElement(parameters, "Parameters", []);
            fixture.AddMixedChoiceElement(parameters, "Parameters.parameter", [], max: -1);
            fixture.AddMixedChoiceElement(parameters, "Parameters.parameter.name", ["string"], min: 1);

            DbValueSet? binding = null;
            bool source = package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5;
            if (primitiveAlternative == "code" && !(source && primitiveQualification == "missing-binding"))
            {
                TestValueSet row = new(package)
                {
                    Id = "mixed-choice-primitive",
                    Name = "MixedChoicePrimitive",
                    UnversionedUrl = "http://example.org/ValueSet/mixed-choice-primitive",
                    Version = "primitive-valueset-v3",
                    VersionedUrl = "http://example.org/ValueSet/mixed-choice-primitive|primitive-valueset-v3",
                    CanExpand = false,
                    Compose = new()
                    {
                        Include = [new()
                        {
                            System = MixedPrimitiveSystem,
                            Version = source && primitiveQualification == "unknown-version" ? null
                                : source && primitiveQualification == "version-nonmatch" ? "primitive-v2" : MixedPrimitiveVersion,
                            Concept = source && primitiveQualification == "nonmember"
                                ? [new() { Code = "safe" }] : [new() { Code = "safe" }, new() { Code = "unsafe" }],
                        }],
                    },
                };
                if (source && primitiveQualification == "multi-system")
                {
                    row.Compose.Include.Add(new()
                    {
                        System = "urn:example:ambiguous",
                        Version = "other-v1",
                        Concept = [new() { Code = "safe" }, new() { Code = "unsafe" }],
                    });
                }

                row.Key = DbValueSet.Insert(fixture.Connection, row);
                binding = row;
            }

            fixture.AddMixedChoiceElement(parameters, MixedChoiceElement, ["Coding", primitiveAlternative], binding: binding);
            foreach (DbStructureDefinition structure in DbStructureDefinition.SelectList(fixture.Connection, FhirPackageKey: package.Key))
            {
                structure.SnapshotCount = DbElement.SelectList(fixture.Connection, StructureKey: structure.Key).Count;
                DbStructureDefinition.Update(fixture.Connection, [structure]);
            }
        }

        foreach ((int source, int target) in new[] { (2, 1), (1, 0), (2, 0) })
        {
            string direction = (source == 1 ? "4B" : "5") + "to" + (target == 1 ? "4B" : "4");
            fixture.WriteMixedChoiceMap("resources", direction,
                "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/resource-types",
                "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/resource-types", ["Parameters"]);
            fixture.WriteMixedChoiceMap("types", direction,
                "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/data-types",
                "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/data-types", ["Coding", "Extension"]);
            fixture.WriteMixedChoiceMap("elements", direction,
                "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/element-names",
                "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/element-names",
                [.. DbElement.SelectList(fixture.Connection, FhirPackageKey: fixture.Packages[source].Key).Select(element => element.Id)]);
        }
        fixture.WriteMixedChoiceMap("types", "fallback", "http://hl7.org/fhir/fhir-types", "http://hl7.org/fhir/fhir-types",
            ["boolean", "code", "string", "uri"]);
        fixture.WriteConfiguration(enableRules
            ? primitiveAlternative == "boolean"
                ? """[{"system":"urn:example:codes","code":"unsafe"}]"""
                : """[{"system":"urn:example:codes","code":"unsafe"},{"system":"urn:example:primitive","version":"primitive-v1","code":"unsafe"}]"""
            : "[]");
        return fixture;
    }

    private DbElement AddMixedChoiceElement(
        DbStructureDefinition structure, string id, string[] types, int min = 0, int max = 1, DbValueSet? binding = null)
    {
        DbFhirPackage package = Packages.Single(package => package.Key == structure.FhirPackageKey);
        List<DbElement> siblings = DbElement.SelectList(Connection, StructureKey: structure.Key);
        int separator = id.LastIndexOf('.');
        TestElement row = new(package, structure.Key, id)
        {
            ParentElementKey = separator < 0 ? null : siblings.Single(parent => parent.Id == id[..separator]).Key,
            ResourceFieldOrder = siblings.Count,
            ComponentFieldOrder = siblings.Count,
            MinCardinality = min,
            MaxCardinality = max,
            MaxCardinalityString = max < 0 ? "*" : max.ToString(),
            FullCollatedTypeLiteral = string.Join(',', types),
            DistinctTypeLiterals = string.Join(',', types),
            DistinctTypeCount = types.Length,
            IsChoiceType = types.Length > 1,
            IsSimpleType = types.Length == 1,
            IsInherited = false,
            BaseElementKey = null,
            BaseStructureKey = null,
            BasePath = null,
            DefinedAsContentReference = false,
            ContentReferenceSourceKey = null,
            BindingValueSet = binding?.VersionedUrl,
            BindingValueSetKey = binding?.Key,
            ValueSetBindingStrength = binding is null ? null : BindingStrength.Required,
        };
        row.Key = DbElement.Insert(Connection, row);
        if (row.ParentElementKey is int parentKey)
        {
            DbElement parent = siblings.Single(parent => parent.Key == parentKey);
            parent.ChildElementCount++;
            DbElement.Update(Connection, [parent]);
        }
        foreach (string type in types)
        {
            AddType(row, type, Structure(package, type));
        }
        return row;
    }

    private void WriteMixedChoiceMap(string category, string suffix, string source, string target, string[] ids)
    {
        ConceptMap map = new()
        {
            Id = "mixed-choice-" + category + "-" + suffix,
            Url = "http://example.org/ConceptMap/mixed-choice-" + category + "-" + suffix,
            Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = source,
                Target = target,
                Element = [.. ids.Select(id => new ConceptMap.SourceElementComponent
                {
                    Code = id,
                    Target = [new() { Code = id, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                })],
            }],
        };
        string directory = Path.Combine(SourceRoot, "input", category);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ConceptMap-" + category + "-" + suffix + ".json"), map.ToJson());
    }

    internal SortedDictionary<string, string[]> MixedChoiceRepresentationState()
    {
        // All persisted columns participate; only safety policy/completion may differ between staged twins.
        Dictionary<string, Dictionary<long, SortedDictionary<string, object?>>> rows = [];
        Dictionary<string, Dictionary<string, string>> references = [];
        List<string> tables = [];
        using (SqliteCommand schema = Connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                "AND name NOT IN ('UnsafeCodeInputState', 'UnsafeCodePairState', 'UnsafeCodeRules', 'UnsafeCodeTriggers') ORDER BY name";
            using SqliteDataReader reader = schema.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        foreach (string table in tables)
        {
            rows[table] = [];
            references[table] = [];
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table + "\"";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    SortedDictionary<string, object?> row = new(StringComparer.Ordinal);
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        row.Add(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column));
                    }
                    rows[table].Add((long)row["Key"]!, row);
                }
            }
            command.CommandText = "PRAGMA foreign_key_list(\"" + table + "\")";
            using SqliteDataReader foreignKeys = command.ExecuteReader();
            while (foreignKeys.Read())
            {
                references[table].Add(foreignKeys.GetString(3), foreignKeys.GetString(2));
            }
        }

        Dictionary<(string Table, long Key), string> identities = [];
        HashSet<(string Table, long Key)> visiting = [];
        SortedDictionary<string, string[]> state = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            state.Add(table, [.. rows[table].Keys.Select(key => Normalize(table, key)).Order(StringComparer.Ordinal)]);
        }
        return state;

        string Normalize(string table, long key)
        {
            if (identities.TryGetValue((table, key), out string? cached))
            {
                return cached;
            }
            if (!visiting.Add((table, key)))
            {
                throw new InvalidOperationException("Unexpected cyclic mixed-choice fixture foreign key: " + table);
            }
            SortedDictionary<string, object?> normalized = new(StringComparer.Ordinal);
            foreach ((string column, object? value) in rows[table][key])
            {
                if (column == "Key")
                {
                    continue;
                }
                string? reference = references[table].GetValueOrDefault(column) ?? column switch
                {
                    "FhirPackageKey" or "SourceFhirPackageKey" or "TargetFhirPackageKey" => "FhirPackages",
                    "ConceptMapSourceKey" or "FmlSourceKey" => "MappingSourceFiles",
                    "StructureKey" or "SourceStructureKey" or "TargetStructureKey" or "BaseStructureKey" or
                        "TypeStructureKey" or "TypeProfileStructureKey" or "TargetProfileStructureKey" => "Structures",
                    "ElementKey" or "SourceElementKey" or "TargetElementKey" or "BaseElementKey" or
                        "ParentElementKey" or "ContentReferenceSourceKey" or "ContextElementKey" or
                        "CardinalityContextElementKey" => "Elements",
                    "SourceElementTypeKey" or "TargetElementTypeKey" => "ElementTypes",
                    "BindingValueSetKey" or "SourceValueSetKey" or "TargetValueSetKey" => "ValueSets",
                    "SourceValueSetConceptKey" or "TargetValueSetConceptKey" => "ValueSetConcepts",
                    "StructureMappingKey" => "StructureMappings",
                    "ElementMappingKey" => "ElementMappings",
                    "ValueSetMappingKey" => "ValueSetMappings",
                    "StructureComparisonKey" => "StructureComparisons",
                    "ElementComparisonKey" => "ElementComparisons",
                    "ValueSetComparisonKey" or "BoundValueSetComparisonKey" => "ValueSetComparisons",
                    "StructureOutcomeKey" => "StructureOutcomes",
                    "ElementOutcomeKey" or "ParentElementOutcomeKey" or "AncestorElementOutcomeKey" or
                        "ContentReferenceOutcomeKey" or "SourceAncestorContentReferenceOutcomeKey" => "ElementOutcomes",
                    "ExtensionSubstitutionKey" => "ExtensionSubstitutions",
                    _ => null,
                };
                if (column.StartsWith("ContentKeyR", StringComparison.Ordinal))
                {
                    reference = table switch
                    {
                        "StructureComparisons" => "Structures",
                        "ElementComparisons" => "Elements",
                        "ElementTypeComparisons" => "ElementTypes",
                        "ValueSetComparisons" => "ValueSets",
                        "ValueSetConceptComparisons" => "ValueSetConcepts",
                        _ => throw new InvalidOperationException("Unknown mixed-choice content reference: " + table),
                    };
                }
                if (value is long foreignKey && reference is not null)
                {
                    normalized[column] = Identity(reference, foreignKey);
                }
                else if (value is string literal && column.EndsWith("KeysLiteral", StringComparison.Ordinal))
                {
                    string memberTable = column.Contains("ChildElement", StringComparison.Ordinal) ? "Elements" : "ElementTypes";
                    normalized[column] = literal.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(member => Identity(memberTable, long.Parse(member))).Order(StringComparer.Ordinal).ToArray();
                }
                else if (value is long && column.EndsWith("Key", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unaccounted mixed-choice foreign key: " + table + "." + column);
                }
                else
                {
                    normalized[column] = value;
                }
            }
            visiting.Remove((table, key));
            string result = JsonSerializer.Serialize(normalized);
            identities.Add((table, key), result);
            return result;
        }

        string Identity(string table, long key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(table, key))));
    }

    internal void WriteMixedChoiceStageObservation(FhirPackageComparisonPair pair, string alternative, string qualification)
    {
        WriteMixedChoiceObservation("MEASURED_H2_STAGE", new
        {
            sourceRoot = SourceRoot, alternative, qualification,
            packages = Packages.Select(package => new { package.Key, package.PackageId, package.PackageVersion }),
            input = InputState, state = PairState(pair), reopens = ConnectionReopens,
            choices = DbElement.SelectList(Connection, Id: MixedChoiceElement).Select(element => new
            {
                element.Key, element.FhirPackageKey, element.StructureKey, element.ParentElementKey,
                element.IsChoiceType, element.IsSimpleType, element.DistinctTypeCount, element.DistinctTypeLiterals,
                element.FullCollatedTypeLiteral, element.BindingValueSet, element.BindingValueSetKey,
                types = DbElementType.SelectList(Connection, ElementKey: element.Key),
            }),
            bindings = DbValueSet.SelectList(Connection).Select(binding => new
                { binding.Key, binding.FhirPackageKey, binding.VersionedUrl, binding.Version, binding.CanExpand,
                    includes = binding.Compose!.Include.Select(include => new { include.System, include.Version,
                        codes = include.Concept.Select(concept => concept.Code) }) }),
            maps = DbElementMapping.SelectList(Connection, SourceElementId: MixedChoiceElement).Select(map => new
                { map.Key, map.SourceFhirPackageKey, map.TargetFhirPackageKey, map.SourceElementId, map.TargetElementId,
                    map.ConceptMapFilename, map.ExplicitNoMap }),
            comparisons = DbElementComparison.SelectList(Connection, SourceElementId: MixedChoiceElement).Select(comparison => new
            {
                comparison.Key, comparison.SourceFhirPackageKey, comparison.TargetFhirPackageKey,
                comparison.SourceElementId, comparison.TargetElementId, comparison.ElementMappingKey, comparison.Steps,
                comparison.ContentKeys, comparison.Relationship,
                types = DbElementTypeComparison.SelectList(Connection, ElementComparisonKey: comparison.Key)
                    .Select(type => new { type.SourceTypeLiteral, type.TargetTypeLiteral, type.Relationship,
                        type.SourceElementTypeKey, type.TargetElementTypeKey }),
                targets = DbElementOutcomeTarget.SelectList(Connection, ElementComparisonKey: comparison.Key),
            }),
            outcomes = DbStructureOutcome.SelectList(Connection).Select(outcome => new
                { outcome.Key, outcome.SourceId, outcome.TargetId, outcome.GenUrl }),
            rules = DbUnsafeCodeRule.SelectList(Connection), triggers = DbUnsafeCodeTrigger.SelectList(Connection),
        });
    }

    internal static void WriteMixedChoiceObservation(string label, object observation)
    {
        Console.WriteLine(label + " " + JsonSerializer.Serialize(observation));
    }

    internal string SafetyPersistenceSnapshot() => JsonSerializer.Serialize(new
    {
        Input = DbUnsafeCodeInputState.SelectList(Connection),
        Pairs = DbUnsafeCodePairState.SelectList(Connection),
        Triggers = DbUnsafeCodeTrigger.SelectList(Connection),
        Rules = DbUnsafeCodeRule.SelectList(Connection),
        Evidence = DbUnsafeCodeMapEvidence.SelectList(Connection),
        Annotations = DbUnsafeCodeElementAnnotation.SelectList(Connection),
    });

    internal string SchemaSnapshot()
    {
        using SqliteCommand command = Connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string Name, string Sql)> schema = [];
        while (reader.Read())
        {
            schema.Add((reader.GetString(0), reader.GetString(1)));
        }

        return JsonSerializer.Serialize(schema.Select(table => new { table.Name, table.Sql }));
    }

    internal static UnsafeCodeTestFixture CreateProfileInputs(bool initializeIndices = true, bool persistDatabase = false)
    {
        UnsafeCodeTestFixture fixture = new(initializeIndices, persistDatabase);
        foreach (DbFhirPackage package in fixture.Packages)
        {
            DbStructureDefinition code = fixture.AddStructure(package, "code", FhirArtifactClassEnum.PrimitiveType);
            DbStructureDefinition text = fixture.AddStructure(package, "string", FhirArtifactClassEnum.PrimitiveType);
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddElement(extension, "Extension");
            DbElement value = fixture.AddElement(extension, "Extension.value[x]", "code,string");
            fixture.AddType(value, "code", code);
            fixture.AddType(value, "string", text);

            DbStructureDefinition structure = fixture.Structure(package);
            fixture.AddElement(structure, "ValueSet");
            fixture.AddElement(structure, "ValueSet.compose");
            fixture.AddElement(structure, "ValueSet.compose.include");
            fixture.AddElement(structure, "ValueSet.compose.include.filter");
            DbElement op = fixture.AddElement(structure, FilterElement, "code", FilterValueSet);
            fixture.AddType(op, "code", code);
            if (package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5)
            {
                op.MaxCardinality = -1;
                op.MaxCardinalityString = "*";
                DbElement.Update(fixture.Connection, [op]);
            }

            DbElement filterValue = fixture.AddElement(structure, "ValueSet.compose.include.filter.value", "string");
            fixture.AddType(filterValue, "string", text);
            fixture.SetBinding(package, systemVersion: package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5
                ? "source-system,version" : package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R4 ? "target-system-r4" : "target-system-r4b",
                valueSetVersion: package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5
                    ? "source-vs" : package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R4 ? "target-vs-r4" : "target-vs-r4b");
        }

        return fixture;
    }

    internal static UnsafeCodeTestFixture CreateNoFeatureInputs(bool initializeIndices = true, bool persistDatabase = false)
    {
        UnsafeCodeTestFixture fixture = CreateProfileInputs(initializeIndices, persistDatabase);
        foreach (string filename in Directory.GetFiles(Path.Combine(fixture.SourceRoot, "input", "elements"), "*.json"))
        {
            ConceptMap map = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename));
            foreach (ConceptMap.TargetElementComponent target in map.Group.SelectMany(group => group.Element).SelectMany(element => element.Target))
            {
                target.Property.RemoveAll(property => property.Code == UnsafeCodeConstants.PropertyCode && property.Value is FhirBoolean { Value: true });
            }

            File.WriteAllText(filename, map.ToJson());
        }

        if (File.Exists(fixture.ConfigurationFilename))
        {
            File.Delete(fixture.ConfigurationFilename);
        }

        return fixture;
    }

    internal FhirPackageComparisonPair LoadCompareAndGenerate(int targetIndex)
    {
        LoadProfileMaps();
        FhirPackageComparisonPair pair = new(Packages[2], Packages[targetIndex]);
        CompareProfilePair(pair);
        GenerateProfileOutcomes(pair);
        return pair;
    }

    internal void GenerateProfileOutcomes(params FhirPackageComparisonPair[] pairs)
    {
        new OutcomeGenerator(Connection, NullLoggerFactory.Instance).GenerateOutcomes(processValueSets: false,
            specificPairs: pairs.Select(pair => pair.SequencePair).ToHashSet());
        ReopenPersistedDatabase();
    }

    internal StructureFhirExporter.BuiltDefinitions BuildDefinitions(FhirPackageComparisonPair pair)
    {
        StructureFhirExporter.BuiltDefinitions built = new StructureFhirExporter(new XVerExporter(Connection, new ConfigXVer { XverArtifactVersion = "0.1.0-test" }),
            Connection, NullLoggerFactory.Instance).BuildDefinitions(new IgExporter.XVerIgExportTrackingRecord
            {
                PackagePair = pair,
                PackageId = "hl7.fhir.uv.xver-r5." + pair.TargetFhirSequence.ToRLiteral().ToLowerInvariant(),
            });
        ReopenPersistedDatabase();
        return built;
    }

    private void ReopenPersistedDatabase()
    {
        if (!_persistDatabase)
        {
            return;
        }

        string connectionString = Connection.ConnectionString;
        Connection.Dispose();
        Connection = new(connectionString);
        Connection.Open();
        ConnectionReopens++;
    }

    internal static Hl7.Fhir.Model.ElementDefinition.ConstraintComponent[] SerializedConstraints(
        Hl7.Fhir.Model.StructureDefinition profile, int targetIndex)
    {
        ParserSettings settings = new() { PermissiveParsing = true };
        Hl7.Fhir.Model.StructureDefinition recovered = targetIndex == 0
            ? new r4::Hl7.Fhir.Serialization.FhirJsonParser(settings).Parse<Hl7.Fhir.Model.StructureDefinition>(profile.ToJson())
            : new r4b::Hl7.Fhir.Serialization.FhirJsonParser(settings).Parse<Hl7.Fhir.Model.StructureDefinition>(profile.ToJson());
        return [.. recovered.Differential.Element.SelectMany(element => element.Constraint)
            .Where(constraint => constraint.Key.StartsWith("xv-unsafe-code-", StringComparison.Ordinal))];
    }

    internal static bool EvaluateFilter(
        Hl7.Fhir.Model.ElementDefinition.ConstraintComponent[] constraints, int targetIndex, string? code,
        string? uri = null, string? extension = null, bool mixed = false)
    {
        string filter = "{\"property\":\"status\",\"op\":" + JsonSerializer.Serialize(extension is null ? code : "=") +
            ",\"value\":\"active\"" + (extension is null ? "" : ",\"extension\":[{\"url\":" +
                JsonSerializer.Serialize(extension) + ",\"valueCode\":" + JsonSerializer.Serialize(code) + "}]") + "}";
        string json = "{\"resourceType\":\"ValueSet\",\"status\":\"active\"" +
            (uri is null ? "" : ",\"implicitRules\":" + JsonSerializer.Serialize(uri)) +
            (code is null ? "" : ",\"compose\":{\"include\":[{\"filter\":[" +
                (mixed ? "{\"property\":\"status\",\"op\":\"=\",\"value\":\"active\"}," : "") + filter + "]}]}") + "}";
        ITypedElement instance = TargetInstance(targetIndex, json);
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        bool valid = true;
        foreach (Hl7.Fhir.Model.ElementDefinition.ConstraintComponent constraint in constraints)
        {
            ITypedElement[] results = [.. compiler.Compile(constraint.Expression)(instance, new EvaluationContext())];
            if (results.Length != 1 || results[0].Value is not bool result)
            {
                throw new InvalidOperationException("The serialized invariant did not return exactly one Boolean.");
            }

            valid &= result;
        }

        return valid;
    }

    internal void LoadProfileMaps()
    {
        // The production transitive comparer indexes the package list by release.
        foreach (FhirReleases.FhirSequenceCodes sequence in new[] { FhirReleases.FhirSequenceCodes.DSTU2, FhirReleases.FhirSequenceCodes.STU3 })
        {
            if (DbFhirPackage.SelectSingle(Connection, PackageId: sequence.ToCorePackageId()) is not null)
            {
                continue;
            }

            DbFhirPackage.Insert(Connection, new DbFhirPackage
            {
                Name = sequence.ToCorePackageId(),
                PackageId = sequence.ToCorePackageId(),
                PackageVersion = sequence.ToLongVersion(),
                FhirVersionShort = sequence.ToShortVersion(),
                CanonicalUrl = "http://hl7.org/fhir",
                ShortName = sequence.ToRLiteral(),
                Dependencies = null,
                DefinitionFhirSequence = sequence,
            });
        }

        if (!Load())
        {
            throw new InvalidOperationException("The synthetic cross-version maps did not load.");
        }

        ReopenPersistedDatabase();
    }

    internal void CompareProfilePair(FhirPackageComparisonPair pair)
    {
        CompareProfilePairs(new(Packages[2], Packages[1]), new(Packages[1], Packages[0]), pair);
    }

    internal void CompareProfilePairs(params FhirPackageComparisonPair[] pairs)
    {
        // Match FhirDbComparer.Compare's reset boundary when reusing a database.
        DbComparisonClasses.DropTables(Connection, forValueSets: false);
        DbComparisonClasses.CreateTables(Connection);
        new StructureComparer(Connection, NullLoggerFactory.Instance).CompareStructures(
            specificPairs: pairs.Select(pair => pair.SequencePair).ToHashSet());
        ReopenPersistedDatabase();
    }

    internal static UnsafeCodeTestFixture CreateRepresentationInputs()
    {
        UnsafeCodeTestFixture fixture = new();
        fixture.InsertRepresentationGraph(includeOtherCarriers: true);
        return fixture;
    }

    private void InsertRepresentationGraph(bool includeOtherCarriers)
    {
        UnsafeCodeTestFixture fixture = this;
        foreach (DbFhirPackage package in fixture.Packages)
        {
            foreach (string primitive in new[] { "code", "string", "uri" })
            {
                fixture.AddStructure(package, primitive, FhirArtifactClassEnum.PrimitiveType);
            }

            DbStructureDefinition coding = fixture.AddStructure(package, "Coding");
            fixture.AddElement(coding, "Coding");
            fixture.AddTypedElement(coding, "Coding.system", "uri");
            fixture.AddTypedElement(coding, "Coding.version", "string");
            fixture.AddTypedElement(coding, "Coding.code", "code");
            DbStructureDefinition concept = fixture.AddStructure(package, "CodeableConcept");
            fixture.AddElement(concept, "CodeableConcept");
            fixture.AddTypedElement(concept, "CodeableConcept.coding", "Coding");
            if (includeOtherCarriers)
            {
                DbStructureDefinition quantity = fixture.AddStructure(package, "Quantity");
                fixture.AddElement(quantity, "Quantity");
                fixture.AddTypedElement(quantity, "Quantity.system", "uri");
                fixture.AddTypedElement(quantity, "Quantity.code", "code");
            }
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddElement(extension, "Extension");
            DbElement value = fixture.AddElement(extension, "Extension.value[x]");
            foreach (string type in includeOtherCarriers
                ? new[] { "code", "string", "uri", "Coding", "CodeableConcept", "Quantity" }
                : new[] { "code", "string", "uri", "Coding", "CodeableConcept" })
            {
                fixture.AddType(value, type, fixture.Structure(package, type));
            }

            fixture.AddElement(fixture.Structure(package), "ValueSet");
            if (includeOtherCarriers)
            {
                DbStructureDefinition basic = fixture.AddStructure(package, "Basic", FhirArtifactClassEnum.Resource);
                fixture.AddElement(basic, "Basic");
                fixture.AddTypedElement(basic, "Basic.code", "CodeableConcept");
            }
        }
    }

    internal static UnsafeCodeTestFixture CreateImportedNativeCodingInputs(
        int targetIndex, ImportedNativeScenario scenario, bool enableRule, bool includeLocalContexts = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetIndex, 1);
        UnsafeCodeTestFixture fixture = new(initializeIndices: true, persistDatabase: true);
        fixture.InsertRepresentationGraph(includeOtherCarriers: false);
        if (includeLocalContexts)
        {
            // Bound extensions query terminology outcomes even when this staged run processes only structures.
            DbOutcomeClasses.CreateTables(fixture.Connection, forValueSets: true, forStructures: false);
        }
        Directory.Delete(Path.Combine(fixture.SourceRoot, "input"), recursive: true);
        foreach (DbFhirPackage package in fixture.Packages)
        {
            foreach (string primitive in new[] { "code", "string", "uri" })
            {
                fixture.AddElement(fixture.Structure(package, primitive), primitive);
            }

            bool source = package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5;
            (string system, string version, string code) = source
                ? (ImportedSourceSystem, ImportedSystemVersion, "unsafe") : ImportedTargetIdentity(scenario);
            DbValueSet inner = fixture.AddImportedValueSet(package, "inner", new()
            {
                Include = [new() { System = system, Version = version,
                    Concept = [new() { Code = source && scenario == ImportedNativeScenario.SourceNonmember ||
                        !source && scenario == ImportedNativeScenario.TargetNonmember ? "sibling-only" : code }] }],
            });
            Hl7.Fhir.Model.ValueSet.ComposeComponent compose = new() { Include = [new() { ValueSet = [inner.VersionedUrl] }] };
            if (source && scenario == ImportedNativeScenario.SourceNonmember)
            {
                compose.Include.Add(new() { System = ImportedSourceSystem, Version = ImportedSystemVersion, Concept = [new() { Code = "unsafe" }] });
            }
            if (!source && scenario == ImportedNativeScenario.TargetNonmember)
            {
                compose.Include.Add(new() { System = system, Version = version, Concept = [new() { Code = code }] });
            }

            DbValueSet outer = fixture.AddImportedValueSet(package, "outer", compose);
            DbElement jurisdiction = fixture.AddTypedElement(fixture.Structure(package), ImportedNativeElement, "CodeableConcept");
            jurisdiction.BindingValueSet = outer.VersionedUrl;
            jurisdiction.BindingValueSetKey = outer.Key;
            jurisdiction.MinCardinality = 0;
            jurisdiction.MaxCardinality = -1;
            jurisdiction.MaxCardinalityString = "*";
            DbElement coding = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: package.Key, Id: "CodeableConcept.coding")!;
            coding.MinCardinality = 0;
            coding.MaxCardinality = -1;
            coding.MaxCardinalityString = "*";
            DbElement.Update(fixture.Connection, [jurisdiction, coding]);
            if (includeLocalContexts)
            {
                DbValueSet unrelated = fixture.AddImportedValueSet(package, "unrelated", new()
                {
                    Include = [new() { System = "urn:example:unrelated", Concept = [new() { Code = "safe" }] }],
                });
                foreach (string use in source ? new[] { "sourceUnrelated", "targetUnrelated", "extensionOnly" }
                    : new[] { "sourceUnrelated", "targetUnrelated" })
                {
                    DbElement site = fixture.AddTypedElement(fixture.Structure(package), "ValueSet." + use, "CodeableConcept");
                    DbValueSet binding = source && use == "sourceUnrelated" || !source && use == "targetUnrelated" ? unrelated : outer;
                    site.BindingValueSet = binding.VersionedUrl;
                    site.BindingValueSetKey = binding.Key;
                    DbElement.Update(fixture.Connection, [site]);
                }
            }
        }

        foreach ((int source, int target) in new[] { (2, 1), (1, 0), (2, 0) })
        {
            string direction = (source == 1 ? "4B" : "5") + "to" +
                (target == 1 ? "4B" : "4");
            foreach ((string category, string suffix, string[] types) in new[]
                {
                    ("resources", "resource-types", new[] { "ValueSet" }),
                    ("types", "data-types", new[] { "Coding", "CodeableConcept", "Extension" }),
                })
            {
                fixture.WriteInputMap(new()
                {
                    Id = "imported-native-" + category + "-" + direction,
                    Url = "http://example.org/ConceptMap/imported-native-" + category + "-" + direction,
                    Status = PublicationStatus.Active,
                    Group = [new()
                    {
                        Source = "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/" + suffix,
                        Target = "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/" + suffix,
                        Element = [.. types.Select(type => new ConceptMap.SourceElementComponent
                        {
                            Code = type,
                            Target = [new() { Code = type, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                        })],
                    }],
                }, category, "ConceptMap-" + category + "-" + direction + ".json");
            }

            ConceptMap map = new()
            {
                Id = "imported-native-elements-" + direction,
                Url = "http://example.org/ConceptMap/imported-native-elements-" + direction,
                Status = PublicationStatus.Active,
                Group = [new()
                {
                    Source = "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/element-names",
                    Target = "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/element-names",
                    Element = [.. DbElement.SelectList(fixture.Connection, FhirPackageKey: fixture.Packages[source].Key)
                        .Select(element => new ConceptMap.SourceElementComponent
                        {
                            Code = element.Id,
                            NoMap = element.Id == "ValueSet.extensionOnly" ? true : null,
                            Target = element.Id == "ValueSet.extensionOnly" ? []
                                : [new() { Code = element.Id, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                        })],
                }],
            };
            fixture.WriteInputMap(map, "elements", "ConceptMap-elements-" + direction + ".json");
        }

        fixture.WriteInputMap(new()
        {
            Id = "imported-native-primitives",
            Url = "http://example.org/ConceptMap/imported-native-primitives",
            Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = "http://hl7.org/fhir/fhir-types",
                Target = "http://hl7.org/fhir/fhir-types",
                Element = [.. new[] { "code", "string", "uri" }.Select(type => new ConceptMap.SourceElementComponent
                {
                    Code = type,
                    Target = [new() { Code = type, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                })],
            }],
        }, "types", "ConceptMap-types-fallback.json");
        DbValueSet sourceInner = fixture.ImportedValueSet(fixture.Packages[2], "inner");
        DbValueSet targetInner = fixture.ImportedValueSet(fixture.Packages[targetIndex], "inner");
        (string targetSystem, string targetVersion, string targetCode) = ImportedTargetIdentity(scenario);
        string destination = targetIndex == 0 ? "R4" : "R4B";
        fixture.WriteInputMap(new()
        {
            Id = "imported-native-R5-" + destination,
            Url = "http://example.org/ConceptMap/imported-native-R5-" + destination,
            Status = PublicationStatus.Active,
            SourceScope = new Hl7.Fhir.Model.Canonical(scenario == ImportedNativeScenario.SourceValueSetVersionMismatch
                ? sourceInner.UnversionedUrl + "|wrong-vs-version" : sourceInner.VersionedUrl),
            TargetScope = new Hl7.Fhir.Model.Canonical(scenario == ImportedNativeScenario.TargetValueSetVersionMismatch
                ? targetInner.UnversionedUrl + "|wrong-vs-version" : targetInner.VersionedUrl),
            Group = [new()
            {
                Source = ImportedSourceSystem + "|" + (scenario == ImportedNativeScenario.SourceSystemVersionMismatch ? "conflicting-system-version" : ImportedSystemVersion),
                Target = targetSystem + "|" + (scenario == ImportedNativeScenario.TargetSystemVersionMismatch ? "conflicting-system-version" : targetVersion),
                Element = [new() { Code = "unsafe", Target = [new() { Code = targetCode, Relationship = ConceptMap.ConceptMapRelationship.RelatedTo }] }],
            }],
        }, "codes", "ConceptMap-R5-imported-native-" + destination + ".json");
        if (enableRule)
        {
            fixture.WriteConfiguration("""[{"system":"urn:example:source","code":"unsafe"}]""");
        }

        return fixture;
    }

    internal static (string System, string Version, string Code) ImportedTargetIdentity(ImportedNativeScenario scenario) => scenario switch
    {
        ImportedNativeScenario.IdentityPreserving => (ImportedSourceSystem, ImportedSystemVersion, "unsafe"),
        ImportedNativeScenario.CodeOnly => (ImportedSourceSystem, ImportedSystemVersion, "changed"),
        ImportedNativeScenario.SystemOnly => ("urn:example:target", ImportedSystemVersion, "unsafe"),
        ImportedNativeScenario.SystemVersionOnly => (ImportedSourceSystem, "system-version-8", "unsafe"),
        _ => ("urn:example:target", ImportedSystemVersion, "changed"),
    };

    internal void WriteEquivalentImportedMaps(int targetIndex)
    {
        string destination = targetIndex == 0 ? "R4" : "R4B";
        ConceptMap original = ReadMap("codes", "ConceptMap-R5-imported-native-" + destination + ".json");
        foreach (DbFhirPackage package in new[] { Packages[2], Packages[targetIndex] })
        {
            DbValueSet inner = ImportedValueSet(package, "inner");
            inner.Compose!.Include[0].Concept.Add(new() { Code = "safe" });
            DbValueSet.Update(Connection, [inner]);
        }
        foreach (string name in new[] { "A", "z" })
        {
            ConceptMap map = (ConceptMap)original.DeepCopy();
            map.Id = name == "A" ? null : "equivalent-imported-" + destination;
            map.Url = "http://example.org/ConceptMap/equivalent-imported-" + name + "-" + destination;
            map.Group = [];
            for (int group = 0; group < 3; group++)
            {
                map.Group.Add(new()
                {
                    Source = group == 2 ? ImportedSourceSystem : original.Group[0].Source,
                    Target = group == 2 ? original.Group[0].Target.Split('|')[0] : original.Group[0].Target,
                    Element = [.. new[] { "unsafe", "safe" }.Select(code => new ConceptMap.SourceElementComponent
                    {
                        Code = code,
                        Target = [.. Enumerable.Range(0, group == 2 ? 1 : 2).Select(index => new ConceptMap.TargetElementComponent
                        {
                            Code = code == "unsafe" ? original.Group[0].Element[0].Target[0].Code : code,
                            Relationship = index == 0 ? ConceptMap.ConceptMapRelationship.RelatedTo : ConceptMap.ConceptMapRelationship.Equivalent,
                        })],
                    })],
                });
            }

            WriteInputMap(map, "codes", "ConceptMap-R5-" + name + "-equivalent-imported-" + destination + ".json");
        }
    }

    internal void WriteInputMap(ConceptMap map, string category, string filename)
    {
        string directory = Path.Combine(SourceRoot, "input", category);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, filename), map.ToJson());
    }

    internal DbValueSet ImportedValueSet(DbFhirPackage package, string scope) =>
        DbValueSet.SelectSingle(Connection, FhirPackageKey: package.Key, Id: "imported-native-" + scope)!;

    private DbValueSet AddImportedValueSet(DbFhirPackage package, string scope, Hl7.Fhir.Model.ValueSet.ComposeComponent compose)
    {
        string release = package.DefinitionFhirSequence.ToRLiteral().ToLowerInvariant();
        TestValueSet row = new(package)
        {
            Id = "imported-native-" + scope,
            Name = "ImportedNative" + scope,
            UnversionedUrl = "http://example.org/ValueSet/" + release + "-" + scope,
            Version = scope + "-vs-" + release + "-3",
            Compose = compose,
            CanExpand = false,
        };
        row.VersionedUrl = row.UnversionedUrl + "|" + row.Version;
        row.Key = DbValueSet.Insert(Connection, row);
        return row;
    }

    internal static bool EvaluateImportedNativeCondition(string expression, int targetIndex, string? system, string? code, string? uri = null,
        bool mixed = false)
    {
        string json = "{\"resourceType\":\"ValueSet\",\"status\":\"active\"" +
            (uri is null ? "" : ",\"implicitRules\":" + JsonSerializer.Serialize(uri)) +
            (code is null ? "" : ",\"jurisdiction\":[{\"coding\":[" +
                (mixed ? "{\"system\":\"urn:example:source\",\"code\":\"safe\"}," : "") +
                "{\"system\":" + JsonSerializer.Serialize(system) + ",\"code\":" + JsonSerializer.Serialize(code) + "}]}]") + "}";
        ITypedElement instance = TargetInstance(targetIndex, json);
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        ITypedElement[] results = [.. compiler.Compile(expression)(instance, new EvaluationContext())];
        if (results.Length != 1 || results[0].Value is not bool result)
        {
            throw new InvalidOperationException("The recovered imported-native condition did not return exactly one Boolean.");
        }

        return result;
    }

    internal SortedDictionary<string, string[]> ImportedNativeSemanticState()
    {
        // Compare every persisted field, replacing only primary/foreign keys with semantic identities.
        List<string> tables = [];
        using (SqliteCommand schema = Connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                "AND name NOT IN ('UnsafeCodeInputState', 'UnsafeCodePairState', 'UnsafeCodeRules', 'UnsafeCodeTriggers') ORDER BY name";
            using SqliteDataReader reader = schema.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        Dictionary<string, Dictionary<long, SortedDictionary<string, object?>>> rows = [];
        Dictionary<string, Dictionary<string, string>> references = [];
        foreach (string table in tables)
        {
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table + "\"";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                rows[table] = [];
                while (reader.Read())
                {
                    SortedDictionary<string, object?> row = new(StringComparer.Ordinal);
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        row.Add(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column));
                    }

                    rows[table].Add((long)row["Key"]!, row);
                }
            }

            command.CommandText = "PRAGMA foreign_key_list(\"" + table + "\")";
            using SqliteDataReader foreignKeys = command.ExecuteReader();
            references[table] = [];
            while (foreignKeys.Read())
            {
                references[table].Add(foreignKeys.GetString(3), foreignKeys.GetString(2));
            }
        }

        Dictionary<(string Table, long Key), string> identities = [];
        SortedDictionary<string, string[]> state = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            state.Add(table, [.. rows[table].Keys.Select(key => Normalize(table, key)).Order(StringComparer.Ordinal)]);
        }

        return state;

        string Normalize(string table, long key)
        {
            if (identities.TryGetValue((table, key), out string? cached))
            {
                return cached;
            }

            SortedDictionary<string, object?> normalized = new(StringComparer.Ordinal);
            foreach ((string column, object? value) in rows[table][key])
            {
                if (column == "Key")
                {
                    continue;
                }

                string? reference = references[table].GetValueOrDefault(column);
                reference ??= column switch
                {
                    "ParentElementKey" or "ContentReferenceSourceKey" => "Elements",
                    "BindingValueSetKey" => "ValueSets",
                    _ => null,
                };
                if (column.StartsWith("ContentKeyR", StringComparison.Ordinal))
                {
                    reference = table switch
                    {
                        "StructureComparisons" => "Structures",
                        "ElementComparisons" => "Elements",
                        "ElementTypeComparisons" => "ElementTypes",
                        "ValueSetComparisons" => "ValueSets",
                        "ValueSetConceptComparisons" => "ValueSetConcepts",
                        _ => throw new InvalidOperationException("Unknown comparison content key: " + table),
                    };
                }

                if (value is long foreignKey && reference is not null)
                {
                    normalized[column] = Identity(reference, foreignKey);
                }
                else if (value is string literal && column.EndsWith("KeysLiteral", StringComparison.Ordinal))
                {
                    string memberTable = column.Contains("ChildElement", StringComparison.Ordinal) ? "Elements" : "ElementTypes";
                    normalized[column] = literal.Split(',').Select(member => Identity(memberTable, long.Parse(member))).ToArray();
                }
                else
                {
                    normalized[column] = value;
                }
            }

            string result = JsonSerializer.Serialize(normalized);
            identities.Add((table, key), result);
            return result;
        }

        string Identity(string table, long key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(table, key))));
    }

    internal DbElement AddTypedElement(DbStructureDefinition structure, string id, string type)
    {
        DbElement element = AddElement(structure, id, type);
        AddType(element, type, Structure(Packages.Single(package => package.Key == structure.FhirPackageKey), type));
        return element;
    }

    internal DbStructureOutcome[] AddResourceOwnerGraph(FhirPackageComparisonPair pair, params string[] resourceTypes)
    {
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            AddElement(AddStructure(package, "Resource", FhirArtifactClassEnum.Resource), "Resource");
            foreach (string type in resourceTypes)
            {
                DbStructureDefinition resource = AddStructure(package, type, FhirArtifactClassEnum.Resource);
                AddElement(resource, type);
                AddTypedElement(resource, type + ".contained", "Resource");
                if (type == "Bundle")
                {
                    AddTypedElement(resource, "Bundle.type", "code");
                    AddElement(resource, "Bundle.entry");
                    AddTypedElement(resource, "Bundle.entry.resource", "Resource");
                }
                else if (type == "Parameters")
                {
                    AddElement(resource, "Parameters.parameter");
                    AddTypedElement(resource, "Parameters.parameter.resource", "Resource");
                    DbElement value = AddTypedElement(resource, "Parameters.parameter.value[x]", "Coding");
                    value.IsChoiceType = true;
                    DbElement.Update(Connection, [value]);
                }
                else if (type == "Observation")
                {
                    AddTypedElement(resource, "Observation.code", "CodeableConcept");
                }
            }
        }

        return [.. resourceTypes.Select(type => AddOutcome(pair, Structure(pair.SourcePackage, type), Structure(pair.TargetPackage, type)))];
    }

    internal static ITypedElement TargetInstance(int targetIndex, string json)
    {
        ModelInspector inspector = targetIndex == 0
            ? r4::Hl7.Fhir.Model.ModelInfo.ModelInspector
            : r4b::Hl7.Fhir.Model.ModelInfo.ModelInspector;
        BaseFhirJsonPocoDeserializer deserializer = new(inspector);
        Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 512 });
        return deserializer.DeserializeResource(ref reader).ToTypedElement(inspector);
    }

    internal DbElementOutcome AddRepresentationOutcome(FhirPackageComparisonPair pair, DbElement source,
        string? context = null, DbElementOutcome? parent = null, bool cardinalityOnly = false)
    {
        TestElementOutcome outcome = new(pair, source)
        {
            RequiresXVerDefinition = true,
            RequiresExtensionDefinition = !cardinalityOnly && parent is null,
            RequiresCardinalityDefinition = cardinalityOnly,
            RequiresSliceDefinition = parent is not null,
            ParentElementOutcomeKey = parent?.Key,
            ExtensionContextsLiteral = cardinalityOnly ? null : context,
            CardinalityExtensionContextsLiteral = cardinalityOnly ? context : null,
        };
        outcome.Key = DbElementOutcome.Insert(Connection, outcome);
        return outcome;
    }

    internal void WriteConfiguration(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFilename)!);
        File.WriteAllText(ConfigurationFilename, json);
    }

    internal ConceptMap ReadMap(string category, string filename) =>
        new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(Path.Combine(SourceRoot, "input", category, filename)));

    internal void Capture(ConceptMap map, string category = "elements")
    {
        UnsafeCodeSourceReader.Capture(Connection, map, category, Path.Combine(SourceRoot, "test.json"), "input/test.json", Packages[2].Key, Packages[0].Key);
    }

    internal void SetExpandable(bool value)
    {
        List<DbValueSet> rows = DbValueSet.SelectList(Connection);
        foreach (DbValueSet row in rows)
        {
            row.CanExpand = value;
        }

        rows.Update(Connection);
    }

    internal void InsertFilterElements()
    {
        foreach (DbFhirPackage package in Packages)
        {
            TestElement row = new(package, DbStructureDefinition.SelectSingle(Connection, FhirPackageKey: package.Key, Id: "ValueSet")!.Key, FilterElement);
            DbElement.Insert(Connection, row);
        }
    }

    internal FhirPackageComparisonPair PreparePair(int source = 2, int target = 0)
    {
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(Connection, Packages);
        DbComparisonClasses.CreateTables(Connection);
        DbOutcomeClasses.CreateTables(Connection, forValueSets: false);
        FhirPackageComparisonPair pair = new(Packages[source], Packages[target]);
        StampComparison(pair);
        return pair;
    }

    internal void StampComparison(FhirPackageComparisonPair pair)
    {
        string? revision = UnsafeCodeInputStore.BeginComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
        UnsafeCodeInputStore.CompleteComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey, revision);
    }

    internal void GenerateSafety(FhirPackageComparisonPair pair) =>
        new UnsafeCodeOutcomeGenerator(Connection).GenerateForPair(pair);

    internal void AddTrigger(DbStructureOutcome outcome, ImmutableArray<UnsafeCodePathStep> path,
        string system = FilterSystem, string code = "unsafe", string? sourceVersion = null,
        bool wildcard = true, params string[] versions)
    {
        DbUnsafeCodeTrigger.Insert(Connection, new DbUnsafeCodeTrigger
        {
            StructureOutcomeKey = outcome.Key,
            SourceFhirPackageKey = outcome.SourceFhirPackageKey,
            TargetFhirPackageKey = outcome.TargetFhirPackageKey,
            SourceElementId = path[^1].ElementId,
            TargetElementId = "",
            SourcePathJson = JsonSerializer.Serialize(path),
            System = system,
            Code = code,
            SourceVersion = sourceVersion,
            IsWildcardVersion = wildcard,
            VersionsJson = JsonSerializer.Serialize(versions),
            EvidenceKeysJson = "[]",
            RuleKeysJson = "[]",
        });
    }

    internal DbUnsafeCodePairState PairState(FhirPackageComparisonPair pair) =>
        DbUnsafeCodePairState.SelectSingle(Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)!;

    internal DbStructureDefinition AddStructure(DbFhirPackage package, string id, FhirArtifactClassEnum artifactClass = FhirArtifactClassEnum.ComplexType)
    {
        TestStructure row = new(package)
        {
            Id = id,
            Name = id,
            ArtifactClass = artifactClass,
            UnversionedUrl = "http://example.org/StructureDefinition/" + id,
            VersionedUrl = "http://example.org/StructureDefinition/" + id + "|" + package.PackageVersion,
        };
        row.Key = DbStructureDefinition.Insert(Connection, row);
        return row;
    }

    internal DbStructureDefinition Structure(DbFhirPackage package, string id = "ValueSet") =>
        DbStructureDefinition.SelectSingle(Connection, FhirPackageKey: package.Key, Id: id)!;

    internal DbElement AddElement(DbStructureDefinition structure, string id, string type = "", string? binding = null, BindingStrength strength = BindingStrength.Required)
    {
        DbFhirPackage package = Packages.Single(package => package.Key == structure.FhirPackageKey);
        List<DbElement> siblings = DbElement.SelectList(Connection, StructureKey: structure.Key);
        int separator = id.LastIndexOf('.');
        TestElement row = new(package, structure.Key, id)
        {
            ResourceFieldOrder = siblings.Count,
            ParentElementKey = separator < 0 ? null : siblings.SingleOrDefault(parent => parent.Id == id[..separator])?.Key,
            FullCollatedTypeLiteral = type,
            DistinctTypeLiterals = type,
            DistinctTypeCount = type.Length == 0 ? 0 : 1,
            BindingValueSet = binding,
            ValueSetBindingStrength = strength,
        };
        row.Key = DbElement.Insert(Connection, row);
        if (row.ParentElementKey is int parentKey)
        {
            DbElement parent = siblings.Single(parent => parent.Key == parentKey);
            parent.ChildElementCount++;
            DbElement.Update(Connection, [parent]);
        }

        return row;
    }

    internal void AddType(DbElement element, string typeName, DbStructureDefinition type, DbStructureDefinition? profile = null)
    {
        DbElementType.Insert(Connection, new DbElementType
        {
            FhirPackageKey = element.FhirPackageKey,
            StructureKey = element.StructureKey,
            ElementKey = element.Key,
            TypeName = typeName,
            TypeStructureKey = type.Key,
            TypeProfile = profile?.VersionedUrl,
            TypeProfileStructureKey = profile?.Key,
            TargetProfile = null,
            TargetProfileStructureKey = null,
        });
    }

    internal DbValueSet SetBinding(DbFhirPackage package, string system = FilterSystem, string? systemVersion = null, string valueSetVersion = "source-vs")
    {
        DbValueSet row = DbValueSet.SelectSingle(Connection, FhirPackageKey: package.Key, Id: "filter-operator")!;
        row.Version = valueSetVersion;
        row.VersionedUrl = row.UnversionedUrl + "|" + valueSetVersion;
        row.Compose = new() { Include = [new() { System = system, Version = systemVersion }] };
        DbValueSet.Update(Connection, [row]);
        return row;
    }

    internal DbStructureOutcome AddOutcome(FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition? target = null)
    {
        TestStructureOutcome row = new(pair, source, target);
        row.Key = DbStructureOutcome.Insert(Connection, row);
        return row;
    }

    internal void AddRule(string system = FilterSystem, string code = "unsafe", string? version = null, bool noMapOnly = false,
        string excludedRelationships = "", string excludedVersions = "")
    {
        DbUnsafeCodeRule.Insert(Connection, new DbUnsafeCodeRule
        {
            System = system,
            Code = code,
            Version = version,
            NoMapOnly = noMapOnly,
            ExcludedRelationshipLiteral = excludedRelationships,
            ExcludedVersionLiteral = excludedVersions,
            SourceRelativeFilename = "input/ig-support/unsafe-code-mappings.json",
            EntryIndex = DbUnsafeCodeRule.SelectList(Connection).Count,
        });
    }

    internal void AddAnnotation(FhirPackageComparisonPair pair, DbElement source, DbElement target)
    {
        DbUnsafeCodeElementAnnotation.Insert(Connection, new DbUnsafeCodeElementAnnotation
        {
            SourceFhirPackageKey = pair.SourcePackageKey,
            TargetFhirPackageKey = pair.TargetPackageKey,
            SourceElementId = source.Id,
            TargetElementId = target.Id,
            RequiresOmittedMandatoryCode = true,
            SourceRelativeFilename = "input/elements/test.json",
            GroupIndex = 0,
            SourceIndex = 0,
            TargetIndex = 0,
        });
    }

    internal DbUnsafeCodeMapEvidence AddEvidence(FhirPackageComparisonPair pair, string code = "unsafe", string? sourceScope = FilterValueSet,
        string? targetScope = FilterValueSet, string? sourceVersion = null, string? targetVersion = null, bool noMap = true,
        ConceptMap.ConceptMapRelationship? relationship = null, string sourceSystem = FilterSystem)
    {
        DbUnsafeCodeMapEvidence row = new()
        {
            SourceFhirPackageKey = pair.SourcePackageKey,
            TargetFhirPackageKey = pair.TargetPackageKey,
            SourceValueSetUrl = sourceScope,
            TargetValueSetUrl = targetScope,
            SourceValueSetVersion = sourceVersion,
            TargetValueSetVersion = targetVersion,
            SourceSystem = sourceSystem,
            SourceCode = code,
            ExplicitNoMap = noMap,
            Relationship = relationship,
            SourceRelativeFilename = "input/codes/test.json",
            GroupIndex = 0,
            SourceIndex = DbUnsafeCodeMapEvidence.SelectList(Connection).Count,
        };
        row.Key = DbUnsafeCodeMapEvidence.Insert(Connection, row);
        return row;
    }

    internal void Connect(FhirPackageComparisonPair pair, DbStructureOutcome outcome, DbElement source, DbElement target, DbElementOutcome? existing = null)
    {
        TestElementMapping mapping = new(pair, source, target);
        mapping.Key = DbElementMapping.Insert(Connection, mapping);
        TestElementComparison comparison = new(pair, source, target) { ElementMappingKey = mapping.Key };
        comparison.Key = DbElementComparison.Insert(Connection, comparison);
        DbElementOutcome elementOutcome = existing ?? new TestElementOutcome(pair, source);
        if (existing is null)
        {
            elementOutcome.Key = DbElementOutcome.Insert(Connection, elementOutcome);
        }
        DbElementOutcomeTarget.Insert(Connection, new TestElementTarget(pair, outcome, elementOutcome, comparison, target));
    }

    private sealed class TestStructureOutcome : DbStructureOutcome
    {
        [SetsRequiredMembers]
        internal TestStructureOutcome(FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition? target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceStructureKey = source.Key;
            TargetStructureKey = target?.Key;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceCanonicalVersioned = source.VersionedUrl;
            SourceCanonicalUnversioned = source.UnversionedUrl;
            SourceVersion = source.Version;
            SourceArtifactClass = source.ArtifactClass;
            TargetArtifactClass = target?.ArtifactClass;
            TargetId = target?.Id;
            TargetName = target?.Name;
            TargetCanonicalUnversioned = target?.UnversionedUrl;
            TargetCanonicalVersioned = target?.VersionedUrl;
            GenShortId = "profile-" + source.Id;
            GenName = "Profile" + source.Name;
            GenUrl = "http://example.org/StructureDefinition/profile-" + source.Id;
            GenFileName = "StructureDefinition-profile-" + source.Id;
            Comments = "";
        }
    }

    private sealed class TestElementMapping : DbElementMapping
    {
        [SetsRequiredMembers]
        internal TestElementMapping(FhirPackageComparisonPair pair, DbElement source, DbElement target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceStructureKey = source.StructureKey;
            TargetStructureKey = target.StructureKey;
            SourceElementKey = source.Key;
            TargetElementKey = target.Key;
            SourceElementId = source.Id;
            TargetElementId = target.Id;
        }
    }

    private sealed class TestElementComparison : DbElementComparison
    {
        [SetsRequiredMembers]
        internal TestElementComparison(FhirPackageComparisonPair pair, DbElement source, DbElement target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceStructureKey = source.StructureKey;
            TargetStructureKey = target.StructureKey;
            SourceElementKey = source.Key;
            TargetElementKey = target.Key;
            SourceElementId = source.Id;
            TargetElementId = target.Id;
        }
    }

    private sealed class TestElementOutcome : DbElementOutcome
    {
        [SetsRequiredMembers]
        internal TestElementOutcome(FhirPackageComparisonPair pair, DbElement source)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceStructureKey = source.StructureKey;
            SourceElementKey = source.Key;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceCanonicalVersioned = "";
            SourceCanonicalUnversioned = "";
            SourceVersion = "";
            SourceMaxCardinalityString = source.MaxCardinalityString;
            SourceMinCardinality = source.MinCardinality;
            SourceResourceOrder = source.ResourceFieldOrder;
            SourceChildElementCount = source.ChildElementCount;
            GenShortId = "ext-" + source.Id;
            GenName = "Extension" + source.Id.Replace(".", "_", StringComparison.Ordinal);
            GenUrl = "http://example.org/StructureDefinition/ext-" + source.Id;
            GenFileName = "StructureDefinition-ext-" + source.Id;
            GenSliceName = "fixed-local-" + source.Name;
            Comments = "";
        }
    }

    private sealed class TestElementTarget : DbElementOutcomeTarget
    {
        [SetsRequiredMembers]
        internal TestElementTarget(FhirPackageComparisonPair pair, DbStructureOutcome outcome, DbElementOutcome element, DbElementComparison comparison, DbElement target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            ElementOutcomeKey = element.Key;
            StructureOutcomeKey = outcome.Key;
            ElementComparisonKey = comparison.Key;
            TargetStructureKey = target.StructureKey;
            TargetElementKey = target.Key;
            TargetElementId = target.Id;
        }
    }

    private void InsertStructure(DbFhirPackage package)
    {
        TestStructure row = new(package);
        row.Key = DbStructureDefinition.Insert(Connection, row);
    }

    private void InsertValueSet(DbFhirPackage package)
    {
        TestValueSet row = new(package);
        row.Key = DbValueSet.Insert(Connection, row);
    }

    // Test-only subclasses avoid repeating the nullable metadata fields on these minimal definitions.
    private sealed class TestStructure : DbStructureDefinition
    {
        [SetsRequiredMembers]
        internal TestStructure(DbFhirPackage package)
        {
            FhirPackageKey = package.Key;
            Id = "ValueSet";
            Name = "ValueSet";
            Version = package.PackageVersion;
            UnversionedUrl = "http://hl7.org/fhir/StructureDefinition/ValueSet";
            VersionedUrl = UnversionedUrl + "|" + package.PackageVersion;
            Status = PublicationStatus.Active;
            ArtifactClass = FhirArtifactClassEnum.Resource;
        }
    }

    private sealed class TestValueSet : DbValueSet
    {
        [SetsRequiredMembers]
        internal TestValueSet(DbFhirPackage package)
        {
            FhirPackageKey = package.Key;
            Id = "filter-operator";
            Name = "FilterOperator";
            Version = package.PackageVersion;
            UnversionedUrl = FilterValueSet;
            VersionedUrl = UnversionedUrl + "|" + package.PackageVersion;
            Status = PublicationStatus.Active;
        }
    }

    private sealed class TestElement : DbElement
    {
        [SetsRequiredMembers]
        internal TestElement(DbFhirPackage package, int structureKey, string id)
        {
            FhirPackageKey = package.Key;
            StructureKey = structureKey;
            Id = id;
            Path = id;
            Name = id.Split('.')[^1];
            MinCardinality = 1;
            MaxCardinality = 1;
            MaxCardinalityString = "1";
            FullCollatedTypeLiteral = "code";
            DistinctTypeLiterals = "code";
            DistinctTypeCount = 1;
            BindingValueSet = FilterValueSet;
            ValueSetBindingStrength = BindingStrength.Required;
            IsSimpleType = true;
        }
    }

    public void Dispose()
    {
        Connection.Dispose();
        if (Directory.Exists(SourceRoot))
        {
            Directory.Delete(SourceRoot, true);
        }
    }
}
