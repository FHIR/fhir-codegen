// <copyright file="UnsafeCodeTestFixture.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fhir.CodeGen.Lib.Tests;

internal sealed class UnsafeCodeTestFixture : IDisposable
{
    internal const string FilterSystem = "http://hl7.org/fhir/filter-operator";
    internal const string FilterValueSet = "http://hl7.org/fhir/ValueSet/filter-operator";
    internal const string FilterElement = "ValueSet.compose.include.filter.op";

    internal SqliteConnection Connection { get; }
    internal string SourceRoot { get; }
    internal string ConfigurationFilename => Path.Combine(SourceRoot, "input", "ig-support", "unsafe-code-mappings.json");
    internal List<DbFhirPackage> Packages { get; } = [];
    internal DbUnsafeCodeInputState InputState => DbUnsafeCodeInputState.SelectSingle(Connection, Key: 1)!;

    internal UnsafeCodeTestFixture()
    {
        Connection = new("Data Source=:memory:");
        Connection.Open();
        SourceRoot = Path.Combine(Path.GetTempPath(), "fhir-codegen-unsafe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(SourceRoot);
            string input = Path.Combine(AppContext.BaseDirectory, "TestData", "XverUnsafeCodes");
            foreach (string filename in Directory.GetFiles(input, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(SourceRoot, Path.GetRelativePath(input, filename));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(filename, destination);
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

    internal void Connect(FhirPackageComparisonPair pair, DbStructureOutcome outcome, DbElement source, DbElement target)
    {
        TestElementMapping mapping = new(pair, source, target);
        mapping.Key = DbElementMapping.Insert(Connection, mapping);
        TestElementComparison comparison = new(pair, source, target) { ElementMappingKey = mapping.Key };
        comparison.Key = DbElementComparison.Insert(Connection, comparison);
        TestElementOutcome elementOutcome = new(pair, source);
        elementOutcome.Key = DbElementOutcome.Insert(Connection, elementOutcome);
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
