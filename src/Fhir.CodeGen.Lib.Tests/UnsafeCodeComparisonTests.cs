// <copyright file="UnsafeCodeComparisonTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class UnsafeCodeComparisonTests
{
    [Fact]
    public void MultipleAuthoredTargetsPersistTheirOwnElementMappingKeys()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        DbStructureDefinition sourceStructure = fixture.Structure(pair.SourcePackage);
        DbStructureDefinition targetStructure = fixture.Structure(pair.TargetPackage);
        DbElement source = Element(fixture, sourceStructure, UnsafeCodeTestFixture.FilterElement);
        DbElement target = Element(fixture, targetStructure, UnsafeCodeTestFixture.FilterElement);
        DbElement renamed = fixture.AddElement(targetStructure, "ValueSet.compose.include.filter.renamed", "code", UnsafeCodeTestFixture.FilterValueSet);
        fixture.AddType(renamed, "code", fixture.Structure(pair.TargetPackage, "code"));
        renamed.MaxCardinality = -1;
        renamed.MaxCardinalityString = "*";

        DbValueSet sourceValueSet = fixture.SetBinding(pair.SourcePackage);
        DbValueSet targetValueSet = fixture.SetBinding(pair.TargetPackage);
        source.BindingValueSetKey = sourceValueSet.Key;
        target.BindingValueSetKey = targetValueSet.Key;
        renamed.BindingValueSetKey = targetValueSet.Key;
        DbElement.Update(fixture.Connection, [source, target, renamed]);
        TestValueSetComparison binding = new(pair, sourceValueSet, targetValueSet);
        binding.Key = DbValueSetComparison.Insert(fixture.Connection, binding);

        // Offset the mapping-family keys so a structure key cannot accidentally look correct.
        foreach (string type in new[] { "code", "string", "Extension" })
        {
            AddStructureMapping(fixture, pair, fixture.Structure(pair.SourcePackage, type), fixture.Structure(pair.TargetPackage, type));
        }

        DbStructureMapping structureMapping = AddStructureMapping(fixture, pair, sourceStructure, targetStructure);
        DbElementMapping[] mappings =
        [
            new TestElementMapping(pair, structureMapping, source, target),
            new TestElementMapping(pair, structureMapping, source, renamed) { Relationship = CMR.SourceIsNarrowerThanTarget },
            new TestElementMapping(pair, structureMapping, source, null),
        ];
        foreach (DbElementMapping mapping in mappings)
        {
            mapping.Key = DbElementMapping.Insert(fixture.Connection, mapping);
        }
        mappings.ShouldAllBe(mapping => mapping.Key != structureMapping.Key);

        Compare(fixture, pair);
        List<DbElementComparison> comparisons = DbElementComparison.SelectList(fixture.Connection,
            SourceElementKey: source.Key, TargetFhirPackageKey: pair.TargetPackageKey);
        comparisons.Count.ShouldBe(3);
        comparisons.Select(comparison => comparison.TargetElementKey).OrderBy(key => key)
            .ShouldBe(new int?[] { null, target.Key, renamed.Key }.OrderBy(key => key));

        DbElementComparison originalTarget = comparisons.Single(comparison => comparison.TargetElementKey == target.Key);
        originalTarget.Relationship.ShouldBe(CMR.SourceIsBroaderThanTarget);
        originalTarget.SourceAllowsMoreValues.ShouldBe(true);
        originalTarget.TargetAllowsMoreValues.ShouldBe(false);
        originalTarget.IsIdentical.ShouldBe(true);
        DbElementComparison renamedTarget = comparisons.Single(comparison => comparison.TargetElementKey == renamed.Key);
        renamedTarget.Relationship.ShouldBe(CMR.SourceIsNarrowerThanTarget);
        renamedTarget.SourceAllowsMoreValues.ShouldBe(false);
        renamedTarget.TargetAllowsMoreValues.ShouldBe(false);
        renamedTarget.RelativePathsAreIdentical.ShouldBe(false);

        foreach (DbElementComparison comparison in new[] { originalTarget, renamedTarget })
        {
            comparison.NotMapped.ShouldBeFalse();
            comparison.SourceRequiresMoreValues.ShouldBe(false);
            comparison.TargetRequiresMoreValues.ShouldBe(false);
            comparison.TypeRelationship.ShouldBe(CMR.Equivalent);
            comparison.BoundValueSetComparisonKey.ShouldBe(binding.Key);
            comparison.BoundValueSetRelationship.ShouldBe(CMR.Equivalent);
            comparison.ConceptDomainRelationship.ShouldBe(CMR.Equivalent);
            comparison.ValueDomainRelationship.ShouldBe(CMR.Equivalent);
            comparison.ContentKeys[pair.SourcePackage.PackageArrayIndex].ShouldBe(source.Key);
            comparison.ContentKeys[pair.TargetPackage.PackageArrayIndex].ShouldBe(comparison.TargetElementKey);
        }

        DbElementComparison authoredNoMap = comparisons.Single(comparison => comparison.TargetElementKey is null);
        authoredNoMap.NotMapped.ShouldBeTrue();
        authoredNoMap.Relationship.ShouldBe(CMR.Equivalent);
        authoredNoMap.TypeRelationship.ShouldBeNull();
        authoredNoMap.SourceAllowsMoreValues.ShouldBeNull();

        DbStructureComparison structureComparison = DbStructureComparison.SelectSingle(fixture.Connection,
            SourceStructureKey: sourceStructure.Key, TargetStructureKey: targetStructure.Key)!;
        structureComparison.StructureMappingKey.ShouldBe(structureMapping.Key);
        foreach (DbElementMapping mapping in mappings)
        {
            DbElementComparison comparison = comparisons.Single(comparison => comparison.TargetElementKey == mapping.TargetElementKey);
            comparison.StructureComparisonKey.ShouldBe(structureComparison.Key);
            comparison.ElementMappingKey.ShouldBe(mapping.Key);
            DbElementMapping persistedMapping = DbElementMapping.SelectSingle(fixture.Connection, Key: comparison.ElementMappingKey)!;
            persistedMapping.StructureMappingKey.ShouldBe(structureMapping.Key);
            persistedMapping.SourceElementKey.ShouldBe(comparison.SourceElementKey);
            persistedMapping.TargetElementKey.ShouldBe(comparison.TargetElementKey);
        }
    }

    [Fact]
    public void InferredAndUnmatchedElementsDoNotClaimStructureMappingProvenance()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        DbStructureDefinition sourceStructure = fixture.Structure(pair.SourcePackage);
        DbStructureDefinition targetStructure = fixture.Structure(pair.TargetPackage);
        DbElement inferredSource = Element(fixture, sourceStructure, "ValueSet.compose.include.filter.value");
        DbElement inferredTarget = Element(fixture, targetStructure, inferredSource.Id);
        DbElement unmatched = fixture.AddElement(sourceStructure, "ValueSet.compose.include.filter.unmatched", "string");
        fixture.AddType(unmatched, "string", fixture.Structure(pair.SourcePackage, "string"));
        DbStructureMapping structureMapping = AddStructureMapping(fixture, pair, sourceStructure, targetStructure);
        AddStructureMapping(fixture, pair, fixture.Structure(pair.SourcePackage, "string"), fixture.Structure(pair.TargetPackage, "string"));
        DbElementMapping.SelectList(fixture.Connection).ShouldBeEmpty();

        Compare(fixture, pair);
        DbElementComparison inferred = DbElementComparison.SelectSingle(fixture.Connection,
            SourceElementKey: inferredSource.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        inferred.TargetElementKey.ShouldBe(inferredTarget.Key);
        inferred.Relationship.ShouldBe(CMR.Equivalent);
        inferred.TypeRelationship.ShouldBe(CMR.Equivalent);
        inferred.NotMapped.ShouldBeFalse();
        inferred.IsIdentical.ShouldBe(true);
        DbElementComparison noMap = DbElementComparison.SelectSingle(fixture.Connection,
            SourceElementKey: unmatched.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        noMap.TargetElementKey.ShouldBeNull();
        noMap.Relationship.ShouldBeNull();
        noMap.NotMapped.ShouldBeTrue();
        noMap.TypeRelationship.ShouldBeNull();

        DbStructureComparison structureComparison = DbStructureComparison.SelectSingle(fixture.Connection,
            SourceStructureKey: sourceStructure.Key, TargetStructureKey: targetStructure.Key)!;
        structureComparison.StructureMappingKey.ShouldBe(structureMapping.Key);
        inferred.StructureComparisonKey.ShouldBe(structureComparison.Key);
        noMap.StructureComparisonKey.ShouldBe(structureComparison.Key);
        inferred.ElementMappingKey.ShouldBeNull();
        noMap.ElementMappingKey.ShouldBeNull();
    }

    [Fact]
    public void AbsentTargetStructureDoesNotSupplyElementMappingProvenance()
    {
        using UnsafeCodeTestFixture fixture = new(initializeIndices: true);
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        DbStructureDefinition sourceStructure = fixture.AddStructure(pair.SourcePackage, "RemovedResource", FhirArtifactClassEnum.Resource);
        fixture.AddElement(sourceStructure, "RemovedResource");
        fixture.AddElement(sourceStructure, "RemovedResource.value");
        DbStructureMapping structureMapping = AddStructureMapping(fixture, pair, sourceStructure, null);

        Compare(fixture, pair);
        DbStructureComparison structureComparison = DbStructureComparison.SelectSingle(fixture.Connection,
            SourceStructureKey: sourceStructure.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        structureComparison.TargetStructureKey.ShouldBeNull();
        structureComparison.StructureMappingKey.ShouldBe(structureMapping.Key);
        List<DbElementComparison> comparisons = DbElementComparison.SelectList(fixture.Connection,
            StructureComparisonKey: structureComparison.Key);
        comparisons.Count.ShouldBe(2);
        foreach (DbElementComparison comparison in comparisons)
        {
            comparison.TargetElementKey.ShouldBeNull();
            comparison.NotMapped.ShouldBeTrue();
            comparison.Relationship.ShouldBeNull();
            comparison.SourceAllowsMoreValues.ShouldBeNull();
            comparison.ElementMappingKey.ShouldBeNull();
        }
    }

    private static DbElement Element(UnsafeCodeTestFixture fixture, DbStructureDefinition structure, string id) =>
        DbElement.SelectSingle(fixture.Connection, StructureKey: structure.Key, Id: id)!;

    private static void Compare(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair) =>
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: [pair.SequencePair]);

    private static DbStructureMapping AddStructureMapping(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair,
        DbStructureDefinition source, DbStructureDefinition? target)
    {
        TestStructureMapping mapping = new(pair, source, target);
        mapping.Key = DbStructureMapping.Insert(fixture.Connection, mapping);
        return mapping;
    }

    private sealed class TestStructureMapping : DbStructureMapping
    {
        [SetsRequiredMembers]
        internal TestStructureMapping(FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition? target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirPackageKey = pair.TargetPackageKey;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceStructureKey = source.Key;
            SourceStructureId = source.Id;
            SourceStructureUrl = source.VersionedUrl;
            TargetStructureKey = target?.Key;
            TargetStructureId = target?.Id;
            TargetStructureUrl = target?.VersionedUrl;
            ExplicitNoMap = target is null;
            Relationship = target is null ? null : CMR.Equivalent;
        }
    }

    private sealed class TestElementMapping : DbElementMapping
    {
        [SetsRequiredMembers]
        internal TestElementMapping(FhirPackageComparisonPair pair, DbStructureMapping structureMapping, DbElement source, DbElement? target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirPackageKey = pair.TargetPackageKey;
            TargetFhirSequence = pair.TargetFhirSequence;
            StructureMappingKey = structureMapping.Key;
            SourceStructureKey = source.StructureKey;
            TargetStructureKey = structureMapping.TargetStructureKey;
            SourceElementKey = source.Key;
            SourceElementId = source.Id;
            TargetElementKey = target?.Key;
            TargetElementId = target?.Id;
            ExplicitNoMap = target is null;
            Relationship = target is null ? null : CMR.Equivalent;
        }
    }

    private sealed class TestValueSetComparison : DbValueSetComparison
    {
        [SetsRequiredMembers]
        internal TestValueSetComparison(FhirPackageComparisonPair pair, DbValueSet source, DbValueSet target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirPackageKey = pair.TargetPackageKey;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceValueSetKey = source.Key;
            TargetValueSetKey = target.Key;
            SourceCanonicalVersioned = source.VersionedUrl;
            SourceCanonicalUnversioned = source.UnversionedUrl;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceVersion = source.Version;
            TargetCanonicalVersioned = target.VersionedUrl;
            TargetCanonicalUnversioned = target.UnversionedUrl;
            TargetId = target.Id;
            TargetName = target.Name;
            TargetVersion = target.Version;
            Relationship = CMR.Equivalent;
        }
    }
}
