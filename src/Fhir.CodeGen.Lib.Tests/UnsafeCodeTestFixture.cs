// <copyright file="UnsafeCodeTestFixture.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

extern alias r4;
extern alias r4b;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
            DbStructureDefinition quantity = fixture.AddStructure(package, "Quantity");
            fixture.AddElement(quantity, "Quantity");
            fixture.AddTypedElement(quantity, "Quantity.system", "uri");
            fixture.AddTypedElement(quantity, "Quantity.code", "code");
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddElement(extension, "Extension");
            DbElement value = fixture.AddElement(extension, "Extension.value[x]");
            foreach (string type in new[] { "code", "string", "uri", "Coding", "CodeableConcept", "Quantity" })
            {
                fixture.AddType(value, type, fixture.Structure(package, type));
            }

            fixture.AddElement(fixture.Structure(package), "ValueSet");
            DbStructureDefinition basic = fixture.AddStructure(package, "Basic", FhirArtifactClassEnum.Resource);
            fixture.AddElement(basic, "Basic");
            fixture.AddTypedElement(basic, "Basic.code", "CodeableConcept");
        }

        return fixture;
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
