// <copyright file="UnsafeCodePipelineTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

extern alias r4;

using System.Text.Json;
using System.Text.Json.Nodes;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class UnsafeCodePipelineTests
{
    [Theory]
    [InlineData("missing-type")]
    [InlineData("opaque-extension")]
    public void ZeroCandidateRunsCannotCertifyUnclassifiedRequiredFacts(string corruption)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureDefinition quantity = fixture.Structure(pair.SourcePackage, "Quantity");
        DbStructureDefinitionFacts row = DbStructureDefinitionFacts.SelectSingle(fixture.Connection, StructureKey: quantity.Key)!;
        string authoredJson = row.StructureDefinitionJson;
        StructureDefinition definition = new FhirJsonParser().Parse<StructureDefinition>(row.StructureDefinitionJson);
        if (corruption == "missing-type")
        {
            definition.Snapshot.Element.Single(element => element.Path == "Quantity.code").Type.Clear();
        }
        else
        {
            definition.Snapshot.Element[0].Extension.Add(new("urn:example:unclassified-semantic-extension", new FhirString("required")));
        }
        row.StructureDefinitionJson = definition.ToJson();
        DbStructureDefinitionFacts.Update(fixture.Connection, row);
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => fixture.GenerateSafety(pair)).Message.ShouldContain("Quantity");
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldBeEmpty();
        DbElementOutcome.SelectList(fixture.Connection).ShouldBeEmpty();

        row.StructureDefinitionJson = authoredJson;
        DbStructureDefinitionFacts.Update(fixture.Connection, row);
        string? revision = UnsafeCodeInputStore.BeginComparison(fixture.Connection, pair.SourcePackageKey, pair.TargetPackageKey);
        row.StructureDefinitionJson = definition.ToJson();
        DbStructureDefinitionFacts.Update(fixture.Connection, row);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.CompleteComparison(fixture.Connection,
            pair.SourcePackageKey, pair.TargetPackageKey, revision));
        fixture.PairState(pair).ComparisonRevision.ShouldBeNull();
    }

    [Fact]
    public void NegativeProfileAlternativesPersistEveryFailedNativeContext()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs(persistDatabase: true);
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbElement? source = null;
        List<string> nativeAlternatives = [];
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition resource = fixture.Structure(package);
            DbStructureDefinition coding = fixture.Structure(package, "Coding");
            DbElement use = fixture.AddElement(resource, "ValueSet.selected", "Coding");
            List<string> alternatives = [];
            foreach (string code in package.Key == pair.SourcePackageKey ? new[] { "a" } : ["b", "c"])
            {
                DbStructureDefinition profile = fixture.AddStructure(package, "Coding-" + code, FhirArtifactClassEnum.Profile);
                fixture.DeclareProfile(profile, coding);
                fixture.AddElement(profile, "Coding");
                fixture.AddTypedElement(profile, "Coding.code", "code");
                fixture.ConfigureDefinition(profile, definition =>
                    definition.Snapshot.Element.Single(element => element.Path == "Coding.code").Fixed = new Code(code));
                fixture.AddType(use, "Coding", coding, profile);
                alternatives.Add(profile.VersionedUrl);
            }
            fixture.ConfigureDefinition(resource, definition =>
                definition.Snapshot.Element.Single(element => element.ElementId == use.Id).Type =
                    [new() { Code = "Coding", Profile = alternatives }]);
            if (package.Key == pair.SourcePackageKey)
            {
                source = use;
            }
            else
            {
                nativeAlternatives.AddRange(alternatives);
            }
        }
        fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        fixture.CompleteAuthoredArrangement(pair);
        fixture.GenerateSafety(pair);
        fixture.ReopenPersistedDatabase();
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
        DbElementOutcome outcome = DbElementOutcome.SelectSingle(fixture.Connection,
            SourceElementKey: source!.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        IReadOnlyList<PersistedProfileDecision> decisions = ProfileUseFacts.ReadDecisions(outcome.ExtensionProhibitionReason!);
        decisions.Count.ShouldBe(2);
        decisions.Select(decision => decision.NativeInput!.ProfileAlternative).Order().ShouldBe(nativeAlternatives.Order());
        decisions.ShouldAllBe(decision => decision.Input.ElementKey == source.Key && decision.NativeInput!.TypeUseToken == 0 &&
            decision.InputOwnerRoot!.ElementId == "ValueSet" && decision.NativeOwnerRoot!.ElementId == "ValueSet" &&
            decision.Kind == ProfileProofKind.Unrepresentable && decision.TargetCanonical == null);
        decisions.ShouldAllBe(decision => decision.RequiredFacts[".code"].Restrictions.Any(restriction =>
            restriction.Kind == ProfileRestrictionKind.Fixed && restriction.Value.GetString() == "a"));
        decisions.SelectMany(decision => decision.NativeFacts[".code"].Restrictions.Where(restriction =>
            restriction.Kind == ProfileRestrictionKind.Fixed).Select(restriction => restriction.Value.GetString()))
            .Order().ShouldBe(["b", "c"]);
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldBeEmpty();
    }

    [Fact]
    public void TypedNegativePersistsAcrossReopenAndRefusesOnlyActualScheduledUsesBeforeWrites()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs(persistDatabase: true);
        FhirPackageComparisonPair negativePair = fixture.PreparePair();
        DbStructureDefinition quantity = fixture.Structure(negativePair.SourcePackage, "Quantity");
        fixture.ConfigureDefinition(quantity, definition => definition.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "opaque-required-quantity", Severity = ConstraintSeverity.Error,
            Expression = "code.empty() or code = 'ad'", Human = "An exact retained source-only invariant.",
        }));
        DbElement source = fixture.AddTypedElement(fixture.Structure(negativePair.SourcePackage), "ValueSet.measure", "Quantity");
        DbElement target = fixture.AddTypedElement(fixture.Structure(negativePair.TargetPackage), "ValueSet.measure", "Quantity");
        DbStructureOutcome owner = fixture.AddOutcome(negativePair, fixture.Structure(negativePair.SourcePackage),
            fixture.Structure(negativePair.TargetPackage));
        fixture.Connect(negativePair, owner, source, target);
        fixture.CompleteAuthoredArrangement(negativePair);
        fixture.GenerateSafety(negativePair);
        fixture.ReopenPersistedDatabase();

        DbElementOutcome outcome = DbElementOutcome.SelectSingle(fixture.Connection,
            SourceFhirPackageKey: negativePair.SourcePackageKey, TargetFhirPackageKey: negativePair.TargetPackageKey,
            SourceElementKey: source.Key)!;
        outcome.ExtensionDefinitionIsProhibited.ShouldBeTrue();
        outcome.IsEquivalent.ShouldBeFalse();
        outcome.GenUrl.ShouldBeNull();
        PersistedProfileDecision decision = ProfileUseFacts.ReadDecisions(outcome.ExtensionProhibitionReason!).ShouldHaveSingleItem();
        decision.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        decision.Input.ElementKey.ShouldBe(source.Key);
        decision.Input.OwnerKey.ShouldBe(source.StructureKey);
        decision.Input.TypeUseToken.ShouldBe(0);
        decision.NativeInput!.ElementKey.ShouldBe(target.Key);
        decision.SourceRoot.ElementId.ShouldBe("Quantity");
        decision.RequiredFacts[""].Invariants.ShouldContain(invariant => invariant.Expression == "code.empty() or code = 'ad'");
        decision.NativeFacts.ShouldNotBeEmpty();
        decision.TargetCanonical.ShouldBeNull();
        decision.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.InvariantTransformation);
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldBeEmpty();
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [negativePair]);

        DbOutcomeClasses.CreateTables(fixture.Connection, forStructures: false);
        ConfigXVer config = ExportConfig(fixture, 0, "negative-prewrite");
        string sentinel = Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5.r4", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "keep every existing output");
        string before = DatabaseSnapshot(fixture);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            new XVerExporter(fixture.Connection, config).Export(specificPairs: [negativePair.SequencePair]));
        error.Message.ShouldContain("Unrepresentable scheduled");
        error.Message.ShouldContain("ValueSet");
        File.ReadAllText(sentinel).ShouldBe("keep every existing output");
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).ShouldBe([sentinel]);
        DatabaseSnapshot(fixture).ShouldBe(before);

        FhirPackageComparisonPair unrelated = new(fixture.Packages[0], fixture.Packages[1]);
        fixture.StampComparison(unrelated);
        fixture.AddOutcome(unrelated, fixture.Structure(unrelated.SourcePackage), fixture.Structure(unrelated.TargetPackage));
        fixture.GenerateSafety(unrelated);
        config = ExportConfig(fixture, 1, "unrelated-representable");
        new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false,
            processVocabulary: false, specificPairs: [unrelated.SequencePair]);
        Directory.GetFiles(config.OutputDirectory, "StructureDefinition-*.json", SearchOption.AllDirectories).ShouldNotBeEmpty();

        config = ExportConfig(fixture, 0, "later-negative-combined");
        config.ExportR4B = true;
        XVerExporter combined = new(fixture.Connection, config);
        IgExporter ig = new(fixture.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, combined, config);
        IgExporter.ExportSchedule schedule = ig.BuildExportSchedule(specificPairs: [unrelated.SequencePair, negativePair.SequencePair]);
        schedule.PackagePairs.Select(pair => pair.SequencePair).ShouldBe([unrelated.SequencePair, negativePair.SequencePair]);
        IgExporter.XVerExportTrackingRecord prepared = ig.PrepareXVerIgs(schedule);
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        List<string> sentinels = [];
        foreach (string directory in prepared.XVerIgs.Select(tracking => tracking.IgRootDir!)
            .Concat(prepared.ValidationIgs.Select(tracking => tracking.IgRootDir!)))
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "sentinel.txt");
            File.WriteAllText(path, "preserve XVer and validation IG");
            sentinels.Add(path);
        }
        Should.Throw<InvalidOperationException>(() => combined.Export(
            specificPairs: [unrelated.SequencePair, negativePair.SequencePair])).Message.ShouldContain("Unrepresentable scheduled");
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).Order().ShouldBe(sentinels.Order());
        sentinels.ShouldAllBe(path => File.ReadAllText(path) == "preserve XVer and validation IG");

        JsonObject document = JsonNode.Parse(outcome.ExtensionProhibitionReason!)!.AsObject();
        document["Decisions"]![0]!["Input"]!["SliceContext"] = ".another-slice";
        outcome.ExtensionProhibitionReason = document.ToJsonString();
        DbElementOutcome.Update(fixture.Connection, outcome);
        fixture.ReopenPersistedDatabase();
        before = DatabaseSnapshot(fixture);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [negativePair]))
            .Message.ShouldContain("decisions");
        DatabaseSnapshot(fixture).ShouldBe(before);
    }

    [Fact]
    public void PreflightPreservesVocabularyFirstNamesAndElementMapTargets()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome owner = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.label", "string");
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
        DbOutcomeClasses.CreateTables(fixture.Connection, forStructures: false);
        DbValueSetOutcome vocabulary = fixture.AddVocabularyMapOutcome(pair, owner.GenName!);
        fixture.CompleteAuthoredArrangement(pair);
        fixture.GenerateSafety(pair);
        ConfigXVer config = ExportConfig(fixture, 0, "prepared-collisions");
        XVerExporter exporter = new(fixture.Connection, config);
        IgExporter ig = new(fixture.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.XVerExportTrackingRecord prepared = ig.PrepareXVerIgs(ig.BuildExportSchedule(specificPairs: [pair.SequencePair]));
        VocabularyFhirExporter vocabExporter = new(exporter, fixture.Connection, NullLoggerFactory.Instance);
        IReadOnlyList<VocabularyFhirExporter.BuiltFile> vocab = vocabExporter.Build(prepared);
        IgExporter.XVerIgExportTrackingRecord tracking = prepared.XVerIgs.ShouldHaveSingleItem();
        StructureFhirExporter structures = new(exporter, fixture.Connection, NullLoggerFactory.Instance);
        StructureFhirExporter.BuiltDefinitions definitions = structures.BuildDefinitions(tracking);
        StructureDefinition profile = definitions.Profiles.ShouldHaveSingleItem().Definition;
        profile.Name.ShouldNotBe(owner.GenName);
        tracking.EdOutcomeMapTargets[element.Key].ShouldNotBeEmpty();
        IgExporter.EdOutcomeMapTargetRecord[] targets = [.. tracking.EdOutcomeMapTargets[element.Key]];
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();

        exporter.Export(includeIgScripts: false, specificPairs: [pair.SequencePair]);
        r4::Hl7.Fhir.Serialization.FhirJsonParser parser = new();
        string[] files = Directory.GetFiles(config.OutputDirectory, "*.json", SearchOption.AllDirectories);
        StructureDefinition emitted = files.Where(path => Path.GetFileName(path) == owner.GenFileName + ".json")
            .Select(path => parser.Parse<StructureDefinition>(File.ReadAllText(path))).ShouldHaveSingleItem();
        emitted.Name.ShouldBe(profile.Name);
        emitted.Url.ShouldBe(profile.Url);
        r4::Hl7.Fhir.Model.ConceptMap emittedVocabulary = files.Where(path => Path.GetFileName(path) == vocabulary.ConceptMapFileName + ".json")
            .Select(path => parser.Parse<r4::Hl7.Fhir.Model.ConceptMap>(File.ReadAllText(path))).ShouldHaveSingleItem();
        emittedVocabulary.Name.ShouldBe(owner.GenName);
        r4::Hl7.Fhir.Model.ConceptMap map = files.Where(path => Path.GetFileName(path) == owner.ElementConceptMapFileName + ".json")
            .Select(path => parser.Parse<r4::Hl7.Fhir.Model.ConceptMap>(File.ReadAllText(path))).ShouldHaveSingleItem();
        foreach (IgExporter.EdOutcomeMapTargetRecord target in targets)
        {
            Canonical canonical = new(target.sdCanonical);
            map.Group.ShouldContain(group => group.Target == canonical.Uri && group.TargetVersion == canonical.Version &&
                group.Element.Any(entry => entry.Code == source.Id && entry.Target.Any(mapped => mapped.Code == target.elementPath)));
        }
    }

    [Theory]
    [InlineData("http://hl7.org/fhir/5.0/ValueSet", true)]
    [InlineData("http://hl7.org/fhir/5.0/StructureDefinition/ValueSet", true)]
    [InlineData("http://hl7.org/fhir/StructureDefinition/ValueSet|5.0.0", true)]
    [InlineData("http://hl7.org/fhir/4.0/StructureDefinition/ValueSet", false)]
    [InlineData("http://hl7.org/fhir/5.0/ValueSet|4.0.1", false)]
    [InlineData("http://example.org/fhir/5.0/ValueSet", false)]
    public void FmlDeclarationsRequireExactCanonicalOrMatchingCoreRelease(string sourceCanonical, bool accepted)
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.InsertFilterElements();
        string directory = Path.Combine(fixture.SourceRoot, "input", "R5toR4");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ValueSet5to4.fml"), $$"""
            /// url = "http://example.org/StructureMap/ValueSet5to4"
            /// name = "ValueSet5to4"
            uses "{{sourceCanonical}}" alias ValueSetR5 as source
            uses "http://hl7.org/fhir/4.0/ValueSet" alias ValueSetR4 as target
            group ValueSet(source src : ValueSetR5, target tgt : ValueSetR4) {
              src.compose.include.filter.op -> tgt.compose.include.filter.op;
            }
            """);
        using MappingLoader loader = new(fixture.Connection, NullLoggerFactory.Instance);
        loader.TryLoadCrossVersionSourceMaps(fixture.SourceRoot, false).ShouldBeTrue();
        DbElementMapping.SelectList(fixture.Connection).Any(row =>
            row.SourceElementId == UnsafeCodeTestFixture.FilterElement &&
            row.TargetFhirPackageKey == fixture.Packages[0].Key &&
            row.FmlSourceKey is not null && row.FmlIsSimpleCopy == true).ShouldBe(accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransitiveAnnotationsSelectOnlyContributingOwnersAndEdges(bool separateOwner)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs(persistDatabase: true);
        fixture.LoadProfileMaps();
        FhirPackageComparisonPair pair = new(fixture.Packages[2], fixture.Packages[0]);
        FhirPackageComparisonPair firstStep = new(pair.SourcePackage, fixture.Packages[1]);
        fixture.CompareProfilePair(pair);
        DbElementMapping contributing = DbElementMapping.SelectList(fixture.Connection,
            SourceFhirPackageKey: firstStep.SourcePackageKey, TargetFhirPackageKey: firstStep.TargetPackageKey,
            SourceElementId: UnsafeCodeTestFixture.FilterElement, TargetElementId: UnsafeCodeTestFixture.FilterElement).Single();
        DbProfileMapOwnerFacts expected = DbProfileMapOwnerFacts.SelectList(fixture.Connection,
            ElementMappingKey: contributing.Key).ShouldHaveSingleItem();
        DbElement source = DbElement.SelectSingle(fixture.Connection, Key: contributing.SourceElementKey)!;
        DbElement target = DbElement.SelectSingle(fixture.Connection, Key: contributing.TargetElementKey)!;
        if (separateOwner)
        {
            DbStructureDefinition profile = fixture.AddStructure(firstStep.SourcePackage, "PassiveValueSet", FhirArtifactClassEnum.Profile);
            fixture.DeclareProfile(profile, fixture.Structure(firstStep.SourcePackage));
            fixture.AddElement(profile, "ValueSet");
            source = fixture.AddElement(profile, source.Id, "code", UnsafeCodeTestFixture.FilterValueSet);
        }

        fixture.AddAnnotation(firstStep, source, target);
        DbProfileMapOwnerFacts decoy = DbProfileMapOwnerFacts.SelectList(fixture.Connection).MaxBy(row => row.Key)!;
        if (separateOwner)
        {
            decoy.ResolutionState = ProfileMapOwnerResolution.Passive;
        }
        else
        {
            contributing.Key = DbElementMapping.GetIndex();
            contributing.Key = DbElementMapping.Insert(fixture.Connection, contributing, insertPrimaryKey: true);
            decoy.ElementMappingKey = contributing.Key;
        }
        DbProfileMapOwnerFacts.Update(fixture.Connection, decoy);
        fixture.CompleteAuthoredArrangement(pair);
        fixture.StampComparison(firstStep);
        fixture.StampComparison(new(fixture.Packages[1], pair.TargetPackage));
        fixture.GenerateProfileOutcomes(pair);

        DbUnsafeCodeTrigger[] triggers = [.. DbUnsafeCodeTrigger.SelectList(fixture.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)];
        triggers.ShouldNotBeEmpty();
        triggers.ShouldAllBe(trigger => JsonSerializer.Deserialize<int[]>(trigger.AnnotationKeysJson)!.SequenceEqual(new[] { expected.AnnotationKey }));
        triggers.ShouldAllBe(trigger => !JsonSerializer.Deserialize<int[]>(trigger.AnnotationKeysJson)!.Contains(decoy.AnnotationKey));
        fixture.ConnectionReopens.ShouldBe(3);
        IReadOnlyList<UnsafeCodeConstraintPlan> plans = UnsafeCodeInputStore.ReadConstraintPlans(fixture.Connection, pair.SourcePackageKey, pair.TargetPackageKey);
        plans.Count.ShouldBe(triggers.Length);
        plans.ShouldAllBe(plan => plan.Provenance.Contains(expected.SourceRelativeFilename, StringComparison.Ordinal));
        UnsafeCodeTestFixture.SerializedConstraints(fixture.BuildDefinitions(pair).Profiles.ShouldHaveSingleItem().Definition, 0).ShouldHaveSingleItem();

        DbUnsafeCodeTrigger altered = DbUnsafeCodeTrigger.SelectSingle(fixture.Connection, Key: triggers[0].Key)!;
        altered.AnnotationKeysJson = JsonSerializer.Serialize(new[] { decoy.AnnotationKey });
        DbUnsafeCodeTrigger.Update(fixture.Connection, altered);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.ReadConstraintPlans(fixture.Connection,
            pair.SourcePackageKey, pair.TargetPackageKey)).Message.ShouldContain("provenance");
    }

    [Fact]
    public void FixtureConsumersNeverPublishMissingDefinitionFacts()
    {
        using UnsafeCodeTestFixture fixture = new();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        fixture.GenerateSafety(pair);
        string revision = DefinitionInventoryStore.RequireReady(fixture.Connection);
        DbStructureDefinition owner = fixture.Structure(pair.SourcePackage);
        DbStructureDefinitionFacts.Delete(fixture.Connection, StructureKey: owner.Key);
        string before = fixture.SafetyPersistenceSnapshot();

        Should.Throw<InvalidOperationException>(() => fixture.GenerateSafety(pair)).Message.ShouldContain("facts");
        DbStructureDefinitionFacts.SelectList(fixture.Connection, StructureKey: owner.Key).ShouldBeEmpty();
        DefinitionInventoryStore.ReadState(fixture.Connection)!.StartedRevision.ShouldBe(revision);
        fixture.SafetyPersistenceSnapshot().ShouldBe(before);
    }

    [Fact]
    public void FixtureProfilesDeclareTheirDatatypeAndAncestryRatherThanTheirName()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        DbFhirPackage package = fixture.Packages[2];
        DbStructureDefinition coding = fixture.Structure(package, "Coding");
        DbStructureDefinition profile = fixture.AddStructure(package, "SimpleQuantity", FhirArtifactClassEnum.Profile);
        fixture.DeclareProfile(profile, coding);
        fixture.AddElement(profile, "Coding");
        fixture.PublishAuthoredDefinitions();

        XVerStructureCatalog catalog = new(fixture.Connection);
        catalog.GetDeclaredType(profile).ShouldBe("Coding");
        catalog.IsProcessed(profile).ShouldBeFalse();
        catalog.ResolveRoot(profile).Path.ShouldBe("Coding");
        catalog.ReadPayload(profile).BaseDefinition.ShouldBe(coding.VersionedUrl);
        catalog.GetAncestors(profile).ShouldHaveSingleItem().Key.ShouldBe(coding.Key);
        DefinitionInventoryStore.RequireReady(fixture.Connection).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void AuthoredNormalizedPayloadRetainsSuppliedProfileRestrictions()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        DbFhirPackage package = fixture.Packages[2];
        DbStructureDefinition quantity = fixture.Structure(package, "Quantity");
        DbStructureDefinition profile = fixture.AddStructure(package, "RestrictedMeasure", FhirArtifactClassEnum.Profile);
        fixture.DeclareProfile(profile, quantity);
        fixture.AddElement(profile, "Quantity");
        DbElement comparator = fixture.AddTypedElement(profile, "Quantity.comparator", "code");
        comparator.MaxCardinality = 0;
        comparator.MaxCardinalityString = "0";
        DbElement.Update(fixture.Connection, comparator);
        fixture.ConfigureDefinition(profile, definition =>
        {
            definition.Snapshot.Element[0].Constraint.Add(new()
            {
                Key = "fixture-quantity", Severity = ConstraintSeverity.Error,
                Human = "No comparator", Expression = "comparator.empty()",
            });
        });
        fixture.PublishAuthoredDefinitions();

        XVerStructureCatalog catalog = new(fixture.Connection);
        catalog.IsProcessed(profile).ShouldBeTrue();
        StructureDefinition recovered = catalog.ReadPayload(profile);
        recovered.Type.ShouldBe("Quantity");
        recovered.Kind.ShouldBe(StructureDefinition.StructureDefinitionKind.ComplexType);
        recovered.Derivation.ShouldBe(StructureDefinition.TypeDerivationRule.Constraint);
        recovered.BaseDefinition.ShouldBe(quantity.VersionedUrl);
        recovered.Snapshot.Element[0].Constraint.ShouldHaveSingleItem().Expression.ShouldBe("comparator.empty()");
        recovered.Snapshot.Element[1].Max.ShouldBe("0");
        DbStructureDefinitionFacts.SelectSingle(fixture.Connection, StructureKey: profile.Key)!
            .SnapshotAvailability.ShouldBe(DefinitionSnapshotAvailability.Available);
        DefinitionInventoryStore.RequireReady(fixture.Connection).ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(0, "configuration")]
    [InlineData(1, "configuration")]
    [InlineData(0, "annotation")]
    [InlineData(1, "annotation")]
    [InlineData(0, "combined-exempt")]
    [InlineData(1, "combined-exempt")]
    public void LoadCompareOutcomesAndBuildHonorBothInputMechanisms(int targetIndex, string mechanism)
    {
        using UnsafeCodeTestFixture fixture = mechanism == "configuration"
            ? UnsafeCodeTestFixture.CreateNoFeatureInputs(persistDatabase: true) : UnsafeCodeTestFixture.CreateProfileInputs(persistDatabase: true);
        if (mechanism != "annotation")
        {
            fixture.WriteConfiguration(mechanism == "configuration"
                ? """[{"system":"http://hl7.org/fhir/filter-operator","code":"exists"}]"""
                : """
                    [
                    {"system":"http://hl7.org/fhir/filter-operator","code":"exists"},
                    {"system":"http://hl7.org/fhir/filter-operator","code":"unsafe-demo","excludedVersion":["4.0.1","4.3.0"]}
                    ]
                    """);
        }

        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        fixture.ConnectionReopens.ShouldBe(3);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
        StructureFhirExporter.BuiltDefinitions built = fixture.BuildDefinitions(pair);
        fixture.ConnectionReopens.ShouldBe(4);
        StructureDefinition profile = built.Profiles.ShouldHaveSingleItem().Definition;
        ElementDefinition.ConstraintComponent[] constraints = UnsafeCodeTestFixture.SerializedConstraints(profile, targetIndex);
        constraints.ShouldHaveSingleItem();
        string extension = built.Extensions.Single(item => item.Definition.Id == "ext-R5-ValueSet.com.inc.fil.op").Definition.Url;
        constraints[0].Expression.ShouldContain(extension);
        foreach ((string? code, bool unsafeCode) in new (string?, bool)[]
        {
            (null, false), ("=", false), ("unresolved", mechanism != "configuration"),
            ("unsafe-demo", mechanism == "annotation"), ("exists", mechanism != "annotation"),
        })
        {
            foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
            {
                foreach (bool mixed in new[] { false, true })
                {
                    UnsafeCodeTestFixture.EvaluateFilter(constraints, targetIndex, code, uri, extension, mixed)
                        .ShouldBe(!unsafeCode || uri == UnsafeCodeConstants.ImplicitRules);
                }
            }
        }
        profile.ImplicitRules.ShouldBeNull();
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ConfigurationChangesRequireSnapshotRegeneration(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateNoFeatureInputs(persistDatabase: true);
        fixture.WriteConfiguration("""[{"system":"http://hl7.org/fhir/filter-operator","code":"exists"}]""");
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        string firstRevision = fixture.InputState.CompletedInputRevision!;
        string firstComparison = fixture.PairState(pair).ComparisonRevision!;
        string[] initial = Constraints();
        string[] initialElements = ElementComparisons();
        Check("exists", false);
        fixture.WriteConfiguration("""[{"system":"http://hl7.org/fhir/filter-operator","code":"="}]""");
        Constraints().ShouldBe(initial);
        Check("exists", false);
        Check("=", true);

        fixture.WriteConfiguration("{ invalid preflight");
        Should.Throw<InvalidDataException>(() => fixture.LoadProfileMaps());
        fixture.InputState.CompletedInputRevision.ShouldBe(firstRevision);
        fixture.PairState(pair).ComparisonRevision.ShouldBe(firstComparison);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
        Constraints().ShouldBe(initial);

        fixture.WriteConfiguration("""[{"system":"http://hl7.org/fhir/filter-operator","code":"="}]""");
        fixture.LoadProfileMaps();
        fixture.InputState.CompletedInputRevision.ShouldNotBe(firstRevision);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]));
        Should.Throw<InvalidOperationException>(() => new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance)
            .GenerateOutcomes(processValueSets: false, specificPairs: [pair.SequencePair]));
        fixture.CompareProfilePair(pair);
        fixture.PairState(pair).ComparisonRevision.ShouldNotBe(firstComparison);
        fixture.PairState(pair).OutcomeInputRevision.ShouldBeNull();
        fixture.GenerateProfileOutcomes(pair);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
        AssertPackageSlotsAndMappedComparisons();
        ElementComparisons().ShouldBe(initialElements);
        Check("exists", true);
        Check("=", false);
        Constraints().ShouldNotBe(initial);
        AssertPackageSlotsAndMappedComparisons();

        File.Delete(fixture.ConfigurationFilename);
        fixture.LoadCompareAndGenerate(targetIndex).SequencePair.ShouldBe(pair.SequencePair);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldBeEmpty();
        fixture.PairState(pair).OutcomeFormatVersion.ShouldBe(UnsafeCodeConstants.OutcomeFormatVersion);
        Constraints().ShouldBeEmpty();
        Check("=", true);
        AssertPackageSlotsAndMappedComparisons();
        fixture.ConnectionReopens.ShouldBeGreaterThan(12);

        string[] Constraints() => UnsafeCodeTestFixture.SerializedConstraints(
            fixture.BuildDefinitions(pair).Profiles.ShouldHaveSingleItem().Definition, targetIndex)
            .Select(constraint => constraint.ToJson()).ToArray();

        void Check(string code, bool expected) => UnsafeCodeTestFixture.EvaluateFilter(
            UnsafeCodeTestFixture.SerializedConstraints(fixture.BuildDefinitions(pair).Profiles.ShouldHaveSingleItem().Definition, targetIndex),
            targetIndex, code).ShouldBe(expected);

        void AssertPackageSlotsAndMappedComparisons()
        {
            List<DbFhirPackage> packages = DbFhirPackage.SelectList(fixture.Connection, orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);
            packages.Count.ShouldBe(5);
            packages.Select(package => package.DefinitionFhirSequence).Distinct().Count().ShouldBe(5);
            DbStructureComparison.SelectList(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey,
                TargetFhirPackageKey: pair.TargetPackageKey).ShouldContain(comparison =>
                    comparison.SourceStructureKey == fixture.Structure(pair.SourcePackage).Key &&
                    comparison.TargetStructureKey == fixture.Structure(pair.TargetPackage).Key);
        }

        string[] ElementComparisons() => DbElementComparison.SelectList(fixture.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            .Select(comparison => comparison.SourceElementId + " -> " + comparison.TargetElementId + " " + comparison.Relationship)
            .Order(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RepresentativeRuleSetDoesNotDuplicateOutput(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateNoFeatureInputs(persistDatabase: true);
        string[] entries = [.. Enumerable.Range(0, 100).Select(index =>
            $$"""{"system":"{{UnsafeCodeTestFixture.FilterSystem}}","code":"configured-{{index:D3}}","excludedRelationship":["equivalent","source-is-narrower-than-target"],"excludedVersion":["1.0.2","3.0.2"]}""")];
        string? expected = null;
        int firstOutcomeKey = 0;
        int triggerCount = 0;
        int expressionLength = 0;
        foreach (bool reordered in new[] { false, true })
        {
            if (reordered)
            {
                foreach (string filename in Directory.GetFiles(Path.Combine(fixture.SourceRoot, "input", "elements"), "*.json"))
                {
                    ConceptMap map = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename));
                    foreach (ConceptMap.SourceElementComponent element in map.Group.SelectMany(group => group.Element))
                    {
                        element.Target.Reverse();
                    }
                    File.WriteAllText(filename, map.ToJson());
                }
            }

            string[] input = reordered
                ? [.. entries.Reverse().SelectMany(entry => new[] { entry, entry.Replace(
                    "\"excludedVersion\":[\"1.0.2\",\"3.0.2\"]",
                    "\"noMapOnly\":false,\"excludedVersion\":[\"3.0.2\",\"1.0.2\",\"3.0.2\"]", StringComparison.Ordinal) })]
                : entries;
            string json = "[" + string.Join(",", input) + "]";
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                document.RootElement.GetArrayLength().ShouldBe(reordered ? 200 : 100);
            }
            fixture.WriteConfiguration(json);
            FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
            DbUnsafeCodeRule.SelectList(fixture.Connection).Count.ShouldBe(100);
            List<DbUnsafeCodeTrigger> triggers = DbUnsafeCodeTrigger.SelectList(fixture.Connection);
            triggers.Count.ShouldBe(100);
            triggers.Select(trigger => trigger.Code).Distinct().Count().ShouldBe(100);
            UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
            StructureFhirExporter.BuiltDefinitions built = fixture.BuildDefinitions(pair);
            StructureDefinition profile = built.Profiles.ShouldHaveSingleItem().Definition;
            string extension = built.Extensions.Single(item => item.Definition.Id == "ext-R5-ValueSet.com.inc.fil.op").Definition.Url;
            ElementDefinition.ConstraintComponent constraint = UnsafeCodeTestFixture.SerializedConstraints(profile, targetIndex).ShouldHaveSingleItem();
            UnsafeCodeTestFixture.SerializedConstraints(fixture.BuildDefinitions(pair).Profiles.ShouldHaveSingleItem().Definition, targetIndex)
                .ShouldHaveSingleItem().ToJson().ShouldBe(constraint.ToJson());
            if (reordered)
            {
                triggers[0].StructureOutcomeKey.ShouldNotBe(firstOutcomeKey);
                triggers.Count.ShouldBe(triggerCount);
                constraint.Expression.Length.ShouldBe(expressionLength);
                constraint.ToJson().ShouldBe(expected);
            }
            else
            {
                firstOutcomeKey = triggers[0].StructureOutcomeKey;
                triggerCount = triggers.Count;
                expressionLength = constraint.Expression.Length;
                expected = constraint.ToJson();
            }

            UnsafeCodeTestFixture.EvaluateFilter([constraint], targetIndex, "configured-099", extension: extension).ShouldBeFalse();
            UnsafeCodeTestFixture.EvaluateFilter([constraint], targetIndex, "configured-099", UnsafeCodeConstants.ImplicitRules, extension).ShouldBeTrue();
            UnsafeCodeTestFixture.EvaluateFilter([constraint], targetIndex, "=").ShouldBeTrue();
            Console.WriteLine($"MANY_RULES target={pair.TargetPackage.PackageVersion} entries={input.Length} rules=100 triggers={triggers.Count} constraints=1 expressionChars={constraint.Expression.Length}");
        }
    }

    [Fact]
    public void ActualFilteredRegenerationInvalidatesOtherPersistedPairs()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs(persistDatabase: true);
        fixture.LoadProfileMaps();
        FhirPackageComparisonPair r4 = new(fixture.Packages[2], fixture.Packages[0]);
        FhirPackageComparisonPair r4b = new(fixture.Packages[2], fixture.Packages[1]);
        fixture.CompareProfilePair(r4);
        fixture.GenerateProfileOutcomes(r4, r4b);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [r4, r4b]);
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldContain(trigger => trigger.TargetFhirPackageKey == r4.TargetPackageKey);
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldContain(trigger => trigger.TargetFhirPackageKey == r4b.TargetPackageKey);
        string r4bComparison = fixture.PairState(r4b).ComparisonRevision!;

        fixture.GenerateProfileOutcomes(r4b);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [r4b]);
        fixture.PairState(r4).ComparisonRevision.ShouldNotBeNull();
        fixture.PairState(r4).OutcomeInputRevision.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [r4]));

        fixture.CompareProfilePairs(r4b);
        fixture.PairState(r4).ComparisonRevision.ShouldBeNull();
        fixture.PairState(r4b).ComparisonRevision.ShouldNotBe(r4bComparison);
        fixture.PairState(r4b).OutcomeInputRevision.ShouldBeNull();
        fixture.GenerateProfileOutcomes(r4b);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [r4b]);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [r4]));
        fixture.BuildDefinitions(r4b).Profiles.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReloadedExportCommandRegeneratesPrerequisites(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = new(fixture.Packages[2], fixture.Packages[targetIndex]);
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand("export", true,
            () => calls.Add("database"),
            () => { fixture.LoadProfileMaps(); calls.Add("maps"); },
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            family =>
            {
                family.ShouldBeNull();
                fixture.CompareProfilePair(pair);
                new ValueSetComparer(fixture.Connection, NullLoggerFactory.Instance).CompareValueSets(specificPairs: [pair.SequencePair]);
                calls.Add("compare");
            },
            family =>
            {
                family.ShouldBeNull();
                new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance)
                    .GenerateOutcomes(specificPairs: [pair.SequencePair]);
                calls.Add("outcomes");
            },
            () =>
            {
                UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
                DbUnsafeCodeTrigger.SelectList(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey,
                    TargetFhirPackageKey: pair.TargetPackageKey).ShouldNotBeEmpty();
                ConfigXVer config = ExportConfig(fixture, targetIndex, "reload-export");
                new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false,
                    specificPairs: [pair.SequencePair]);
                Directory.GetFiles(config.OutputDirectory, "StructureDefinition-*.json", SearchOption.AllDirectories).ShouldNotBeEmpty();
                calls.Add("export");
            });
        calls.ShouldBe(["database", "maps", "substitutions", "type-valuesets", "compare", "outcomes", "export"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LaterInvalidScheduledPairProtectsEarlierExistingIg(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair incomplete = fixture.PreparePair(2, targetIndex);
        ConfigXVer config = ExportConfig(fixture, targetIndex, "later-pair");
        XVerExporter exporter = new(fixture.Connection, config);
        IgExporter ig = new(fixture.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.ExportSchedule schedule = ig.BuildExportSchedule();
        FhirPackageComparisonPair first = schedule.PackagePairs[0];
        first.SequencePair.ShouldNotBe(incomplete.SequencePair);
        fixture.StampComparison(first);
        fixture.GenerateSafety(first);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [first]);
        string root = Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-" + first.SourceFhirSequence.ToRLiteral().ToLowerInvariant() +
            "." + first.TargetFhirSequence.ToRLiteral().ToLowerInvariant());
        Directory.CreateDirectory(root);
        string sentinel = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve earlier IG");
        string before = DatabaseSnapshot(fixture);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => exporter.Export(includeIgScripts: false));
        error.Message.ShouldContain(incomplete.SourcePackage.PackageVersion);
        File.ReadAllText(sentinel).ShouldBe("preserve earlier IG");
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).ShouldBe([sentinel]);
        DatabaseSnapshot(fixture).ShouldBe(before);
    }

    [Fact]
    public void NonReloadExportDoesNotRegenerateAndReloadFailureStopsExport()
    {
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand("export", false,
            () => calls.Add("database"), () => calls.Add("maps"), () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"), _ => calls.Add("compare"), _ => calls.Add("outcomes"),
            () => calls.Add("export"));
        calls.ShouldBe(["export"]);
        calls.Clear();
        Should.Throw<InvalidOperationException>(() => XVerProcessor.ExecuteOutcomeCommand("export", true,
            () => calls.Add("database"), () => calls.Add("maps"), () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"), _ => calls.Add("compare"),
            _ => throw new InvalidOperationException("outcomes failed"), () => calls.Add("export")))
            .Message.ShouldBe("outcomes failed");
        calls.ShouldBe(["database", "maps", "substitutions", "type-valuesets", "compare"]);
    }

    [Theory]
    [InlineData("missing-schema")]
    [InlineData("old-schema")]
    [InlineData("missing-input")]
    [InlineData("partial-input")]
    [InlineData("incompatible-input")]
    [InlineData("missing-pair")]
    [InlineData("partial-comparison")]
    [InlineData("partial-outcome")]
    [InlineData("incompatible-outcome")]
    [InlineData("stale-input")]
    [InlineData("stale-comparison")]
    public void ExportRejectsInvalidScheduledPairsBeforeWriting(string invalid)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        fixture.GenerateSafety(pair);
        Corrupt(fixture, pair, invalid);
        string before = DatabaseSnapshot(fixture);
        ConfigXVer config = ExportConfig(fixture, 0, "fresh-export");
        XVerExporter exporter = new(fixture.Connection, config);
        // The vocabulary outcome schema is deliberately absent: combined export
        // must diagnose safety first, before even attempting vocabulary output.
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            exporter.Export(includeIgScripts: false, specificPairs: [pair.SequencePair]));
        error.Message.ShouldContain("Unsafe-code");
        error.Message.ShouldContain("compare");
        error.Message.ShouldContain("outcomes");
        error.Message.ShouldContain("export");
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        DatabaseSnapshot(fixture).ShouldBe(before);

        string igRoot = Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5.r4");
        Directory.CreateDirectory(igRoot);
        string sentinel = Path.Combine(igRoot, "sentinel.txt");
        byte[] contents = [0, 1, 2, 255];
        File.WriteAllBytes(sentinel, contents);
        Should.Throw<InvalidOperationException>(() =>
            exporter.Export(includeIgScripts: false, specificPairs: [pair.SequencePair]));
        File.ReadAllBytes(sentinel).ShouldBe(contents);
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).ShouldBe([sentinel]);
        DatabaseSnapshot(fixture).ShouldBe(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExportChecksAllAndOnlyActuallyScheduledPairs(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair selected = fixture.PreparePair(2, targetIndex);
        fixture.GenerateSafety(selected);
        ConfigXVer config = ExportConfig(fixture, targetIndex, "selected");
        XVerExporter exporter = new(fixture.Connection, config);
        IgExporter ig = new(fixture.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.ExportSchedule schedule = ig.BuildExportSchedule(specificPairs: [selected.SequencePair]);
        schedule.PackagePairs.ShouldHaveSingleItem().SequencePair.ShouldBe(selected.SequencePair);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, schedule.PackagePairs);
        exporter.Export(includeIgScripts: false, processVocabulary: false, specificPairs: [selected.SequencePair]);
        Directory.Exists(Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5." +
            selected.TargetFhirSequence.ToRLiteral().ToLowerInvariant())).ShouldBeTrue();

        config.OutputDirectory = Path.Combine(fixture.SourceRoot, "all-pairs");
        exporter = new(fixture.Connection, config);
        Should.Throw<InvalidOperationException>(() => exporter.Export(includeIgScripts: false, processVocabulary: false));
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();

        // With a one-step window, the R5->R4 pair is excluded, not certified.
        schedule = ig.BuildExportSchedule(maxStepSize: 1, specificPairs: [selected.SequencePair]);
        schedule.PackagePairs.Count.ShouldBe(targetIndex == 0 ? 0 : 1);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, schedule.PackagePairs);
        config.OutputDirectory = Path.Combine(fixture.SourceRoot, "window");
        new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false, processVocabulary: false,
            maxStepSize: 1, specificPairs: [selected.SequencePair]);
        Directory.GetDirectories(config.OutputDirectory, "hl7.fhir.uv.xver-*").Length.ShouldBe(targetIndex == 0 ? 0 : 1);
    }

    [Fact]
    public void VocabularyOnlyExportBypassesMissingSafetyAndStructureCatalogSchema()
    {
        using UnsafeCodeTestFixture fixture = new();
        DbComparisonClasses.CreateTables(fixture.Connection);
        DbOutcomeClasses.CreateTables(fixture.Connection, forStructures: false);
        DbUnsafeCodeInputState.DropTable(fixture.Connection);
        DbUnsafeCodePairState.DropTable(fixture.Connection);
        DbStructureDefinitionFacts.DropTable(fixture.Connection);
        DbDefinitionInventoryState.DropTable(fixture.Connection);
        DbProfileMapOwnerFacts.DropTable(fixture.Connection);
        ConfigXVer config = ExportConfig(fixture, 0, "vocabulary");
        FhirPackageComparisonPair pair = new(fixture.Packages[2], fixture.Packages[0]);
        DbValueSetOutcome vocabulary = fixture.AddVocabularyMapOutcome(pair, "VocabularyOnly");
        new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false,
            processStructures: false, specificPairs: [pair.SequencePair]);
        Directory.Exists(Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5.r4")).ShouldBeTrue();
        Directory.GetFiles(config.OutputDirectory, vocabulary.ConceptMapFileName + ".json", SearchOption.AllDirectories)
            .ShouldHaveSingleItem();
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN " +
            "('UnsafeCodeInputState', 'UnsafeCodePairState', 'StructureDefinitionFacts', 'DefinitionInventoryState', 'ProfileMapOwnerFacts')";
        Convert.ToInt32(command.ExecuteScalar()).ShouldBe(0);

        config.OutputDirectory = Path.Combine(fixture.SourceRoot, "structures-without-catalog");
        string before = DatabaseSnapshot(fixture);
        Should.Throw<InvalidOperationException>(() => new XVerExporter(fixture.Connection, config)
            .Export(includeIgScripts: false, specificPairs: [pair.SequencePair])).Message.ShouldContain("Unsafe-code");
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        DatabaseSnapshot(fixture).ShouldBe(before);
        Convert.ToInt32(command.ExecuteScalar()).ShouldBe(0);
    }

    private static ConfigXVer ExportConfig(UnsafeCodeTestFixture fixture, int targetIndex, string directory) => new()
    {
        OutputDirectory = Path.Combine(fixture.SourceRoot, directory),
        ExportR2 = false, ExportR3 = false, ExportR4 = targetIndex == 0, ExportR4B = targetIndex == 1,
        ExportR5 = false, ExportR6 = false, LogFactory = NullLoggerFactory.Instance,
    };

    private static void Corrupt(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair, string invalid)
    {
        DbUnsafeCodeInputState input = fixture.InputState;
        DbUnsafeCodePairState state = fixture.PairState(pair);
        switch (invalid)
        {
            case "missing-schema":
                DbUnsafeCodeInputState.DropTable(fixture.Connection);
                return;
            case "old-schema":
                DbUnsafeCodeInputState.DropTable(fixture.Connection);
                using (SqliteCommand command = fixture.Connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE UnsafeCodeInputState (Key INTEGER PRIMARY KEY)";
                    command.ExecuteNonQuery();
                }
                return;
            case "missing-input": DbUnsafeCodeInputState.Delete(fixture.Connection); return;
            case "partial-input": input.CompletedInputRevision = null; break;
            case "incompatible-input": input.FormatVersion++; break;
            case "missing-pair":
                DbUnsafeCodePairState.Delete(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
                return;
            case "partial-comparison": state.ComparisonRevision = null; break;
            case "partial-outcome": state.OutcomeInputRevision = null; break;
            case "incompatible-outcome": state.OutcomeFormatVersion++; break;
            case "stale-input": input.CompletedInputRevision = "new-input"; break;
            case "stale-comparison": state.ComparisonRevision = "new-comparison"; break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }
        DbUnsafeCodeInputState.Update(fixture.Connection, [input]);
        DbUnsafeCodePairState.Update(fixture.Connection, [state]);
    }

    private static string DatabaseSnapshot(UnsafeCodeTestFixture fixture)
    {
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> schema = [];
        while (reader.Read())
        {
            schema.Add(reader.GetString(0) + ":" + (reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        reader.Close();
        command.CommandText = "SELECT total_changes()";
        return string.Join("\n", schema) + ":" + command.ExecuteScalar();
    }

    [Theory]
    [InlineData("outcomes", null)]
    [InlineData("outcomes-vs", FhirArtifactClassEnum.ValueSet)]
    [InlineData("outcomes-sd", FhirArtifactClassEnum.Resource)]
    public void ReloadRunsActualDispatchWithSameFamilyComparisonFirst(string command, FhirArtifactClassEnum? expected)
    {
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand(
            command, true,
            () => calls.Add("database"),
            () => calls.Add("maps"),
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            family => { family.ShouldBe(expected); calls.Add("compare"); },
            family => { family.ShouldBe(expected); calls.Add("outcomes"); });
        calls.ShouldBe(["database", "maps", "substitutions", "type-valuesets", "compare", "outcomes"]);
    }

    [Theory]
    [InlineData("outcomes")]
    [InlineData("outcomes-vs")]
    [InlineData("outcomes-sd")]
    public void WithoutReloadOnlyRequestedOutcomesRun(string command)
    {
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand(
            command, false,
            () => calls.Add("database"),
            () => calls.Add("maps"),
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            _ => calls.Add("compare"),
            _ => calls.Add("outcomes"));
        calls.ShouldBe(["outcomes"]);
    }

    [Fact]
    public void ReloadFailureDoesNotDispatchComparisonOrOutcomes()
    {
        List<string> calls = [];
        Should.Throw<InvalidOperationException>(() => XVerProcessor.ExecuteOutcomeCommand(
            "outcomes-sd", true,
            () => calls.Add("database"),
            () => throw new InvalidOperationException("load failed"),
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            _ => calls.Add("compare"),
            _ => calls.Add("outcomes"))).Message.ShouldBe("load failed");
        calls.ShouldBe(["database"]);
    }
}
