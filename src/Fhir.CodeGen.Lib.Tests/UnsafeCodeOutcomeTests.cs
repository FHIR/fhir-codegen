// <copyright file="UnsafeCodeOutcomeTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Hl7.Fhir.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

public class UnsafeCodeOutcomeTests
{
    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 1)]
    public void DirectedAnnotationAndAuthoredNoMapProduceQualifiedPlans(int sourceIndex, int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair(sourceIndex, targetIndex);
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair, "source-system,version");
        fixture.AddAnnotation(pair, source, target);
        fixture.AddEvidence(pair, sourceVersion: "source-vs", targetVersion: "target-vs");
        fixture.Connect(pair, outcome, source, target);

        Generate(fixture, pair);
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        trigger.StructureOutcomeKey.ShouldBe(outcome.Key);
        trigger.SourceFhirPackageKey.ShouldBe(pair.SourcePackageKey);
        trigger.TargetFhirPackageKey.ShouldBe(pair.TargetPackageKey);
        trigger.SourceVersion.ShouldBe("source-system,version");
        JsonSerializer.Deserialize<string[]>(trigger.VersionsJson).ShouldBe(["source-system,version"]);
        trigger.IsWildcardVersion.ShouldBeFalse();
        trigger.System.ShouldBe(UnsafeCodeTestFixture.FilterSystem);
        UnsafeCodePathStep[] path = JsonSerializer.Deserialize<UnsafeCodePathStep[]>(trigger.SourcePathJson)!;
        path.Select(step => step.ElementId).ShouldBe(["ValueSet", source.Id]);
        trigger.SourcePathJson.ShouldNotContain("where(");
        JsonSerializer.Deserialize<int[]>(trigger.EvidenceKeysJson)!.Length.ShouldBe(1);
        JsonSerializer.Deserialize<int[]>(trigger.AnnotationKeysJson)!.Length.ShouldBe(1);
        AssertCompleted(fixture, pair);
        DbUnsafeCodePairState reverse = fixture.PairState(new(pair.TargetPackage, pair.SourcePackage));
        reverse.OutcomeInputRevision.ShouldBeNull();
    }

    [Theory]
    [InlineData("no-annotation")]
    [InlineData("no-evidence")]
    [InlineData("inferred-no-map")]
    [InlineData("reverse")]
    [InlineData("other-source-scope")]
    [InlineData("other-target-scope")]
    [InlineData("source-vs-version")]
    [InlineData("target-vs-version")]
    [InlineData("leaf-spelling")]
    public void UnrelatedOrMissingFactsNeverManufactureNoMap(string scenario)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        if (scenario != "no-annotation")
        {
            fixture.AddAnnotation(scenario == "reverse" ? new(pair.TargetPackage, pair.SourcePackage) : pair, source, target);
        }

        if (scenario != "no-evidence")
        {
            fixture.AddEvidence(pair,
                sourceScope: scenario == "other-source-scope" ? "http://example.org/other" : UnsafeCodeTestFixture.FilterValueSet,
                targetScope: scenario == "other-target-scope" ? "http://example.org/other" : UnsafeCodeTestFixture.FilterValueSet,
                sourceVersion: scenario == "source-vs-version" ? "wrong" : "source-vs",
                targetVersion: scenario == "target-vs-version" ? "wrong" : "target-vs",
                noMap: scenario != "inferred-no-map");
        }

        if (scenario == "leaf-spelling")
        {
            List<DbUnsafeCodeElementAnnotation> annotations = DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection);
            annotations[0].SourceElementId = "Other.op";
            annotations.Update(fixture.Connection);
        }

        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void RelationshipExemptionIsLocalToContributingTargetAndValueSet()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        fixture.AddAnnotation(pair, source, target);
        fixture.AddRule(excludedRelationships: "equivalent");
        fixture.AddEvidence(pair);
        fixture.AddEvidence(pair, targetScope: "http://example.org/unrelated", noMap: false, relationship: ConceptMap.ConceptMapRelationship.Equivalent);
        fixture.AddEvidence(new(pair.TargetPackage, pair.SourcePackage), noMap: false, relationship: ConceptMap.ConceptMapRelationship.Equivalent);
        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem();

        fixture.AddEvidence(pair, noMap: false, relationship: ConceptMap.ConceptMapRelationship.Equivalent);
        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void OneCodesExemptionCannotCancelAnotherCodesAnnotatedTrigger()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        fixture.AddAnnotation(pair, source, target);
        fixture.AddEvidence(pair);
        fixture.AddEvidence(pair, code: "other-code");
        fixture.AddRule(code: "other-code", excludedVersions: pair.TargetFhirSequence.ToLongVersion());
        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem().Code.ShouldBe("unsafe");
    }

    [Fact]
    public void AnnotationToUnrelatedTargetInSameStructureIsNotAContributingContext()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        DbElement unrelated = fixture.AddElement(fixture.Structure(pair.TargetPackage), "ValueSet.other", "code", UnsafeCodeTestFixture.FilterValueSet);
        fixture.AddAnnotation(pair, source, unrelated);
        fixture.AddEvidence(pair);
        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void AnnotationSurvivesAbsentNormalizedElementMappingAtExactDirectedTarget()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome _, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.AddAnnotation(pair, source, target);
        fixture.AddEvidence(pair);
        DbElementMapping.SelectList(fixture.Connection).ShouldBeEmpty();
        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem();
    }

    [Fact]
    public void AnnotatedPrimitiveWithUnresolvedBindingDoesNotSilentlyDropPotentialNoMap()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        source.BindingValueSet = null;
        source.BindingValueSetKey = null;
        DbElement.Update(fixture.Connection, [source]);
        fixture.AddAnnotation(pair, source, target);
        fixture.AddEvidence(pair);
        Should.Throw<InvalidOperationException>(() => Generate(fixture, pair)).Message.ShouldContain("ambiguous");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Theory]
    [InlineData("code", "target")]
    [InlineData("Coding", "source")]
    [InlineData("Coding", "target")]
    [InlineData("Coding", "both")]
    [InlineData("Coding", "source-version")]
    [InlineData("Coding", "target-version")]
    [InlineData("Quantity", "source")]
    [InlineData("Quantity", "target")]
    public void AnnotatedNoMapWithUnresolvedBindingScopesCannotCertifyPair(string typeName, string unresolved)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair, typeName: typeName);
        fixture.Connect(pair, outcome, source, target);
        DbElement unbound = unresolved.StartsWith("source", StringComparison.Ordinal) ? source : target;
        if (unresolved.EndsWith("-version", StringComparison.Ordinal))
        {
            unbound.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet;
            DbValueSet.Delete(fixture.Connection, FhirPackageKey: unbound.FhirPackageKey);
        }
        else
        {
            unbound.BindingValueSet = null;
        }

        unbound.BindingValueSetKey = null;
        if (unresolved == "both")
        {
            source.BindingValueSet = null;
            source.BindingValueSetKey = null;
        }

        DbElement.Update(fixture.Connection, [source, target]);
        fixture.AddAnnotation(pair, source, target);
        fixture.AddEvidence(pair, sourceVersion: "source-vs", targetVersion: "target-vs");

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => Generate(fixture, pair));
        error.Message.ShouldContain("ambiguous");
        error.Message.ShouldContain(source.Id);
        error.Message.ShouldContain(target.Id);
        error.Message.ShouldContain($"{pair.SourcePackageKey}->{pair.TargetPackageKey}");
        Triggers(fixture).ShouldBeEmpty();
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        fixture.PairState(pair).OutcomeComparisonRevision.ShouldBeNull();
        fixture.PairState(pair).OutcomeFormatVersion.ShouldBeNull();
        fixture.PairState(pair).ComparisonRevision.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ProvenScopeNonmatchRemainsSafeWithOtherBindingUnresolved(bool missingSource, bool versionMismatch)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair, typeName: "Coding");
        fixture.Connect(pair, outcome, source, target);
        DbElement unbound = missingSource ? source : target;
        unbound.BindingValueSet = null;
        unbound.BindingValueSetKey = null;
        DbElement.Update(fixture.Connection, [unbound]);
        fixture.AddAnnotation(pair, source, target);
        fixture.AddEvidence(pair,
            sourceScope: !missingSource && !versionMismatch ? "http://example.org/other" : UnsafeCodeTestFixture.FilterValueSet,
            targetScope: missingSource && !versionMismatch ? "http://example.org/other" : UnsafeCodeTestFixture.FilterValueSet,
            sourceVersion: !missingSource && versionMismatch ? "wrong" : "source-vs",
            targetVersion: missingSource && versionMismatch ? "wrong" : "target-vs");

        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoMapOnlyTypedRuleCannotUseUnresolvedBindingScopesAsAnAbsentGate(bool missingSource)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair, typeName: "Coding");
        fixture.Connect(pair, outcome, source, target);
        DbElement unbound = missingSource ? source : target;
        unbound.BindingValueSet = null;
        unbound.BindingValueSetKey = null;
        DbElement.Update(fixture.Connection, [unbound]);
        fixture.AddRule(noMapOnly: true);
        fixture.AddEvidence(pair);

        Should.Throw<InvalidOperationException>(() => Generate(fixture, pair)).Message.ShouldContain("ambiguous");
        Triggers(fixture).ShouldBeEmpty();
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Fact]
    public void UnboundTypedConfigurationDoesNotRequireUnusedNoMapEvidenceScopes()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(resource, "ValueSet.coding", "Coding");
        fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        fixture.AddEvidence(pair);

        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void IneligibleNoMapOnlyRuleNeitherTriggersNorRequiresPrimitiveQualification()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(resource, "ValueSet.op", "code");
        fixture.AddOutcome(pair, resource);
        fixture.AddRule(noMapOnly: true, excludedVersions: pair.TargetFhirSequence.ToLongVersion());
        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void QualifiedBindingCanResolveAuthoredCodeMapWithoutGroupSystem()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome _, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.AddAnnotation(pair, source, target);
        DbUnsafeCodeMapEvidence evidence = fixture.AddEvidence(pair);
        evidence.SourceSystem = null;
        DbUnsafeCodeMapEvidence.Update(fixture.Connection, [evidence]);
        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem().System.ShouldBe(UnsafeCodeTestFixture.FilterSystem);
    }

    [Fact]
    public void ParentCodeableConceptAnnotationSurvivesDatatypesOwnContributingComparison()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        DbStructureDefinition targetResource = fixture.Structure(pair.TargetPackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(targetResource, "ValueSet");
        fixture.SetBinding(pair.SourcePackage);
        fixture.SetBinding(pair.TargetPackage, valueSetVersion: "target-vs");
        DbElement use = fixture.AddElement(resource, "ValueSet.concept", "CodeableConcept", UnsafeCodeTestFixture.FilterValueSet);
        DbElement targetUse = fixture.AddElement(targetResource, "ValueSet.concept", "CodeableConcept", UnsafeCodeTestFixture.FilterValueSet);
        AddTypedGraph(fixture, pair.SourcePackage, use, "CodeableConcept");
        DbStructureDefinition concept = fixture.Structure(pair.SourcePackage, "CodeableConcept");
        DbElement coding = DbElement.SelectSingle(fixture.Connection, StructureKey: concept.Key, Id: "CodeableConcept.coding")!;
        DbStructureDefinition targetConcept = fixture.AddStructure(pair.TargetPackage, "CodeableConcept");
        fixture.AddElement(targetConcept, "CodeableConcept");
        DbElement targetCoding = fixture.AddElement(targetConcept, "CodeableConcept.coding", "Coding");
        DbStructureOutcome owner = fixture.AddOutcome(pair, resource, targetResource);
        fixture.Connect(pair, owner, use, targetUse);
        fixture.Connect(pair, fixture.AddOutcome(pair, concept, targetConcept), coding, targetCoding);
        fixture.AddAnnotation(pair, use, targetUse);
        fixture.AddEvidence(pair);
        Generate(fixture, pair);
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        trigger.SourceElementId.ShouldBe(use.Id);
        trigger.SourcePathJson.ShouldContain("CodeableConcept.coding");
        trigger.StructureOutcomeKey.ShouldBe(owner.Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BindingKeyComposeImportsAndScopedConceptsQualifyPrimitiveIdentity(bool expanded)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbValueSet bound = fixture.SetBinding(pair.SourcePackage, systemVersion: "v,1");
        if (expanded)
        {
            bound.CanExpand = true;
            DbValueSetConcept.Insert(fixture.Connection, new DbValueSetConcept
            {
                FhirPackageKey = pair.SourcePackageKey,
                ValueSetKey = bound.Key,
                System = UnsafeCodeTestFixture.FilterSystem,
                SystemVersion = "v,1",
                Code = "unsafe",
                Display = null,
                Inactive = false,
                Abstract = false,
                Properties = null,
            });
        }
        else
        {
            DbValueSet imported = fixture.SetBinding(pair.TargetPackage, systemVersion: "unrelated");
            imported.Key = 0;
            imported.FhirPackageKey = pair.SourcePackageKey;
            imported.Id = "imported";
            imported.UnversionedUrl = "http://example.org/imported";
            imported.Version = "vs-version";
            imported.VersionedUrl = imported.UnversionedUrl + "|" + imported.Version;
            imported.Compose!.Include[0].Version = "v,1";
            imported.Key = DbValueSet.Insert(fixture.Connection, imported);
            bound.Compose = new() { Include = [new() { ValueSet = [imported.VersionedUrl] }] };
        }

        DbValueSet.Update(fixture.Connection, [bound]);
        DbElement scalar = fixture.AddElement(resource, "ValueSet.op", "code");
        scalar.BindingValueSetKey = bound.Key;
        DbElement.Update(fixture.Connection, [scalar]);
        fixture.AddOutcome(pair, resource);
        fixture.AddRule(version: "v,1");
        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem().SourceVersion.ShouldBe("v,1");
    }

    [Fact]
    public void UnrelatedTargetSystemRelationshipCannotExemptCurrentContext()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        fixture.AddRule(excludedRelationships: "equivalent");
        DbUnsafeCodeMapEvidence evidence = fixture.AddEvidence(pair, noMap: false, relationship: ConceptMap.ConceptMapRelationship.Equivalent);
        evidence.TargetSystem = "http://example.org/other-system";
        DbUnsafeCodeMapEvidence.Update(fixture.Connection, [evidence]);
        Generate(fixture, pair);
        Triggers(fixture).ShouldHaveSingleItem();
    }

    [Fact]
    public void PrimitiveBindingCannotBorrowVersionFromAnotherCoding()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        BoundElements(fixture, pair);
        fixture.AddRule(version: "v1");
        DbValueSet codingBinding = fixture.SetBinding(pair.TargetPackage, systemVersion: "v1");
        codingBinding.FhirPackageKey = pair.SourcePackageKey;
        codingBinding.Id = "coding-binding";
        codingBinding.UnversionedUrl = "http://example.org/coding-binding";
        codingBinding.VersionedUrl = codingBinding.UnversionedUrl + "|" + codingBinding.Version;
        codingBinding.Key = DbValueSet.Insert(fixture.Connection, codingBinding);
        fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.otherCoding", "Coding", codingBinding.VersionedUrl);
        Should.Throw<InvalidOperationException>(() => Generate(fixture, pair)).Message.ShouldContain("ambiguous");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Fact]
    public void MissingCodeableConceptTypeGraphCannotBeCertifiedAsEmpty()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(resource, "ValueSet.concept", "CodeableConcept");
        fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        Should.Throw<InvalidOperationException>(() => Generate(fixture, pair)).Message.ShouldContain("type graph");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Theory]
    [InlineData("Coding")]
    [InlineData("CodeableConcept")]
    [InlineData("CodeableReference")]
    [InlineData("Quantity")]
    [InlineData("Age")]
    public void UnboundTypedConfigurationUsesActualCodeBearingFields(string typeName)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbElement use = fixture.AddElement(resource, "ValueSet.value", typeName);
        AddTypedGraph(fixture, pair.SourcePackage, use, typeName);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        Generate(fixture, pair);
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        trigger.StructureOutcomeKey.ShouldBe(outcome.Key);
        trigger.IsWildcardVersion.ShouldBeTrue();
        UnsafeCodePathStep[] path = JsonSerializer.Deserialize<UnsafeCodePathStep[]>(trigger.SourcePathJson)!;
        path[0].ElementId.ShouldBe("ValueSet");
        path[1].ElementId.ShouldBe("ValueSet.value");
        if (typeName == "CodeableReference")
        {
            path.ShouldContain(step => step.ElementId == "CodeableReference.concept");
            path.ShouldContain(step => step.ElementId == "CodeableConcept.coding");
            path.ShouldNotContain(step => step.ElementId == "CodeableReference.reference");
        }

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuantityComparatorHasIndependentPrimitiveIdentityAlongsideCarrier(bool inline)
    {
        const string comparatorSystem = "http://hl7.org/fhir/quantity-comparator";
        const string comparatorValueSet = "http://hl7.org/fhir/ValueSet/quantity-comparator";
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbElement quantity = fixture.AddElement(resource, "ValueSet.quantity", "Quantity");
        DbStructureDefinition fields = resource;
        string prefix = quantity.Id;
        if (!inline)
        {
            AddTypedGraph(fixture, pair.SourcePackage, quantity, "Quantity");
            fields = fixture.Structure(pair.SourcePackage, "Quantity");
            prefix = "Quantity";
        }
        else
        {
            fixture.AddElement(resource, quantity.Id + ".system", "uri");
            fixture.AddElement(resource, quantity.Id + ".code", "code");
        }

        DbValueSet binding = fixture.SetBinding(pair.SourcePackage, comparatorSystem, "comparator-v1");
        binding.Id = "quantity-comparator";
        binding.UnversionedUrl = comparatorValueSet;
        binding.VersionedUrl = comparatorValueSet + "|" + binding.Version;
        binding.Compose!.Include[0].Concept = [new() { Code = "<" }];
        DbValueSet.Update(fixture.Connection, [binding]);
        DbElement comparator = fixture.AddElement(fields, prefix + ".comparator", "code", binding.VersionedUrl);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, resource);
        fixture.AddRule(system: comparatorSystem, code: "<");

        Generate(fixture, pair);
        List<DbUnsafeCodeTrigger> triggers = Triggers(fixture);
        triggers.Count.ShouldBe(2);
        triggers.ShouldAllBe(trigger => trigger.StructureOutcomeKey == outcome.Key && trigger.System == comparatorSystem && trigger.Code == "<");
        DbUnsafeCodeTrigger carrier = triggers.Single(trigger => trigger.SourceElementId == quantity.Id);
        carrier.IsWildcardVersion.ShouldBeTrue();
        JsonSerializer.Deserialize<UnsafeCodePathStep[]>(carrier.SourcePathJson)!.Select(step => step.ElementId)
            .ShouldBe(["ValueSet", quantity.Id]);
        DbUnsafeCodeTrigger scalar = triggers.Single(trigger => trigger.SourceElementId == comparator.Id);
        scalar.TargetElementId.ShouldBeEmpty();
        scalar.SourceVersion.ShouldBe("comparator-v1");
        scalar.IsWildcardVersion.ShouldBeFalse();
        JsonSerializer.Deserialize<UnsafeCodePathStep[]>(scalar.SourcePathJson)!.Select(step => step.ElementId)
            .ShouldBe(["ValueSet", quantity.Id, comparator.Id]);
        triggers.ShouldNotContain(trigger => trigger.SourcePathJson.Contains(prefix + ".code\"", StringComparison.Ordinal));
        AssertCompleted(fixture, pair);
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("multiple-systems")]
    [InlineData("preferred")]
    [InlineData("unknown-exact-version")]
    public void ApplicableAmbiguousPrimitiveFailsAndLeavesPairIncomplete(string scenario)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.SetBinding(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet.op", "code", scenario == "missing-binding" ? null : UnsafeCodeTestFixture.FilterValueSet,
            scenario == "preferred" ? BindingStrength.Preferred : BindingStrength.Required);
        fixture.AddOutcome(pair, resource);
        if (scenario == "multiple-systems")
        {
            DbValueSet binding = DbValueSet.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey)!;
            binding.Compose!.Include.Add(new() { System = "http://example.org/other" });
            DbValueSet.Update(fixture.Connection, [binding]);
        }

        fixture.AddRule(version: scenario == "unknown-exact-version" ? "v1" : null);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => Generate(fixture, pair));
        error.Message.ShouldContain("ValueSet.op");
        error.Message.ShouldContain("ambiguous");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        fixture.PairState(pair).ComparisonRevision.ShouldNotBeNull();
    }

    [Fact]
    public void AuthoritativeDifferentSystemOrVersionIsNonmatch()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        BoundElements(fixture, pair, "v2");
        fixture.AddRule(version: "v1");
        fixture.AddRule(system: "http://example.org/other");
        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void WildcardAndExactExemptionRemainDisjointForRuntimeCodingVersions()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(resource, "ValueSet.coding", "Coding");
        fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        fixture.AddRule(version: "v,1", excludedVersions: pair.TargetFhirSequence.ToLongVersion());
        Generate(fixture, pair);
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        trigger.IsWildcardVersion.ShouldBeTrue();
        JsonSerializer.Deserialize<string[]>(trigger.VersionsJson).ShouldBe(["v,1"]);
    }

    [Fact]
    public void ReusedNestedProfileAndInheritedFieldsStayAtResourceUseSites()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbStructureDefinition basis = fixture.AddStructure(pair.SourcePackage, "BaseType");
        DbElement baseRoot = fixture.AddElement(basis, "BaseType");
        fixture.AddElement(basis, "BaseType.coding", "Coding");
        DbStructureDefinition profile = fixture.AddStructure(pair.SourcePackage, "ProfileType", FhirArtifactClassEnum.Profile);
        DbElement profileRoot = fixture.AddElement(profile, "ProfileType");
        profileRoot.BaseElementKey = baseRoot.Key;
        DbElement.Update(fixture.Connection, [profileRoot]);
        DbStructureDefinition wrapper = fixture.AddStructure(pair.SourcePackage, "Wrapper");
        fixture.AddElement(wrapper, "Wrapper");
        DbElement nested = fixture.AddElement(wrapper, "Wrapper.nested", "BaseType");
        fixture.AddType(nested, "BaseType", basis, profile);
        foreach (string name in new[] { "first", "second" })
        {
            DbElement use = fixture.AddElement(resource, "ValueSet." + name, "Wrapper");
            fixture.AddType(use, "Wrapper", wrapper);
        }

        DbStructureOutcome owner = fixture.AddOutcome(pair, resource);
        fixture.AddOutcome(pair, basis);
        fixture.AddOutcome(pair, profile);
        fixture.AddRule();
        Generate(fixture, pair);
        List<DbUnsafeCodeTrigger> triggers = Triggers(fixture);
        triggers.Count.ShouldBe(2);
        triggers.ShouldAllBe(trigger => trigger.StructureOutcomeKey == owner.Key);
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("ValueSet.first", StringComparison.Ordinal));
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("ValueSet.second", StringComparison.Ordinal));
        triggers.ShouldAllBe(trigger => trigger.SourcePathJson.Contains("BaseType.coding", StringComparison.Ordinal));
        triggers.ShouldAllBe(trigger => trigger.SourcePathJson.Contains(profile.VersionedUrl, StringComparison.Ordinal));
    }

    [Fact]
    public void ImportedScopeCannotLendAnotherIncludesCodeMapIdentity()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        (DbStructureOutcome outcome, DbElement source, DbElement target) = BoundElements(fixture, pair);
        fixture.Connect(pair, outcome, source, target);
        fixture.AddAnnotation(pair, source, target);
        DbValueSet imported = fixture.SetBinding(pair.TargetPackage, "http://example.org/other-system");
        imported.FhirPackageKey = pair.SourcePackageKey;
        imported.Id = "imported";
        imported.UnversionedUrl = "http://example.org/imported";
        imported.Version = "imported-vs";
        imported.VersionedUrl = imported.UnversionedUrl + "|" + imported.Version;
        imported.Compose!.Include[0].Concept = [new() { Code = "different-code" }];
        imported.Key = DbValueSet.Insert(fixture.Connection, imported);
        DbValueSet binding = DbValueSet.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "filter-operator")!;
        binding.Compose!.Include.Add(new() { ValueSet = [imported.VersionedUrl] });
        DbValueSet.Update(fixture.Connection, [binding]);
        fixture.AddEvidence(pair, sourceScope: imported.UnversionedUrl, sourceVersion: imported.Version);
        Generate(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void InheritedResourceProfileUsesBaseStructureWithoutCreatingDatatypeOwners()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(resource, "ValueSet.coding", "Coding");
        DbStructureDefinition profile = fixture.AddStructure(pair.SourcePackage, "ValueSetProfile", FhirArtifactClassEnum.Profile);
        DbElement root = fixture.AddElement(profile, profile.Id);
        root.BaseStructureKey = resource.Key;
        DbElement.Update(fixture.Connection, [root]);
        DbStructureOutcome owner = fixture.AddOutcome(pair, profile);
        fixture.AddRule();
        Generate(fixture, pair);
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        trigger.StructureOutcomeKey.ShouldBe(owner.Key);
        trigger.SourcePathJson.ShouldContain("ValueSetProfile");
        trigger.SourcePathJson.ShouldContain("ValueSet.coding");
    }

    [Fact]
    public void ExactTypeStructureFallbackPreservesSlicesAndSkipsProhibitedUseSites()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        fixture.AddElement(resource, "ValueSet.concept", "CodeableConcept");
        DbElement prohibited = fixture.AddElement(resource, "ValueSet.prohibited", "code");
        prohibited.MaxCardinalityString = "0";
        DbElement.Update(fixture.Connection, [prohibited]);
        DbStructureDefinition concept = fixture.AddStructure(pair.SourcePackage, "CodeableConcept");
        fixture.AddElement(concept, "CodeableConcept");
        DbElement first = fixture.AddElement(concept, "CodeableConcept.coding:first", "Coding");
        DbElement second = fixture.AddElement(concept, "CodeableConcept.coding:second", "Coding");
        first.Path = "CodeableConcept.coding";
        second.Path = first.Path;
        DbElement.Update(fixture.Connection, [first, second]);
        fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        Generate(fixture, pair);
        List<DbUnsafeCodeTrigger> triggers = Triggers(fixture);
        triggers.Count.ShouldBe(2);
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("coding:first", StringComparison.Ordinal));
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("coding:second", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentReferencesAndCyclesPreserveInitialAndDescendantOccurrencesWithoutDepthCap()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbElement block = fixture.AddElement(resource, "ValueSet.block");
        fixture.AddElement(resource, "ValueSet.block.coding", "Coding");
        DbElement repeat = fixture.AddElement(resource, "ValueSet.block.repeat");
        repeat.ContentReferenceSourceKey = block.Key;
        repeat.DefinedAsContentReference = true;
        DbElement reuse = fixture.AddElement(resource, "ValueSet.reuse");
        reuse.ContentReferenceSourceKey = block.Key;
        reuse.DefinedAsContentReference = true;
        DbElement.Update(fixture.Connection, [repeat, reuse]);
        fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        Generate(fixture, pair);
        List<DbUnsafeCodeTrigger> triggers = Triggers(fixture);
        triggers.Count.ShouldBeGreaterThanOrEqualTo(4);
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("ValueSet.reuse", StringComparison.Ordinal));
        triggers.ShouldContain(trigger => JsonSerializer.Deserialize<UnsafeCodePathStep[]>(trigger.SourcePathJson)!.Any(step => step.IsContentReference));
        triggers.Count.ShouldBeLessThan(20);

        // A long acyclic chain must not be mistaken for recursion or silently truncated.
        DbStructureDefinition current = fixture.AddStructure(pair.SourcePackage, "Deep0");
        fixture.AddElement(current, "Deep0");
        DbElement first = fixture.AddElement(resource, "ValueSet.deep", "Deep0");
        fixture.AddType(first, "Deep0", current);
        for (int i = 1; i <= 70; i++)
        {
            DbStructureDefinition next = fixture.AddStructure(pair.SourcePackage, "Deep" + i);
            fixture.AddElement(next, next.Id);
            DbElement child = fixture.AddElement(current, current.Id + ".next", next.Id);
            fixture.AddType(child, next.Id, next);
            current = next;
        }

        fixture.AddElement(current, current.Id + ".coding", "Coding");
        Generate(fixture, pair);
        Triggers(fixture).ShouldContain(trigger => JsonSerializer.Deserialize<UnsafeCodePathStep[]>(trigger.SourcePathJson)!.Length > 70);
    }

    [Fact]
    public void ResourceTypedFieldsDoNotMoveAnotherResourcesCodesIntoOwner()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbStructureDefinition other = fixture.AddStructure(pair.SourcePackage, "Other", FhirArtifactClassEnum.Resource);
        fixture.AddElement(other, "Other");
        fixture.AddElement(other, "Other.coding", "Coding");
        DbElement reference = fixture.AddElement(resource, "ValueSet.other", "Other");
        fixture.AddType(reference, "Other", other);
        DbStructureOutcome owner = fixture.AddOutcome(pair, resource);
        DbStructureOutcome otherOwner = fixture.AddOutcome(pair, other);
        fixture.AddRule();
        Generate(fixture, pair);
        List<DbUnsafeCodeTrigger> triggers = Triggers(fixture);
        triggers.ShouldNotContain(trigger => trigger.StructureOutcomeKey == owner.Key);
        triggers.ShouldHaveSingleItem().StructureOutcomeKey.ShouldBe(otherOwner.Key);
    }

    [Fact]
    public void TypeCyclesIncludeInitialAndNestedOccurrencesAndTerminateBySemanticEdges()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition resource = fixture.Structure(pair.SourcePackage);
        fixture.AddElement(resource, "ValueSet");
        DbStructureDefinition node = fixture.AddStructure(pair.SourcePackage, "Node");
        fixture.AddElement(node, "Node");
        fixture.AddElement(node, "Node.coding", "Coding");
        DbElement recursive = fixture.AddElement(node, "Node.next", "Node");
        fixture.AddType(recursive, "Node", node);
        DbElement initial = fixture.AddElement(resource, "ValueSet.initial", "Node");
        fixture.AddType(initial, "Node", node);
        fixture.AddElement(resource, "ValueSet.coding", "Coding");
        fixture.AddOutcome(pair, resource);
        fixture.AddRule();
        Generate(fixture, pair);
        List<DbUnsafeCodeTrigger> triggers = Triggers(fixture);
        triggers.Count.ShouldBe(4);
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("Node.next", StringComparison.Ordinal));
        triggers.ShouldContain(trigger => trigger.SourceElementId == "ValueSet.coding");
        AssertCompleted(fixture, pair);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("incomplete")]
    [InlineData("incompatible")]
    [InlineData("stale-comparison")]
    public void StructureOutcomesRejectMissingIncompleteAndStaleInputs(string scenario)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        if (scenario == "missing")
        {
            DbUnsafeCodeInputState.Delete(fixture.Connection);
        }
        else if (scenario == "incomplete")
        {
            UnsafeCodeInputStore.InvalidateInputs(fixture.Connection);
        }
        else if (scenario == "incompatible")
        {
            DbUnsafeCodeInputState input = fixture.InputState;
            input.FormatVersion++;
            DbUnsafeCodeInputState.Update(fixture.Connection, [input]);
        }
        else
        {
            UnsafeCodeInputStore.ReplaceWithEmptyInputs(fixture.Connection, fixture.Packages);
        }

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance)
            .GenerateOutcomes(processValueSets: false, specificPairs: Pairs(pair)));
        error.Message.ShouldContain(scenario == "stale-comparison" ? "comparisons" : "maps");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Fact]
    public void CompletedEmptyIsDistinctFromNeverEvaluatedAndVocabularyIsIndependent()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        fixture.PairState(pair).OutcomeFormatVersion.ShouldBeNull();
        new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance).GenerateOutcomes(processValueSets: false, specificPairs: Pairs(pair));
        AssertCompleted(fixture, pair);
        Triggers(fixture).ShouldBeEmpty();
        string comparison = fixture.PairState(pair).ComparisonRevision!;
        DbOutcomeClasses.DropTables(fixture.Connection, forStructures: false);
        DbOutcomeClasses.CreateTables(fixture.Connection, forStructures: false);
        AssertCompleted(fixture, pair);
        fixture.PairState(pair).ComparisonRevision.ShouldBe(comparison);
        UnsafeCodeInputStore.InvalidateInputs(fixture.Connection);
        Should.NotThrow(() => new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance)
            .GenerateOutcomes(processStructures: false, maxStepSize: 0));
    }

    [Fact]
    public void VocabularyOnlyGenerationPreservesActualStructureTriggersAndStamps()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        BoundElements(fixture, pair);
        fixture.AddRule();
        Generate(fixture, pair);
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        DbUnsafeCodePairState before = fixture.PairState(pair);
        new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance).GenerateOutcomes(processStructures: false, maxStepSize: 0);
        Triggers(fixture).ShouldHaveSingleItem().Key.ShouldBe(trigger.Key);
        fixture.PairState(pair).OutcomeComparisonRevision.ShouldBe(before.OutcomeComparisonRevision);
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void EvenZeroTriggerCompletionRechecksDependencies()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbUnsafeCodePairState dependency = UnsafeCodeInputStore.BeginOutcomes(fixture.Connection, pair.SourcePackageKey, pair.TargetPackageKey);
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(fixture.Connection, fixture.Packages);
        fixture.StampComparison(pair);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.CompleteOutcomes(fixture.Connection, dependency));
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Fact]
    public void FamilyResetsInvalidateAllPairsBeforeDeletionAndOutcomeResetPreservesComparisons()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        FhirPackageComparisonPair other = new(pair.TargetPackage, pair.SourcePackage);
        fixture.StampComparison(other);
        Generate(fixture, pair);
        Generate(fixture, other);
        string comparison = fixture.PairState(pair).ComparisonRevision!;
        DbOutcomeClasses.DropTables(fixture.Connection, forValueSets: false);
        fixture.PairState(pair).ComparisonRevision.ShouldBe(comparison);
        fixture.PairState(other).OutcomeInputRevision.ShouldBeNull();
        DbOutcomeClasses.CreateTables(fixture.Connection, forValueSets: false);
        Generate(fixture, pair);
        Generate(fixture, other);
        DbComparisonClasses.DropTables(fixture.Connection, forStructures: false);
        AssertCompleted(fixture, pair);
        DbComparisonClasses.DropTables(fixture.Connection, forValueSets: false);
        DbUnsafeCodePairState.SelectList(fixture.Connection).ShouldAllBe(state =>
            state.ComparisonRevision == null && state.OutcomeInputRevision == null);
    }

    [Fact]
    public void FilteredOutcomeResetDoesNotLeaveOtherPairCertified()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        FhirPackageComparisonPair other = new(pair.TargetPackage, pair.SourcePackage);
        fixture.StampComparison(other);
        Generate(fixture, other);
        new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance).GenerateOutcomes(processValueSets: false, specificPairs: Pairs(pair));
        AssertCompleted(fixture, pair);
        fixture.PairState(other).OutcomeInputRevision.ShouldBeNull();
        fixture.PairState(other).ComparisonRevision.ShouldNotBeNull();
    }

    [Fact]
    public void ActualComparerStampsOnlySuccessfulFlushedFilteredPairsAndDoesNotInventInputs()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        FhirPackageComparisonPair other = new(pair.TargetPackage, pair.SourcePackage);
        fixture.StampComparison(other);
        DbComparisonClasses.DropTables(fixture.Connection);
        DbComparisonClasses.CreateTables(fixture.Connection);
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: Pairs(pair));
        DbStructureComparison.SelectList(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey).ShouldNotBeEmpty();
        fixture.PairState(pair).ComparisonRevision.ShouldNotBeNullOrEmpty();
        fixture.PairState(other).ComparisonRevision.ShouldBeNull();
        DbUnsafeCodeInputState.Delete(fixture.Connection);
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: Pairs(pair));
        DbUnsafeCodeInputState.SelectList(fixture.Connection).ShouldBeEmpty();
        fixture.PairState(pair).ComparisonRevision.ShouldBeNull();
    }

    [Fact]
    public void FailedComparisonFlushLeavesNoCertification()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        Generate(fixture, pair);
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_comparison BEFORE INSERT ON StructureComparisons BEGIN SELECT RAISE(FAIL, 'comparison flush'); END";
        command.ExecuteNonQuery();
        Should.Throw<SqliteException>(() => new StructureComparer(fixture.Connection, NullLoggerFactory.Instance)
            .CompareStructures(specificPairs: Pairs(pair))).Message.ShouldContain("comparison flush");
        fixture.PairState(pair).ComparisonRevision.ShouldBeNull();
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    [Fact]
    public void InputChangedDuringSuccessfulComparisonFlushPreventsCertification()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER invalidate_input AFTER INSERT ON StructureComparisons BEGIN UPDATE UnsafeCodeInputState SET CompletedInputRevision = NULL; END";
        command.ExecuteNonQuery();
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: Pairs(pair));
        DbStructureComparison.SelectList(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey).ShouldNotBeEmpty();
        fixture.PairState(pair).ComparisonRevision.ShouldBeNull();
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        fixture.InputState.CompletedInputRevision.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetInvalidationPrecedesFirstDestructiveOperationEvenWhenDropFails(bool comparison)
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        Generate(fixture, pair);
        using SqliteCommand command = fixture.Connection.CreateCommand();
        string table = comparison ? "StructureComparisons" : "UnsafeCodeTriggers";
        command.CommandText = $"DROP TABLE {table}; CREATE VIEW {table} AS SELECT 1 AS Dummy";
        command.ExecuteNonQuery();
        Should.Throw<SqliteException>(() =>
        {
            if (comparison)
            {
                DbComparisonClasses.DropTables(fixture.Connection, forValueSets: false);
            }
            else
            {
                DbOutcomeClasses.DropTables(fixture.Connection, forValueSets: false);
            }
        });
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        if (comparison)
        {
            fixture.PairState(pair).ComparisonRevision.ShouldBeNull();
        }
        else
        {
            fixture.PairState(pair).ComparisonRevision.ShouldNotBeNull();
        }
    }

    [Fact]
    public void StructurePipelineInvokesAdapterAfterPersistingFinalOutcomes()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition structure = fixture.Structure(package);
            fixture.AddElement(structure, "ValueSet");
            fixture.AddElement(structure, "ValueSet.coding", "Coding");
        }

        fixture.AddRule();
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: Pairs(pair));
        new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance).GenerateOutcomes(processValueSets: false, specificPairs: Pairs(pair));
        DbUnsafeCodeTrigger trigger = Triggers(fixture).ShouldHaveSingleItem();
        DbStructureOutcome.SelectSingle(fixture.Connection, Key: trigger.StructureOutcomeKey).ShouldNotBeNull();
        AssertCompleted(fixture, pair);
    }

    [Fact]
    public void FailedTriggerFlushOrChangedDependencyCannotCertifyPair()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        BoundElements(fixture, pair);
        fixture.AddRule();
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_safety BEFORE INSERT ON UnsafeCodeTriggers BEGIN SELECT RAISE(FAIL, 'injected'); END";
        command.ExecuteNonQuery();
        Should.Throw<SqliteException>(() => Generate(fixture, pair)).Message.ShouldContain("injected");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        command.CommandText = "DROP TRIGGER fail_safety";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TRIGGER change_dependency AFTER INSERT ON UnsafeCodeTriggers BEGIN UPDATE UnsafeCodePairState SET ComparisonRevision = 'changed'; END";
        command.ExecuteNonQuery();
        Should.Throw<InvalidOperationException>(() => Generate(fixture, pair)).Message.ShouldContain("changed");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
    }

    private static HashSet<(FhirReleases.FhirSequenceCodes, FhirReleases.FhirSequenceCodes)> Pairs(FhirPackageComparisonPair pair) =>
        [(pair.SourceFhirSequence, pair.TargetFhirSequence)];

    private static void Generate(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair) =>
        new UnsafeCodeOutcomeGenerator(fixture.Connection).GenerateForPair(pair);

    private static List<DbUnsafeCodeTrigger> Triggers(UnsafeCodeTestFixture fixture) => DbUnsafeCodeTrigger.SelectList(fixture.Connection);

    private static void AssertCompleted(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair)
    {
        DbUnsafeCodePairState state = fixture.PairState(pair);
        state.OutcomeInputRevision.ShouldBe(fixture.InputState.CompletedInputRevision);
        state.OutcomeComparisonRevision.ShouldBe(state.ComparisonRevision);
        state.OutcomeFormatVersion.ShouldBe(UnsafeCodeConstants.OutcomeFormatVersion);
    }

    private static (DbStructureOutcome, DbElement, DbElement) BoundElements(
        UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair, string? systemVersion = null, string typeName = "code")
    {
        DbStructureDefinition source = fixture.Structure(pair.SourcePackage);
        DbStructureDefinition target = fixture.Structure(pair.TargetPackage);
        fixture.AddElement(source, "ValueSet");
        fixture.AddElement(target, "ValueSet");
        fixture.SetBinding(pair.SourcePackage, systemVersion: systemVersion);
        fixture.SetBinding(pair.TargetPackage, valueSetVersion: "target-vs");
        DbElement sourceElement = fixture.AddElement(source, "ValueSet.op", typeName, UnsafeCodeTestFixture.FilterValueSet + "|source-vs");
        DbElement targetElement = fixture.AddElement(target, "ValueSet.op", typeName, UnsafeCodeTestFixture.FilterValueSet + "|target-vs");
        return (fixture.AddOutcome(pair, source, target), sourceElement, targetElement);
    }

    private static void AddTypedGraph(UnsafeCodeTestFixture fixture, DbFhirPackage package, DbElement use, string typeName)
    {
        DbStructureDefinition type = fixture.AddStructure(package, typeName);
        fixture.AddElement(type, typeName);
        fixture.AddType(use, typeName, type);
        switch (typeName)
        {
            case "CodeableReference":
                DbElement concept = fixture.AddElement(type, "CodeableReference.concept", "CodeableConcept");
                fixture.AddElement(type, "CodeableReference.reference", "Reference");
                AddTypedGraph(fixture, package, concept, "CodeableConcept");
                break;
            case "CodeableConcept":
                DbElement coding = fixture.AddElement(type, "CodeableConcept.coding", "Coding");
                AddTypedGraph(fixture, package, coding, "Coding");
                break;
            default:
                fixture.AddElement(type, typeName + ".system", "uri");
                fixture.AddElement(type, typeName + ".code", "code");
                if (typeName == "Coding")
                {
                    fixture.AddElement(type, "Coding.version", "string");
                }

                break;
        }
    }
}
