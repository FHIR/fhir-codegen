// <copyright file="UnsafeCodeProfileTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

extern alias r4;
extern alias r4b;
extern alias stu3;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Terminology;
using Fhir.CodeGen.Lib.Configuration;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.FhirPath;
using Hl7.FhirPath;
using Hl7.FhirPath.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

// Other pipeline collections can reset the generated, process-wide SQLite key counters.
[CollectionDefinition(UnsafeCodeProfileCollection.Name, DisableParallelization = true)]
public sealed class UnsafeCodeProfileCollection
{
    internal const string Name = "Unsafe-code profile construction";
}

[Collection(UnsafeCodeProfileCollection.Name)]
public class UnsafeCodeProfileTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NativeMixedCodingBooleanSelectsOnlyCoding(int targetIndex)
    {
        using UnsafeCodeTestFixture main = UnsafeCodeTestFixture.CreateMixedChoiceInputs(
            targetIndex, "boolean", "qualified", enableRules: true);
        FhirPackageComparisonPair pair = main.LoadCompareAndGenerate(targetIndex);
        UnsafeCodeOutcomeTests.AssertMixedChoiceSetup(main, pair, "boolean", "qualified");
        UnsafeCodeInputStore.RequireExportReady(main.Connection, [pair]);
        main.ConnectionReopens.ShouldBe(3);
        main.WriteMixedChoiceStageObservation(pair, "boolean", "qualified");
        DbStructureOutcome outcome = DbStructureOutcome.SelectList(main.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            .Single(row => row.SourceId == "Parameters");
        DbUnsafeCodeTrigger[] triggers = [.. DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)];
        triggers.ShouldHaveSingleItem();
        DbUnsafeCodeTrigger trigger = triggers.Single(row => row.System == UnsafeCodeTestFixture.MixedCodingSystem);
        trigger.SourceElementId.ShouldBe(UnsafeCodeTestFixture.MixedChoiceElement);
        trigger.TargetElementId.ShouldBe(UnsafeCodeTestFixture.MixedChoiceElement);
        trigger.Code.ShouldBe("unsafe");
        trigger.IsWildcardVersion.ShouldBeTrue();
        trigger.SourceVersion.ShouldBeNull();
        trigger.EvidenceKeysJson.ShouldBe("[]");
        trigger.AnnotationKeysJson.ShouldBe("[]");
        int ruleKey = JsonSerializer.Deserialize<int[]>(trigger.RuleKeysJson)!.ShouldHaveSingleItem();
        DbUnsafeCodeRule rule = DbUnsafeCodeRule.SelectSingle(main.Connection, Key: ruleKey)!;
        rule.System.ShouldBe(UnsafeCodeTestFixture.MixedCodingSystem);
        rule.NoMapOnly.ShouldBeFalse();
        rule.EntryIndex.ShouldBe(0);
        rule.SourceRelativeFilename.Replace('\\', '/').ShouldBe("input/ig-support/unsafe-code-mappings.json");
        UnsafeCodeConstraintPlan plan = UnsafeCodeInputStore.ReadConstraintPlans(main.Connection, pair.SourcePackageKey, pair.TargetPackageKey)
            .Single(candidate => candidate.Row.Key == trigger.Key);
        plan.Provenance.Replace('\\', '/').ShouldBe("input/ig-support/unsafe-code-mappings.json entry 0");
        ImmutableArray<UnsafeCodePathStep> path = plan.SourcePath;
        path.ShouldBe(ImmutableArray.Create<UnsafeCodePathStep>(new("Parameters", null, false),
            new("Parameters.parameter", null, false), new(UnsafeCodeTestFixture.MixedChoiceElement, null, false), new("Coding", "Coding", false)));
        string persistedTriggers = JsonSerializer.Serialize(triggers);
        string persistedStamp = JsonSerializer.Serialize(main.PairState(pair));
        SortedDictionary<string, string[]> mainState = main.MixedChoiceRepresentationState();
        StructureFhirExporter.BuiltDefinitions built = main.BuildDefinitions(pair);
        UnsafeCodeSafetyResolution selected = built.Representations.ResolveSafety(outcome.Key, path);
        selected.Failure.ShouldBeNull();
        RepresentationLocation selectedCoding = selected.Locations.ShouldHaveSingleItem();
        selectedCoding.SourceType.ShouldBe("Coding");
        selectedCoding.TargetType.ShouldBe("Coding");
        selectedCoding.State.ShouldBe(RepresentationState.Observable);
        selectedCoding.Carrier!.Kind.ShouldBe(CodeCarrierKind.Coding);
        selectedCoding.Steps.ShouldBe(ImmutableArray.Create<RepresentationStep>(new MemberStep("parameter"),
            new MemberStep("value"), new ChoiceStep("Coding")));
        selectedCoding.Transformations.ShouldBeEmpty();
        selectedCoding.SourceBinding.ShouldBe(new UnsafeCodeCanonical(null, null));
        UnsafeCodeSafetyResolution legacy = built.Representations.ResolveSafety(outcome.Key, path[..^1]);
        legacy.Failure.ShouldBeNull();
        RepresentationLocation legacyCoding = legacy.Locations.ShouldHaveSingleItem();
        legacyCoding.Steps.ShouldBe(selectedCoding.Steps);
        legacyCoding.SourceType.ShouldBe(selectedCoding.SourceType);
        legacyCoding.TargetType.ShouldBe(selectedCoding.TargetType);
        legacyCoding.State.ShouldBe(selectedCoding.State);
        legacyCoding.SourceElementId.ShouldBe(selectedCoding.SourceElementId);
        legacyCoding.SourceBinding.ShouldBe(selectedCoding.SourceBinding);
        legacyCoding.Carrier!.Kind.ShouldBe(selectedCoding.Carrier.Kind);
        legacyCoding.Carrier.System.ShouldBe(selectedCoding.Carrier.System);
        legacyCoding.Carrier.Version.ShouldBe(selectedCoding.Carrier.Version);
        legacyCoding.Carrier.Code.ShouldBe(selectedCoding.Carrier.Code);
        legacyCoding.Transformations.ShouldBeEmpty();
        StructureDefinition activeProfile = built.Profiles.Single(profile => profile.Definition.Type == "Parameters").Definition;
        activeProfile.ImplicitRules.ShouldBeNull();
        ElementDefinition.ConstraintComponent condition = UnsafeCodeTestFixture.SerializedConstraints(activeProfile, targetIndex).ShouldHaveSingleItem();
        condition.Severity.ShouldBe(ConstraintSeverity.Error);
        condition.Human.ShouldBe(UnsafeCodeConstraintEmitter.Human);
        condition.Source.ShouldBe(activeProfile.Url);
        condition.Key.ShouldStartWith("xv-unsafe-code-");
        condition.Expression.ShouldContain(".ofType(Coding)");
        condition.Expression.ShouldNotContain("boolean");
        UnsafeCodeTestFixture.WriteMixedChoiceObservation("MEASURED_H2_CONSTRUCTION", new
        {
            target = pair.TargetFhirSequence.ToRLiteral(), alternative = "boolean",
            result = "constructed", path, plan.Provenance, condition.Key, condition.Expression,
        });
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)).ShouldBe(persistedTriggers);
        JsonSerializer.Serialize(main.PairState(pair)).ShouldBe(persistedStamp);

        using UnsafeCodeTestFixture twin = UnsafeCodeTestFixture.CreateMixedChoiceInputs(
            targetIndex, "boolean", "qualified", enableRules: false);
        FhirPackageComparisonPair twinPair = twin.LoadCompareAndGenerate(targetIndex);
        UnsafeCodeInputStore.RequireExportReady(twin.Connection, [twinPair]);
        DbUnsafeCodeTrigger.SelectList(twin.Connection).ShouldBeEmpty();
        SortedDictionary<string, string[]> twinState = twin.MixedChoiceRepresentationState();
        twinState.Keys.ShouldBe(mainState.Keys);
        foreach ((string table, string[] rows) in mainState)
        {
            twinState[table].ShouldBe(rows, "all-column semantic correspondence: " + table);
        }
        StructureFhirExporter.BuiltDefinitions twinBuilt = twin.BuildDefinitions(twinPair);
        StructureDefinition inactiveProfile = twinBuilt.Profiles.Single(profile => profile.Definition.Type == "Parameters").Definition;
        UnsafeCodeTestFixture.SerializedConstraints(inactiveProfile, targetIndex).ShouldBeEmpty();
        inactiveProfile.ImplicitRules.ShouldBeNull();
        DbStructureOutcome twinOutcome = DbStructureOutcome.SelectList(twin.Connection,
            SourceFhirPackageKey: twinPair.SourcePackageKey, TargetFhirPackageKey: twinPair.TargetPackageKey)
            .Single(row => row.SourceId == "Parameters");
        ImmutableArray<RepresentationLocation> locations = twinBuilt.Representations.Resolve(twinOutcome.Key, path[..^1]);
        locations.Select(location => location.SourceType).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { "Coding", "boolean" }.Order(StringComparer.Ordinal));
        locations.ShouldAllBe(location => location.SourceType == location.TargetType);
        RepresentationLocation coding = locations.Single(location => location.SourceType == "Coding");
        coding.State.ShouldBe(RepresentationState.Observable);
        coding.Carrier!.Kind.ShouldBe(CodeCarrierKind.Coding);
        coding.Transformations.ShouldBeEmpty();
        UnsafeCodeTestFixture.WriteMixedChoiceObservation("MEASURED_H2_PROJECTION", new
        {
            target = pair.TargetFhirSequence.ToRLiteral(), alternative = "boolean", path,
            tables = twinState.Select(entry => new { table = entry.Key, rows = entry.Value.Length }),
            locations = locations.Select(location => new
            {
                state = location.State.ToString(), location.SourceType, location.TargetType, location.SourceElementId,
                location.SourceBinding, carrier = location.Carrier?.Kind.ToString(),
                steps = location.Steps.Select(step => step.ToString()), location.Transformations,
            }),
            inactiveProfile = new { inactiveProfile.Url, inactiveProfile.Type, inactiveProfile.ImplicitRules,
                differential = inactiveProfile.Differential.Element.Select(element => new
                    { element.ElementId, element.Path, types = element.Type.Select(type => type.Code) }) },
        });
        foreach ((string parameter, bool expected) in new[]
        {
            ("""{"name":"control","valueBoolean":true}""", true),
            ("""{"name":"control","valueBoolean":false}""", true),
            ("""{"name":"control","valueCoding":{"system":"urn:example:codes","code":"unsafe"}}""", false),
        })
        {
            ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex,
                """{"resourceType":"Parameters","parameter":[""" + parameter + "]}");
            instance.Children("parameter").ShouldHaveSingleItem().Children("value").ShouldHaveSingleItem();
            ITypedElement[] results = [.. Compiler.Compile(condition.Expression)(instance, new EvaluationContext())];
            results.ShouldHaveSingleItem().Value.ShouldBeOfType<bool>().ShouldBe(expected);
        }
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)).ShouldBe(persistedTriggers);

        UnsafeCodeConstraintPlan broad = plan with { SourcePath = path[..^1] };
        activeProfile.Differential.Element[0].Constraint.Clear();
        UnsafeCodeConstraintEmitter.Attach(built.Profiles, built.Representations, [broad], "R5->" + pair.TargetFhirSequence.ToRLiteral());
        string legacyIdentity = JsonSerializer.Serialize(new
        {
            Profile = activeProfile.Url,
            Direction = "R5->" + pair.TargetFhirSequence.ToRLiteral(),
            Owners = Array.Empty<string>(),
            SourcePath = broad.SourcePath,
        });
        activeProfile.Differential.Element[0].Constraint.ShouldHaveSingleItem().Key.ShouldBe(UnsafeCodeConstraintEmitter.Key(legacyIdentity));
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)).ShouldBe(persistedTriggers);
        JsonSerializer.Serialize(main.PairState(pair)).ShouldBe(persistedStamp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SelectedMixedCodingCodeRetainsIndependentCarriers(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateMixedChoiceInputs(targetIndex, "code", "qualified", enableRules: true);
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        IReadOnlyList<UnsafeCodeConstraintPlan> plans = UnsafeCodeOutcomeTests.AssertSelectedMixedPlans(fixture, pair, primitive: true);
        string persisted = fixture.SafetyPersistenceSnapshot();
        StructureFhirExporter.BuiltDefinitions built = fixture.BuildDefinitions(pair);
        foreach (UnsafeCodeConstraintPlan plan in plans)
        {
            UnsafeCodeSafetyResolution resolution = built.Representations.ResolveSafety(plan.Row.StructureOutcomeKey, plan.SourcePath);
            resolution.Failure.ShouldBeNull();
            RepresentationLocation location = resolution.Locations.ShouldHaveSingleItem();
            location.State.ShouldBe(RepresentationState.Observable);
            location.SourceType.ShouldBe(plan.SourcePath[^1].TypeName);
            location.SourceType.ShouldBe(location.TargetType);
            location.Carrier!.Kind.ShouldBe(location.SourceType == "code" ? CodeCarrierKind.Primitive : CodeCarrierKind.Coding);
            location.SourceBinding.ShouldBe(new UnsafeCodeCanonical("http://example.org/ValueSet/mixed-choice-primitive", "primitive-valueset-v3"));
            location.SourceElementId.ShouldBe(UnsafeCodeTestFixture.MixedChoiceElement);
            location.Transformations.ShouldBeEmpty();
        }

        StructureDefinition profile = built.Profiles.Single(candidate => candidate.Definition.Type == "Parameters").Definition;
        IReadOnlyList<ElementDefinition.ConstraintComponent> conditions = UnsafeCodeTestFixture.SerializedConstraints(profile, targetIndex);
        conditions.Count.ShouldBe(2);
        conditions.Select(condition => condition.Key).Distinct().Count().ShouldBe(2);
        conditions.ShouldContain(condition => condition.Expression.Contains(".ofType(code)", StringComparison.Ordinal) &&
            !condition.Expression.Contains(".version", StringComparison.Ordinal));
        conditions.ShouldContain(condition => condition.Expression.Contains(".ofType(Coding)", StringComparison.Ordinal) &&
            condition.Expression.Contains(".version.where($this = 'primitive-v1')", StringComparison.Ordinal));
        foreach (ElementDefinition.ConstraintComponent condition in conditions)
        {
            ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex,
                """{"resourceType":"Parameters","parameter":[{"name":"control","valueCode":"unsafe"}]}""");
            ITypedElement[] results = [.. Compiler.Compile(condition.Expression)(instance, new EvaluationContext())];
            results.ShouldHaveSingleItem().Value.ShouldBeOfType<bool>();
        }

        fixture.SafetyPersistenceSnapshot().ShouldBe(persisted);
    }

    [Theory]
    [InlineData(0, "legacy-code")]
    [InlineData(1, "legacy-code")]
    [InlineData(0, "legacy-missing-code-row")]
    [InlineData(1, "legacy-missing-code-row")]
    [InlineData(0, "invalid-endpoint")]
    [InlineData(1, "invalid-endpoint")]
    [InlineData(0, "missing-comparison")]
    [InlineData(1, "missing-comparison")]
    [InlineData(0, "unsupported-target")]
    [InlineData(1, "unsupported-target")]
    [InlineData(0, "opaque-attachment")]
    [InlineData(1, "opaque-attachment")]
    public void MixedChoiceSafetyFailuresRetainContextAndRecovery(int targetIndex, string scenario)
    {
        bool legacy = scenario.StartsWith("legacy", StringComparison.Ordinal);
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateMixedChoiceInputs(
            targetIndex, legacy ? "code" : "boolean", "qualified", enableRules: true);
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        UnsafeCodeConstraintPlan plan = UnsafeCodeInputStore.ReadConstraintPlans(fixture.Connection, pair.SourcePackageKey, pair.TargetPackageKey)
            .Single(candidate => candidate.Row.System == UnsafeCodeTestFixture.MixedCodingSystem);
        if (legacy)
        {
            plan = plan with { SourcePath = plan.SourcePath[..^1] };
        }
        else if (scenario == "invalid-endpoint")
        {
            plan = plan with { SourcePath = plan.SourcePath.SetItem(plan.SourcePath.Length - 1, new("boolean", "Coding", false)) };
        }

        if (scenario == "legacy-missing-code-row")
        {
            DbElement source = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: UnsafeCodeTestFixture.MixedChoiceElement)!;
            DbElementType code = DbElementType.SelectList(fixture.Connection, ElementKey: source.Key).Single(type => type.TypeName == "code");
            DbElementType.Delete(fixture.Connection, Key: code.Key);
        }
        if (scenario is "missing-comparison" or "unsupported-target")
        {
            DbElementTypeComparison comparison = DbElementTypeComparison.SelectList(fixture.Connection,
                SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey,
                SourceElementId: UnsafeCodeTestFixture.MixedChoiceElement).Single(row => row.SourceTypeLiteral == "Coding");
            if (scenario == "missing-comparison")
            {
                DbElementTypeComparison.Delete(fixture.Connection, Key: comparison.Key);
            }
            else
            {
                DbElementType boolean = DbElementType.SelectList(fixture.Connection, ElementKey: comparison.TargetElementKey)
                    .Single(type => type.TypeName == "boolean");
                comparison.TargetElementTypeKey = boolean.Key;
                comparison.TargetTypeLiteral = "boolean";
                DbElementTypeComparison.Update(fixture.Connection, [comparison]);
            }
        }

        string persisted = fixture.SafetyPersistenceSnapshot();
        // Construction of this independently staged inactive twin gives an attachment index
        // without deleting or rewriting the main's persisted active plans.
        using UnsafeCodeTestFixture twin = UnsafeCodeTestFixture.CreateMixedChoiceInputs(targetIndex,
            legacy ? "code" : "boolean", "qualified", enableRules: false);
        FhirPackageComparisonPair twinPair = twin.LoadCompareAndGenerate(targetIndex);
        if (scenario == "legacy-missing-code-row")
        {
            DbElement source = DbElement.SelectSingle(twin.Connection, FhirPackageKey: twinPair.SourcePackageKey, Id: UnsafeCodeTestFixture.MixedChoiceElement)!;
            DbElementType code = DbElementType.SelectList(twin.Connection, ElementKey: source.Key).Single(type => type.TypeName == "code");
            DbElementType.Delete(twin.Connection, Key: code.Key);
        }
        StructureFhirExporter.BuiltDefinitions twinBuilt = twin.BuildDefinitions(twinPair);
        if (scenario is "missing-comparison" or "unsupported-target")
        {
            InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => fixture.BuildDefinitions(pair));
            failure.Message.ShouldContain(scenario == "missing-comparison" ? "Missing" : "ErasedIdentity");
            failure.Message.ShouldContain(JsonSerializer.Serialize(plan.SourcePath));
            failure.Message.ShouldContain(plan.ProfileCanonical);
            failure.Message.ShouldContain(plan.Provenance);
            failure.Message.ShouldEndWith("Regenerate load, compare, outcomes, and export after resolving the source-value representation.");
            fixture.SafetyPersistenceSnapshot().ShouldBe(persisted);
            return;
        }

        DbStructureOutcome twinOutcome = DbStructureOutcome.SelectList(twin.Connection, SourceFhirPackageKey: twinPair.SourcePackageKey,
            TargetFhirPackageKey: twinPair.TargetPackageKey).Single(row => row.SourceId == "Parameters");
        DbUnsafeCodeTrigger row = plan.Row;
        row.StructureOutcomeKey = twinOutcome.Key;
        if (scenario == "opaque-attachment")
        {
            DbElement choice = DbElement.SelectSingle(twin.Connection, FhirPackageKey: twinPair.SourcePackageKey,
                Id: UnsafeCodeTestFixture.MixedChoiceElement)!;
            DbElementOutcome source = DbElementOutcome.SelectList(twin.Connection, SourceFhirPackageKey: twinPair.SourcePackageKey,
                TargetFhirPackageKey: twinPair.TargetPackageKey).Single(element => element.SourceElementKey == choice.Key);
            twinBuilt.Representations.Attach(twinOutcome, source, new("Parameters.parameter.extension", "urn:example:opaque"));
            UnsafeCodeSafetyResolution resolution = twinBuilt.Representations.ResolveSafety(twinOutcome.Key, plan.SourcePath);
            resolution.Failure.ShouldBeNull();
            resolution.Locations.Length.ShouldBe(2);
            resolution.Locations.ShouldContain(location => location.State == RepresentationState.Observable && location.TargetType == "Coding");
            resolution.Locations.ShouldContain(location => location.State == RepresentationState.OpaqueSubstitution && location.SourceType == "Coding");
        }
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            UnsafeCodeConstraintEmitter.Attach(twinBuilt.Profiles, twinBuilt.Representations, [plan], "R5->" + pair.TargetFhirSequence.ToRLiteral()));
        error.Message.ShouldContain(legacy ? "AmbiguousSourceUse" : scenario == "opaque-attachment" ? "OpaqueSubstitution" : "InvalidSourceUse");
        error.Message.ShouldContain(JsonSerializer.Serialize(plan.SourcePath));
        error.Message.ShouldContain(plan.ProfileCanonical);
        error.Message.ShouldContain("qualified code urn:example:codes|*#unsafe");
        error.Message.ShouldContain(plan.Provenance);
        error.Message.ShouldEndWith("Regenerate load, compare, outcomes, and export after resolving the source-value representation.");
        fixture.SafetyPersistenceSnapshot().ShouldBe(persisted);
    }

    private static readonly FhirPathCompiler Compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
    private const string ImportedNativeExpression = "($this.jurisdiction.coding.where($this.code.where($this = 'unsafe').exists() and " +
        "$this.system.where($this = 'urn:example:source').exists()).exists() implies implicitRules.where($this = " +
        "'https://hl7.org/fhir/implicit-rules/omitted-mandatory-interversion-code').exists())";

    [Theory]
    [InlineData(0, "IdentityChanging")]
    [InlineData(1, "IdentityChanging")]
    [InlineData(0, "CodeOnly")]
    [InlineData(1, "CodeOnly")]
    [InlineData(0, "SystemOnly")]
    [InlineData(1, "SystemOnly")]
    [InlineData(0, "SystemVersionOnly")]
    [InlineData(1, "SystemVersionOnly")]
    public void ImportedIdentityChangingNativeMapBlocksConstruction(int targetIndex, string scenario) =>
        AssertImportedNativeObservation(targetIndex, Enum.Parse<UnsafeCodeTestFixture.ImportedNativeScenario>(scenario));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ImportedIdentityPreservingNativeMapEnforcesImplicitRules(int targetIndex) =>
        AssertImportedNativeObservation(targetIndex, UnsafeCodeTestFixture.ImportedNativeScenario.IdentityPreserving);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ImportedSourceNonmemberDoesNotAcquireNativeTransformation(int targetIndex) =>
        AssertImportedNativeObservation(targetIndex, UnsafeCodeTestFixture.ImportedNativeScenario.SourceNonmember);

    private static void AssertImportedNativeObservation(int targetIndex, UnsafeCodeTestFixture.ImportedNativeScenario scenario)
    {
        using UnsafeCodeTestFixture main = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex, scenario, enableRule: true);
        FhirPackageComparisonPair pair = main.LoadCompareAndGenerate(targetIndex);
        UnsafeCodeInputStore.RequireExportReady(main.Connection, [pair]);
        main.ConnectionReopens.ShouldBe(3);
        DbValueSet sourceOuter = main.ImportedValueSet(pair.SourcePackage, "outer");
        DbValueSet sourceInner = main.ImportedValueSet(pair.SourcePackage, "inner");
        DbValueSet targetOuter = main.ImportedValueSet(pair.TargetPackage, "outer");
        DbValueSet targetInner = main.ImportedValueSet(pair.TargetPackage, "inner");
        foreach ((DbFhirPackage package, DbValueSet outer, DbValueSet inner) in new[]
            { (pair.SourcePackage, sourceOuter, sourceInner), (pair.TargetPackage, targetOuter, targetInner) })
        {
            outer.FhirPackageKey.ShouldBe(package.Key);
            inner.FhirPackageKey.ShouldBe(package.Key);
            outer.CanExpand.ShouldBeFalse();
            inner.CanExpand.ShouldBeFalse();
            outer.Version.ShouldNotBe(UnsafeCodeTestFixture.ImportedSystemVersion);
            inner.Version.ShouldNotBe(UnsafeCodeTestFixture.ImportedSystemVersion);
            outer.Compose!.Include[0].ValueSet.ShouldBe([inner.VersionedUrl]);
            DbValueSet.SelectList(main.Connection, FhirPackageKey: package.Key, UnversionedUrl: inner.UnversionedUrl, Version: inner.Version)
                .ShouldHaveSingleItem().Key.ShouldBe(inner.Key);
            DbValueSet.SelectList(main.Connection, FhirPackageKey: package.Key, UnversionedUrl: outer.UnversionedUrl, Version: outer.Version)
                .ShouldHaveSingleItem().Key.ShouldBe(outer.Key);
        }
        sourceOuter.UnversionedUrl.ShouldNotBe(targetOuter.UnversionedUrl);
        sourceInner.UnversionedUrl.ShouldNotBe(targetInner.UnversionedUrl);

        DbUnsafeCodeMapEvidence map = DbUnsafeCodeMapEvidence.SelectList(main.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey).ShouldHaveSingleItem();
        map.SourceValueSetUrl.ShouldBe(sourceInner.UnversionedUrl);
        map.SourceValueSetVersion.ShouldBe(sourceInner.Version);
        map.TargetValueSetUrl.ShouldBe(targetInner.UnversionedUrl);
        map.TargetValueSetVersion.ShouldBe(targetInner.Version);
        map.SourceSystem.ShouldBe(UnsafeCodeTestFixture.ImportedSourceSystem);
        map.SourceCode.ShouldBe("unsafe");
        map.SourceSystemVersion.ShouldBe(UnsafeCodeTestFixture.ImportedSystemVersion);
        (string targetSystem, string targetVersion, string targetCode) = UnsafeCodeTestFixture.ImportedTargetIdentity(scenario);
        map.TargetSystem.ShouldBe(targetSystem);
        map.TargetCode.ShouldBe(targetCode);
        map.TargetSystemVersion.ShouldBe(targetVersion);
        map.ExplicitNoMap.ShouldBeFalse();
        map.GroupIndex.ShouldBe(0);
        map.SourceIndex.ShouldBe(0);
        map.TargetIndex.ShouldBe(0);
        string destination = targetIndex == 0 ? "R4" : "R4B";
        map.ConceptMapId.ShouldBe("imported-native-R5-" + destination);
        map.ConceptMapUrl.ShouldBe("http://example.org/ConceptMap/" + map.ConceptMapId);
        map.SourceRelativeFilename.Replace('\\', '/').ShouldBe("input/codes/ConceptMap-R5-imported-native-" + destination + ".json");
        bool sourceMember = sourceInner.Compose!.Include.Any(include => include.System == map.SourceSystem &&
            include.Version == map.SourceSystemVersion && include.Concept.Any(concept => concept.Code == map.SourceCode));
        sourceMember.ShouldBe(scenario != UnsafeCodeTestFixture.ImportedNativeScenario.SourceNonmember);
        targetInner.Compose!.Include.ShouldContain(include => include.System == map.TargetSystem &&
            include.Version == map.TargetSystemVersion && include.Concept.Any(concept => concept.Code == map.TargetCode));
        if (!sourceMember)
        {
            sourceOuter.Compose!.Include.ShouldContain(include => include.System == map.SourceSystem &&
                include.Version == map.SourceSystemVersion && include.Concept.Any(concept => concept.Code == map.SourceCode));
        }

        DbStructureOutcome outcome = DbStructureOutcome.SelectList(main.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            .Single(row => row.SourceId == "ValueSet");
        DbUnsafeCodeTrigger[] triggers = [.. DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)];
        Console.WriteLine("MEASURED_IMPORTED_NATIVE_STAGE " + JsonSerializer.Serialize(new
        {
            baseline = "e153f6ef797a0194b8a36ca1994818a07e08140e", destination, scenario = scenario.ToString(),
            sourceOuter = sourceOuter.VersionedUrl, sourceInner = sourceInner.VersionedUrl,
            targetOuter = targetOuter.VersionedUrl, targetInner = targetInner.VersionedUrl, sourceMember,
            packages = new[] { pair.SourcePackage, pair.TargetPackage }.Select(package => new
                { package.PackageId, package.PackageVersion, package.DefinitionFhirSequence }),
            scopes = new[] { sourceOuter, sourceInner, targetOuter, targetInner }.Select(scope => new
            {
                scope.FhirPackageKey, scope.UnversionedUrl, scope.Version, scope.VersionedUrl, scope.CanExpand,
                includes = scope.Compose!.Include.Select(include => new { include.System, include.Version, include.ValueSet,
                    members = include.Concept.Select(concept => concept.Code) }),
            }),
            map, triggers, rules = DbUnsafeCodeRule.SelectList(main.Connection), state = main.PairState(pair),
            comparisons = DbElementComparison.SelectList(main.Connection, SourceFhirPackageKey: pair.SourcePackageKey,
                TargetFhirPackageKey: pair.TargetPackageKey).Select(comparison => new
            {
                comparison.SourceElementId, comparison.TargetElementId, comparison.Relationship, comparison.Steps,
                comparison.ElementMappingKey, comparison.ContentKeys,
                types = DbElementTypeComparison.SelectList(main.Connection, ElementComparisonKey: comparison.Key)
                    .Select(type => new { type.SourceTypeLiteral, type.TargetTypeLiteral, type.Relationship }),
                outcomes = DbElementOutcomeTarget.SelectList(main.Connection, ElementComparisonKey: comparison.Key),
            }),
        }));
        triggers.ShouldNotBeEmpty();
        triggers.ShouldAllBe(trigger => trigger.System == UnsafeCodeTestFixture.ImportedSourceSystem && trigger.Code == "unsafe" &&
            trigger.EvidenceKeysJson == "[]" && trigger.AnnotationKeysJson == "[]" && trigger.IsWildcardVersion &&
            trigger.SourceVersion == null && trigger.VersionsJson == "[]");
        DbUnsafeCodeTrigger boundTrigger = triggers.Single(trigger => trigger.SourceElementId == UnsafeCodeTestFixture.ImportedNativeElement);
        boundTrigger.TargetElementId.ShouldBe(UnsafeCodeTestFixture.ImportedNativeElement);
        ImmutableArray<UnsafeCodePathStep> path = [.. JsonSerializer.Deserialize<UnsafeCodePathStep[]>(boundTrigger.SourcePathJson)!];
        path.Select(step => step.ElementId).ShouldBe(["ValueSet", UnsafeCodeTestFixture.ImportedNativeElement, "CodeableConcept.coding"]);
        foreach (DbUnsafeCodeTrigger trigger in triggers)
        {
            int key = JsonSerializer.Deserialize<int[]>(trigger.RuleKeysJson)!.ShouldHaveSingleItem();
            DbUnsafeCodeRule rule = DbUnsafeCodeRule.SelectSingle(main.Connection, Key: key)!;
            rule.System.ShouldBe(UnsafeCodeTestFixture.ImportedSourceSystem);
            rule.Code.ShouldBe("unsafe");
            rule.Version.ShouldBeNull();
            rule.NoMapOnly.ShouldBeFalse();
            rule.ExcludedRelationshipLiteral.ShouldBeEmpty();
            rule.ExcludedVersionLiteral.ShouldBeEmpty();
            rule.EntryIndex.ShouldBe(0);
            rule.SourceRelativeFilename.Replace('\\', '/').ShouldBe("input/ig-support/unsafe-code-mappings.json");
        }

        foreach (string elementId in new[] { UnsafeCodeTestFixture.ImportedNativeElement, "CodeableConcept.coding", "Coding.system", "Coding.version", "Coding.code" })
        {
            DbElementComparison comparison = DbElementComparison.SelectList(main.Connection,
                SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey, SourceElementId: elementId)
                .ShouldHaveSingleItem();
            comparison.TargetElementId.ShouldBe(elementId);
            DbElementMapping.SelectList(main.Connection, SourceFhirPackageKey: pair.SourcePackageKey,
                TargetFhirPackageKey: pair.TargetPackageKey, SourceElementId: elementId).ShouldHaveSingleItem()
                .TargetElementId.ShouldBe(elementId);
            comparison.TargetElementKey.ShouldNotBeNull();
            DbElementTypeComparison.SelectList(main.Connection, ElementComparisonKey: comparison.Key)
                .ShouldNotBeEmpty("contributing types for " + elementId);
            DbElementOutcomeTarget.SelectList(main.Connection, ElementComparisonKey: comparison.Key).ShouldNotBeEmpty();
        }
        SortedDictionary<string, string[]> mainState = main.ImportedNativeSemanticState();
        using UnsafeCodeTestFixture probe = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex, scenario, enableRule: false);
        FhirPackageComparisonPair probePair = probe.LoadCompareAndGenerate(targetIndex);
        UnsafeCodeInputStore.RequireExportReady(probe.Connection, [probePair]);
        DbUnsafeCodeTrigger.SelectList(probe.Connection).ShouldBeEmpty();
        SortedDictionary<string, string[]> probeState = probe.ImportedNativeSemanticState();
        probeState.Keys.ShouldBe(mainState.Keys);
        foreach ((string table, string[] signatures) in mainState)
        {
            probeState[table].ShouldBe(signatures, "independently staged semantic correspondence: " + table);
        }
        DbStructureOutcome probeOutcome = DbStructureOutcome.SelectList(probe.Connection,
            SourceFhirPackageKey: probePair.SourcePackageKey, TargetFhirPackageKey: probePair.TargetPackageKey)
            .Single(row => row.SourceId == "ValueSet");
        StructureFhirExporter.BuiltDefinitions probeBuilt = probe.BuildDefinitions(probePair);
        RepresentationLocation native = probeBuilt.Representations.Resolve(probeOutcome.Key, path).ShouldHaveSingleItem();
        native.State.ShouldBe(RepresentationState.Observable);
        native.SourceType.ShouldBe("Coding");
        native.TargetType.ShouldBe("Coding");
        native.SourceBinding.ShouldBe(new UnsafeCodeCanonical(sourceOuter.UnversionedUrl, sourceOuter.Version));
        native.Steps.ShouldAllBe(step => step is MemberStep);
        native.Steps.Cast<MemberStep>().Select(step => step.Name).ShouldBe(["jurisdiction", "coding"]);
        DbElement probeSourceBinding = DbElement.SelectSingle(probe.Connection, FhirPackageKey: probePair.SourcePackageKey, Id: UnsafeCodeTestFixture.ImportedNativeElement)!;
        DbElement probeTargetBinding = DbElement.SelectSingle(probe.Connection, FhirPackageKey: probePair.TargetPackageKey, Id: UnsafeCodeTestFixture.ImportedNativeElement)!;
        probeSourceBinding.BindingValueSet.ShouldBe(sourceOuter.VersionedUrl);
        probeTargetBinding.BindingValueSet.ShouldBe(targetOuter.VersionedUrl);
        probeSourceBinding.BindingValueSetKey.ShouldBe(probe.ImportedValueSet(probePair.SourcePackage, "outer").Key);
        probeTargetBinding.BindingValueSetKey.ShouldBe(probe.ImportedValueSet(probePair.TargetPackage, "outer").Key);
        Console.WriteLine("MEASURED_IMPORTED_NATIVE_PROBE " + JsonSerializer.Serialize(new
        {
            destination, scenario = scenario.ToString(), correspondingTables = mainState.Select(entry => new { table = entry.Key, rows = entry.Value.Length }),
            sourceBinding = probeSourceBinding.BindingValueSet, targetBinding = probeTargetBinding.BindingValueSet,
            native.State, native.SourceType, native.TargetType, path = native.Steps.Cast<MemberStep>().Select(step => step.Name), native.Transformations,
        }));
        UnsafeCodeProvenance mapProvenance = MapProvenance(map);
        if (sourceMember)
        {
            CodeTransformation transformation = native.Transformations.ShouldHaveSingleItem();
            TransformationIdentity(transformation).ShouldBe((map.SourceSystem, map.SourceSystemVersion, map.SourceCode,
                map.TargetSystem, map.TargetSystemVersion, map.TargetCode));
            transformation.Provenance.ShouldBe([mapProvenance]);
        }
        else
        {
            native.Transformations.ShouldBeEmpty();
        }
        AssertTransformations(probeBuilt.Representations.Resolve(probeOutcome.Key, path).ShouldHaveSingleItem().Transformations, native.Transformations);
        if (scenario != UnsafeCodeTestFixture.ImportedNativeScenario.IdentityPreserving && sourceMember)
        {
            UnsafeCodeConstraintPlan plan = UnsafeCodeInputStore.ReadConstraintPlans(main.Connection, pair.SourcePackageKey, pair.TargetPackageKey)
                .Single(plan => plan.Row.Key == boundTrigger.Key);
            plan.Provenance.Replace('\\', '/').ShouldBe("input/ig-support/unsafe-code-mappings.json entry 0");
            InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => main.BuildDefinitions(pair));
            exception.Message.ShouldBe(IdentityChangingDiagnostic(plan, "R5->" + destination, [mapProvenance]));
            JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key))
                .ShouldBe(JsonSerializer.Serialize(triggers));
            Console.WriteLine("MEASURED_CONSTRAINT_DIFF " + JsonSerializer.Serialize(new
            {
                destination, scenario = scenario.ToString(),
                before = new
                {
                    baseline = "e153f6ef797a0194b8a36ca1994818a07e08140e",
                    observation = "Phase 1 measured combined identity change; not rerun against fixed production",
                    construction = "succeeded", nativeTransformations = 0, expression = ImportedNativeExpression,
                    changedTargetWithoutUri = true, configuredSourceWithoutUri = false,
                },
                after = new { construction = "rejected", diagnostic = exception.Message, native.Transformations,
                    activeTriggers = triggers.Length, generatedAffectedProfile = false },
            }));
            return;
        }
        StructureFhirExporter.BuiltDefinitions built = main.BuildDefinitions(pair);
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key))
            .ShouldBe(JsonSerializer.Serialize(triggers));
        RepresentationLocation activeNative = built.Representations.Resolve(outcome.Key, path).ShouldHaveSingleItem();
        activeNative.State.ShouldBe(native.State);
        activeNative.SourceType.ShouldBe(native.SourceType);
        activeNative.TargetType.ShouldBe(native.TargetType);
        activeNative.SourceBinding.ShouldBe(native.SourceBinding);
        activeNative.Steps.ShouldBe(native.Steps);
        AssertTransformations(activeNative.Transformations, native.Transformations);
        StructureDefinition profile = built.Profiles.Single(candidate => candidate.Definition.Type == "ValueSet").Definition;
        ElementDefinition.ConstraintComponent condition = UnsafeCodeTestFixture.SerializedConstraints(profile, targetIndex).ShouldHaveSingleItem();
        RecoveredExpressions(profile, targetIndex).ShouldBe([condition.Expression]);
        condition.Expression.ShouldBe(ImportedNativeExpression);
        condition.Key.ShouldBe(UnsafeCodeConstraintEmitter.Key(JsonSerializer.Serialize(new
        {
            Profile = profile.Url, Direction = "R5->" + destination, Owners = Array.Empty<string>(), SourcePath = path,
        })));
        condition.Severity.ShouldBe(ConstraintSeverity.Error);
        condition.Human.ShouldBe(UnsafeCodeConstraintEmitter.Human);
        condition.Source.ShouldBe(profile.Url);
        bool changedWithoutUri = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, "urn:example:target", "changed");
        bool sourceWithoutUri = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe");
        bool sourceWrongUri = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe", "http://wrong");
        bool sourceExactUri = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe", UnsafeCodeConstants.ImplicitRules);
        bool safeOnly = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "safe");
        bool absent = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, null, null);
        bool mixedWithoutUri = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe", mixed: true);
        bool mixedExactUri = UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe", UnsafeCodeConstants.ImplicitRules, mixed: true);
        Console.WriteLine("MEASURED_CONSTRAINT_DIFF " + JsonSerializer.Serialize(new
        {
            baseline = "e153f6ef797a0194b8a36ca1994818a07e08140e", destination, scenario = scenario.ToString(), delta = "unchanged control condition",
            construction = "succeeded", profile = profile.Url, condition.Key, condition.Expression,
            condition = "newly emitted unsafe-code condition only; not whole-profile validity",
            runtimeVersion = (string?)null, nativeTransformations = activeNative.Transformations,
            condition.Severity, condition.Human, condition.Source,
            changedWithoutUri, sourceWithoutUri, sourceWrongUri, sourceExactUri, safeOnly, absent, mixedWithoutUri, mixedExactUri,
        }));
        changedWithoutUri.ShouldBeTrue();
        sourceWithoutUri.ShouldBeFalse();
        sourceWrongUri.ShouldBeFalse();
        sourceExactUri.ShouldBeTrue();
        safeOnly.ShouldBeTrue();
        absent.ShouldBeTrue();
        mixedWithoutUri.ShouldBeFalse();
        mixedExactUri.ShouldBeTrue();
        profile.ImplicitRules.ShouldBeNull();
    }

    private static UnsafeCodeProvenance MapProvenance(DbUnsafeCodeMapEvidence map) =>
        new(map.SourceRelativeFilename, map.ConceptMapId, map.ConceptMapUrl, map.GroupIndex, map.SourceIndex, map.TargetIndex);

    private static (string?, string?, string?, string?, string?, string?) TransformationIdentity(CodeTransformation transformation) =>
        (transformation.SourceSystem, transformation.SourceVersion, transformation.SourceCode,
            transformation.TargetSystem, transformation.TargetVersion, transformation.TargetCode);

    private static void AssertTransformations(ImmutableArray<CodeTransformation> actual, ImmutableArray<CodeTransformation> expected)
    {
        actual.Select(TransformationIdentity).ShouldBe(expected.Select(TransformationIdentity));
        for (int i = 0; i < actual.Length; i++)
        {
            actual[i].Provenance.ShouldBe(expected[i].Provenance);
        }
    }

    private static string IdentityChangingDiagnostic(UnsafeCodeConstraintPlan plan, string direction, ImmutableArray<UnsafeCodeProvenance> maps) =>
        $"Unsafe-code projection failed in profile {plan.ProfileCanonical}, direction {direction}, source path {JsonSerializer.Serialize(plan.SourcePath)}, " +
        $"qualified code {plan.Row.System}|{plan.Row.SourceVersion ?? "*"}#{plan.Row.Code}, provenance {plan.Provenance}: identity-changing native code mapping. " +
        $"Responsible map provenance {JsonSerializer.Serialize(maps)}. " +
        "Regenerate load, compare, outcomes, and export after resolving the source-value representation.";

    [Theory]
    [InlineData(0, "TargetNonmember")]
    [InlineData(1, "TargetNonmember")]
    [InlineData(0, "SourceValueSetVersionMismatch")]
    [InlineData(1, "SourceValueSetVersionMismatch")]
    [InlineData(0, "TargetValueSetVersionMismatch")]
    [InlineData(1, "TargetValueSetVersionMismatch")]
    [InlineData(0, "SourceSystemVersionMismatch")]
    [InlineData(1, "SourceSystemVersionMismatch")]
    [InlineData(0, "TargetSystemVersionMismatch")]
    [InlineData(1, "TargetSystemVersionMismatch")]
    public void ImportedTargetScopeAndVersionMustMatchActualNativeContext(int targetIndex, string scenarioName)
    {
        UnsafeCodeTestFixture.ImportedNativeScenario scenario = Enum.Parse<UnsafeCodeTestFixture.ImportedNativeScenario>(scenarioName);
        using UnsafeCodeTestFixture main = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex, scenario, enableRule: true);
        FhirPackageComparisonPair pair = main.LoadCompareAndGenerate(targetIndex);
        using UnsafeCodeTestFixture probe = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex, scenario, enableRule: false);
        FhirPackageComparisonPair probePair = probe.LoadCompareAndGenerate(targetIndex);
        AssertStagedCorrespondence(main, probe);
        DbUnsafeCodeMapEvidence map = DbUnsafeCodeMapEvidence.SelectList(main.Connection).ShouldHaveSingleItem();
        DbValueSet sourceInner = main.ImportedValueSet(pair.SourcePackage, "inner");
        DbValueSet targetInner = main.ImportedValueSet(pair.TargetPackage, "inner");
        bool sourceVersionMatches = map.SourceValueSetVersion == sourceInner.Version;
        bool targetVersionMatches = map.TargetValueSetVersion == targetInner.Version;
        bool sourceMember = sourceInner.Compose!.Include.Any(include => include.System == map.SourceSystem &&
            include.Version == map.SourceSystemVersion && include.Concept.Any(concept => concept.Code == map.SourceCode));
        bool targetMember = targetInner.Compose!.Include.Any(include => include.System == map.TargetSystem &&
            include.Version == map.TargetSystemVersion && include.Concept.Any(concept => concept.Code == map.TargetCode));
        sourceVersionMatches.ShouldBe(scenario != UnsafeCodeTestFixture.ImportedNativeScenario.SourceValueSetVersionMismatch);
        targetVersionMatches.ShouldBe(scenario != UnsafeCodeTestFixture.ImportedNativeScenario.TargetValueSetVersionMismatch);
        sourceMember.ShouldBe(scenario != UnsafeCodeTestFixture.ImportedNativeScenario.SourceSystemVersionMismatch);
        targetMember.ShouldBe(scenario != UnsafeCodeTestFixture.ImportedNativeScenario.TargetNonmember &&
            scenario != UnsafeCodeTestFixture.ImportedNativeScenario.TargetSystemVersionMismatch);
        if (scenario == UnsafeCodeTestFixture.ImportedNativeScenario.TargetNonmember)
        {
            main.ImportedValueSet(pair.TargetPackage, "outer").Compose!.Include.ShouldContain(include =>
                include.System == map.TargetSystem && include.Concept.Any(concept => concept.Code == map.TargetCode));
        }

        DbStructureOutcome outcome = ImportedOutcome(main, pair);
        DbUnsafeCodeTrigger[] triggers = [.. DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)];
        triggers.ShouldNotBeEmpty();
        triggers.ShouldAllBe(trigger => trigger.EvidenceKeysJson == "[]" && trigger.AnnotationKeysJson == "[]" && trigger.RuleKeysJson != "[]");
        StructureFhirExporter.BuiltDefinitions probeBuilt = probe.BuildDefinitions(probePair);
        RepresentationLocation probeNative = probeBuilt.Representations.Resolve(ImportedOutcome(probe, probePair).Key, ImportedCodingPath())
            .ShouldHaveSingleItem();
        probeNative.State.ShouldBe(RepresentationState.Observable);
        probeNative.Transformations.ShouldBeEmpty();
        probeNative.SourceBinding.ShouldBe(new UnsafeCodeCanonical(main.ImportedValueSet(pair.SourcePackage, "outer").UnversionedUrl,
            main.ImportedValueSet(pair.SourcePackage, "outer").Version));
        probeBuilt.Representations.Resolve(ImportedOutcome(probe, probePair).Key, ImportedCodingPath()).ShouldHaveSingleItem()
            .Transformations.ShouldBeEmpty();
        StructureFhirExporter.BuiltDefinitions built = main.BuildDefinitions(pair);
        built.Representations.Resolve(outcome.Key, ImportedCodingPath()).ShouldHaveSingleItem().Transformations.ShouldBeEmpty();
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)).ShouldBe(JsonSerializer.Serialize(triggers));
        StructureDefinition profile = built.Profiles.Single(profile => profile.Definition.Type == "ValueSet").Definition;
        ElementDefinition.ConstraintComponent condition = UnsafeCodeTestFixture.SerializedConstraints(profile, targetIndex).ShouldHaveSingleItem();
        condition.Expression.ShouldBe(ImportedNativeExpression);
        condition.Severity.ShouldBe(ConstraintSeverity.Error);
        condition.Human.ShouldBe(UnsafeCodeConstraintEmitter.Human);
        condition.Source.ShouldBe(profile.Url);
        UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe").ShouldBeFalse();
        UnsafeCodeTestFixture.EvaluateImportedNativeCondition(condition.Expression, targetIndex, UnsafeCodeTestFixture.ImportedSourceSystem, "unsafe",
            UnsafeCodeConstants.ImplicitRules).ShouldBeTrue();
        profile.ImplicitRules.ShouldBeNull();
        Console.WriteLine("MEASURED_IMPORTED_NATIVE_ISOLATION " + JsonSerializer.Serialize(new
        {
            destination = targetIndex == 0 ? "R4" : "R4B", scenarioName, sourceVersionMatches, targetVersionMatches,
            sourceMember, targetMember, activeTriggers = triggers.Length, transformations = 0, construction = "succeeded",
            condition.Key, condition.Expression, missingUri = false, exactUri = true,
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ImportedTransformationsRemainNativeAndUseLocalBindingContexts(int targetIndex)
    {
        using UnsafeCodeTestFixture main = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex,
            UnsafeCodeTestFixture.ImportedNativeScenario.IdentityPreserving, enableRule: true, includeLocalContexts: true);
        FhirPackageComparisonPair pair = main.LoadCompareAndGenerate(targetIndex);
        using UnsafeCodeTestFixture probe = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex,
            UnsafeCodeTestFixture.ImportedNativeScenario.IdentityPreserving, enableRule: false, includeLocalContexts: true);
        FhirPackageComparisonPair probePair = probe.LoadCompareAndGenerate(targetIndex);
        AssertStagedCorrespondence(main, probe);
        DbUnsafeCodeMapEvidence map = DbUnsafeCodeMapEvidence.SelectList(main.Connection).ShouldHaveSingleItem();
        DbStructureOutcome outcome = ImportedOutcome(main, pair);
        DbUnsafeCodeTrigger[] triggers = [.. DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)];
        triggers.ShouldNotBeEmpty();
        triggers.ShouldContain(trigger => trigger.SourcePathJson.Contains("ValueSet.extensionOnly", StringComparison.Ordinal));
        StructureFhirExporter.BuiltDefinitions probeBuilt = probe.BuildDefinitions(probePair);
        StructureFhirExporter.BuiltDefinitions built = main.BuildDefinitions(pair);
        foreach (string use in new[] { "jurisdiction", "sourceUnrelated", "targetUnrelated", "extensionOnly" })
        {
            ImmutableArray<UnsafeCodePathStep> path = ImportedCodingPath("ValueSet." + use);
            RepresentationLocation location = built.Representations.Resolve(outcome.Key, path).ShouldHaveSingleItem();
            RepresentationLocation probeLocation = probeBuilt.Representations.Resolve(ImportedOutcome(probe, probePair).Key, path).ShouldHaveSingleItem();
            location.State.ShouldBe(RepresentationState.Observable);
            location.SourceType.ShouldBe("Coding");
            location.TargetType.ShouldBe("Coding");
            location.Steps.ShouldBe(probeLocation.Steps);
            location.SourceBinding.ShouldBe(probeLocation.SourceBinding);
            AssertTransformations(location.Transformations, probeLocation.Transformations);
            AssertTransformations(built.Representations.Resolve(outcome.Key, path).ShouldHaveSingleItem().Transformations, location.Transformations);
            if (use == "jurisdiction")
            {
                location.Steps.OfType<ExtensionStep>().ShouldBeEmpty();
                location.Transformations.ShouldHaveSingleItem().Provenance.ShouldBe([MapProvenance(map)]);
            }
            else
            {
                location.Transformations.ShouldBeEmpty();
                if (use == "extensionOnly")
                {
                    location.Steps.OfType<ExtensionStep>().ShouldNotBeEmpty();
                }
                else
                {
                    location.Steps.OfType<ExtensionStep>().ShouldBeEmpty();
                    DbElement sourceSite = DbElement.SelectSingle(main.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "ValueSet." + use)!;
                    DbElement targetSite = DbElement.SelectSingle(main.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "ValueSet." + use)!;
                    sourceSite.BindingValueSet.ShouldBe(main.ImportedValueSet(pair.SourcePackage, use == "sourceUnrelated" ? "unrelated" : "outer").VersionedUrl);
                    targetSite.BindingValueSet.ShouldBe(main.ImportedValueSet(pair.TargetPackage, use == "targetUnrelated" ? "unrelated" : "outer").VersionedUrl);
                }
            }
        }
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)).ShouldBe(JsonSerializer.Serialize(triggers));
        built.Profiles.ShouldAllBe(profile => profile.Definition.ImplicitRules == null);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void EquivalentTransformationsUnionDeterministicMapProvenance(int targetIndex, bool identityChanging)
    {
        UnsafeCodeTestFixture.ImportedNativeScenario scenario = identityChanging
            ? UnsafeCodeTestFixture.ImportedNativeScenario.IdentityChanging : UnsafeCodeTestFixture.ImportedNativeScenario.IdentityPreserving;
        using UnsafeCodeTestFixture main = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex, scenario, enableRule: true);
        main.WriteEquivalentImportedMaps(targetIndex);
        FhirPackageComparisonPair pair = main.LoadCompareAndGenerate(targetIndex);
        using UnsafeCodeTestFixture probe = UnsafeCodeTestFixture.CreateImportedNativeCodingInputs(targetIndex, scenario, enableRule: false);
        probe.WriteEquivalentImportedMaps(targetIndex);
        FhirPackageComparisonPair probePair = probe.LoadCompareAndGenerate(targetIndex);
        AssertStagedCorrespondence(main, probe);
        DbUnsafeCodeMapEvidence[] rows = [.. DbUnsafeCodeMapEvidence.SelectList(main.Connection)];
        rows.Length.ShouldBe(21);
        rows.ShouldContain(row => row.ConceptMapId == null);
        UnsafeCodeMapEvidence[] evidence = [.. rows.Select(row => new UnsafeCodeMapEvidence(
            row.SourceFhirPackageKey, row.TargetFhirPackageKey,
            new(row.SourceValueSetUrl, row.SourceValueSetVersion), new(row.TargetValueSetUrl, row.TargetValueSetVersion),
            new(row.SourceSystem, row.SourceSystemVersion), row.SourceCode,
            new(row.TargetSystem, row.TargetSystemVersion), row.TargetCode, row.Relationship, row.ExplicitNoMap, MapProvenance(row)))];
        DbElement sourceBinding = DbElement.SelectSingle(main.Connection, FhirPackageKey: pair.SourcePackageKey, Id: UnsafeCodeTestFixture.ImportedNativeElement)!;
        DbElement targetBinding = DbElement.SelectSingle(main.Connection, FhirPackageKey: pair.TargetPackageKey, Id: UnsafeCodeTestFixture.ImportedNativeElement)!;
        UnsafeCodeBindingScopeResolver resolver = new(DbValueSet.SelectDict(main.Connection), key => DbValueSetConcept.SelectList(main.Connection, ValueSetKey: key));
        UnsafeCodeBindingScope sourceScope = resolver.Resolve(sourceBinding)!;
        UnsafeCodeBindingScope targetScope = resolver.Resolve(targetBinding)!;
        resolver.Resolve(sourceBinding).ShouldBeSameAs(sourceScope);
        resolver.Resolve(targetBinding).ShouldBeSameAs(targetScope);
        UnsafeCodeProjectionMatch[] matches = [.. evidence.Select(row => UnsafeCodeEvidenceApplicability.MatchProjection(
            row, UnsafeCodeCanonical.Parse(sourceBinding.BindingValueSet), UnsafeCodeCanonical.Parse(targetBinding.BindingValueSet), sourceScope, targetScope))];
        matches.ShouldAllBe(match => match.IsMatch && match.SourceOrigin == UnsafeCodeEndpointMatchOrigin.ImportedScope &&
            match.TargetOrigin == UnsafeCodeEndpointMatchOrigin.ImportedScope);
        StructureFhirExporter.BuiltDefinitions probeBuilt = probe.BuildDefinitions(probePair);
        RepresentationLocation native = probeBuilt.Representations.Resolve(ImportedOutcome(probe, probePair).Key, ImportedCodingPath()).ShouldHaveSingleItem();
        native.Transformations.Length.ShouldBe(4);
        native.Transformations.Select(transformation => (transformation.SourceVersion, transformation.SourceCode, transformation.TargetVersion))
            .ShouldBe(new (string?, string?, string?)[] { (null, "safe", null), (null, "unsafe", null),
                (UnsafeCodeTestFixture.ImportedSystemVersion, "safe", UnsafeCodeTestFixture.ImportedSystemVersion),
                (UnsafeCodeTestFixture.ImportedSystemVersion, "unsafe", UnsafeCodeTestFixture.ImportedSystemVersion) });
        foreach (CodeTransformation transformation in native.Transformations)
        {
            UnsafeCodeProvenance[] expected = [.. rows.Where(row =>
                (row.SourceSystem, row.SourceSystemVersion, row.SourceCode, row.TargetSystem, row.TargetSystemVersion, row.TargetCode) == TransformationIdentity(transformation))
                .Select(MapProvenance).Distinct()
                .OrderBy(provenance => provenance.SourceRelativeFilename, StringComparer.Ordinal)
                .ThenBy(provenance => provenance.ConceptMapId, StringComparer.Ordinal)
                .ThenBy(provenance => provenance.ConceptMapUrl, StringComparer.Ordinal)
                .ThenBy(provenance => provenance.GroupIndex).ThenBy(provenance => provenance.SourceIndex).ThenBy(provenance => provenance.TargetIndex)];
            transformation.Provenance.ShouldBe(expected);
            transformation.Provenance.Length.ShouldBe(transformation.SourceVersion is null ? 2 : transformation.SourceCode == "unsafe" ? 9 : 8);
        }
        // Repeat and reverse the genuinely loaded evidence, not newly manufactured map facts.
        AssertTransformations(CodeTransformation.FromEvidence(matches.Select(match => match.Evidence).Reverse()), native.Transformations);
        AssertTransformations(CodeTransformation.FromEvidence(matches.Select(match => match.Evidence).Concat(matches.Select(match => match.Evidence)).Reverse()),
            native.Transformations);
        AssertTransformations(probeBuilt.Representations.Resolve(ImportedOutcome(probe, probePair).Key, ImportedCodingPath()).ShouldHaveSingleItem().Transformations,
            native.Transformations);
        DbStructureOutcome outcome = ImportedOutcome(main, pair);
        string triggersBefore = JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key));
        DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key).ShouldNotBeEmpty();
        if (identityChanging)
        {
            UnsafeCodeConstraintPlan plan = UnsafeCodeInputStore.ReadConstraintPlans(main.Connection, pair.SourcePackageKey, pair.TargetPackageKey)
                .Single(plan => plan.Row.SourceElementId == UnsafeCodeTestFixture.ImportedNativeElement);
            CodeTransformation offending = native.Transformations.First(transformation => transformation.SourceCode == "unsafe");
            InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => main.BuildDefinitions(pair));
            exception.Message.ShouldBe(IdentityChangingDiagnostic(plan, targetIndex == 0 ? "R5->R4" : "R5->R4B", offending.Provenance));
            Console.WriteLine("MEASURED_IMPORTED_NATIVE_PROVENANCE " + JsonSerializer.Serialize(new
                { destination = targetIndex == 0 ? "R4" : "R4B", diagnostic = exception.Message, native.Transformations }));
        }
        else
        {
            StructureFhirExporter.BuiltDefinitions built = main.BuildDefinitions(pair);
            AssertTransformations(built.Representations.Resolve(outcome.Key, ImportedCodingPath()).ShouldHaveSingleItem().Transformations, native.Transformations);
            StructureDefinition profile = built.Profiles.Single(profile => profile.Definition.Type == "ValueSet").Definition;
            UnsafeCodeTestFixture.SerializedConstraints(profile, targetIndex).ShouldHaveSingleItem().Expression.ShouldBe(ImportedNativeExpression);
            profile.ImplicitRules.ShouldBeNull();
        }
        JsonSerializer.Serialize(DbUnsafeCodeTrigger.SelectList(main.Connection, StructureOutcomeKey: outcome.Key)).ShouldBe(triggersBefore);
    }

    private static DbStructureOutcome ImportedOutcome(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair) =>
        DbStructureOutcome.SelectList(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            .Single(outcome => outcome.SourceId == "ValueSet");

    private static ImmutableArray<UnsafeCodePathStep> ImportedCodingPath(string use = UnsafeCodeTestFixture.ImportedNativeElement) =>
        [new("ValueSet", null, false), new(use, null, false), new("CodeableConcept.coding", "CodeableConcept", false)];

    private static void AssertStagedCorrespondence(UnsafeCodeTestFixture main, UnsafeCodeTestFixture probe)
    {
        DbUnsafeCodeTrigger.SelectList(probe.Connection).ShouldBeEmpty();
        SortedDictionary<string, string[]> mainState = main.ImportedNativeSemanticState();
        SortedDictionary<string, string[]> probeState = probe.ImportedNativeSemanticState();
        probeState.Keys.ShouldBe(mainState.Keys);
        foreach ((string table, string[] signatures) in mainState)
        {
            probeState[table].ShouldBe(signatures, "independently staged semantic correspondence: " + table);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PersistedPlansNotPolicyFilesDriveConstruction(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateNoFeatureInputs();
        fixture.WriteConfiguration("""[{"system":"http://hl7.org/fhir/filter-operator","code":"exists"}]""");
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        StructureDefinition FirstProfile() => Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition;
        string[] first = RecoveredExpressions(FirstProfile(), targetIndex);
        first.ShouldNotBeEmpty();
        // A fresh exporter must neither reread nor validate the edited policy file.
        fixture.WriteConfiguration("{ deliberately invalid JSON");
        RecoveredExpressions(FirstProfile(), targetIndex).ShouldBe(first);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
        first.Select(expression => Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, FilterJson("exists", null, null))))
            .All(result => result).ShouldBeFalse();
        File.Delete(fixture.ConfigurationFilename);
        RecoveredExpressions(FirstProfile(), targetIndex).ShouldBe(first);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SemanticKeysAndExpressionsIgnoreDatabaseKeysAndPlanOrdering(int targetIndex)
    {
        string? expected = null;
        int? firstOutcome = null;
        foreach (bool reordered in new[] { false, true })
        {
            using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
            FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
            if (reordered)
            {
                DbStructureDefinition noise = fixture.AddStructure(pair.SourcePackage, "Unrelated");
                fixture.AddElement(noise, "Unrelated");
                fixture.AddOutcome(pair, noise);
            }
            DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
            DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "Coding");
            DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.jurisdiction.coding", "Coding");
            fixture.Connect(pair, outcome, source, target);
            foreach (string code in reordered ? new[] { "quoted'\\code", "unsafe", "unsafe" } : new[] { "unsafe", "quoted'\\code" })
            {
                fixture.AddTrigger(outcome, SourcePath(source.Id), code: code, versions: reordered ? ["b", "a", "a"] : ["a", "b"]);
            }
            StructureDefinition profile = Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition;
            string[] expressions = RecoveredExpressions(profile, targetIndex);
            string constraintJson = JsonNode.Parse(profile.ToJson())!["differential"]!["element"]![0]!["constraint"]!.ToJsonString();
            if (reordered)
            {
                outcome.Key.ShouldNotBe(firstOutcome!.Value);
                constraintJson.ShouldBe(expected);
            }
            else
            {
                firstOutcome = outcome.Key;
                expected = constraintJson;
            }
            string json = "{\"resourceType\":\"ValueSet\",\"status\":\"active\",\"jurisdiction\":[{\"coding\":[{\"system\":" +
                JsonSerializer.Serialize(UnsafeCodeTestFixture.FilterSystem) + ",\"code\":" + JsonSerializer.Serialize("quoted'\\code") + "}]}]}";
            Evaluate(expressions.ShouldHaveSingleItem(), UnsafeCodeTestFixture.TargetInstance(targetIndex, json)).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(0, "missing")]
    [InlineData(1, "unsupported")]
    [InlineData(0, "profile")]
    public void ActivePlansNeverSilentlyDisappearWhenProjectionIsMissing(int targetIndex, string kind)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureDefinition sourceStructure = fixture.Structure(pair.SourcePackage, kind == "profile" ? "Coding" : "ValueSet");
        DbStructureDefinition targetStructure = fixture.Structure(pair.TargetPackage, kind == "profile" ? "Coding" : "ValueSet");
        DbStructureOutcome outcome = fixture.AddOutcome(pair, sourceStructure, targetStructure);
        ImmutableArray<UnsafeCodePathStep> path;
        if (kind == "profile")
        {
            path = [new("Coding", null, false), new("Coding.code", null, false)];
        }
        else
        {
            DbElement source = fixture.AddTypedElement(sourceStructure, "ValueSet.original", "code");
            DbElement target = fixture.AddTypedElement(targetStructure, "ValueSet.renamed", "uri");
            if (kind == "unsupported") fixture.Connect(pair, outcome, source, target);
            path = SourcePath(source.Id);
        }
        fixture.AddTrigger(outcome, path);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => Exporter(fixture).BuildDefinitions(Tracking(pair)));
        error.Message.ShouldContain(outcome.GenUrl!);
        error.Message.ShouldContain(path[^1].ElementId);
        error.Message.ShouldContain(UnsafeCodeTestFixture.FilterSystem);
        error.Message.ShouldContain("#unsafe");
        error.Message.ShouldContain("provenance");
        error.Message.ShouldContain(kind == "unsupported" ? "unsupported code carrier" : "Missing");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DecomposedCodingsMatchSystemVersionAndCodeWithinTheirOwnExtension(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement targetValue = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "Extension.value[x]")!;
        DbElementType.Delete(fixture.Connection, ElementKey: targetValue.Key, TypeName: "Coding");
        foreach (DbElement child in DbElement.SelectList(fixture.Connection, StructureKey: fixture.Structure(pair.SourcePackage, "Coding").Key)
            .Where(child => child.ParentElementKey is not null))
        {
            DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, child);
            childOutcome.RequiresExtensionDefinition = false;
            childOutcome.RequiresSliceDefinition = true;
            DbElementOutcome.Update(fixture.Connection, [childOutcome]);
        }
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "Coding");
        DbElementOutcome extension = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
        fixture.AddTrigger(outcome, SourcePath(source.Id), versions: ["exempt"]);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        string expression = RecoveredExpressions(built.Profiles.ShouldHaveSingleItem().Definition, targetIndex).ShouldHaveSingleItem();
        expression.ShouldContain(UnsafeCodeConstraintEmitter.Literal(ProfileRepresentationIndex.DatatypeMetadataUrl));
        foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
        {
            foreach ((string? version, bool unsafeCode) in new[] { ("exempt", false), ("other", true), ((string?)null, true) })
            {
                JsonObject Coding(string system, string code, string? codingVersion)
                {
                    JsonArray children =
                    [
                        new JsonObject { ["url"] = ProfileRepresentationIndex.DatatypeMetadataUrl, ["valueString"] = "Coding" },
                        new JsonObject { ["url"] = "system", ["valueUri"] = system },
                        new JsonObject { ["url"] = "code", ["valueCode"] = code },
                    ];
                    if (codingVersion is not null) children.Add(new JsonObject { ["url"] = "version", ["valueString"] = codingVersion });
                    return new() { ["url"] = extension.GenUrl, ["extension"] = children };
                }
                JsonObject instance = new() { ["resourceType"] = "ValueSet", ["status"] = "active", ["extension"] = new JsonArray(
                    Coding("http://different", "unsafe", "other"),
                    Coding(UnsafeCodeTestFixture.FilterSystem, "safe", "other"),
                    Coding(UnsafeCodeTestFixture.FilterSystem, "unsafe", version)) };
                if (uri is not null) instance["implicitRules"] = uri;
                Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, instance.ToJsonString()))
                    .ShouldBe(!unsafeCode || uri == UnsafeCodeConstants.ImplicitRules);
                instance["extension"]!.AsArray().RemoveAt(2);
                Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, instance.ToJsonString())).ShouldBeTrue();
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NestedChildExtensionUrlsAreExecutedFromTheSerializedConstraint(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement parent = fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.filter");
        DbElement child = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.filter.op", "code");
        DbElementOutcome parentOutcome = fixture.AddRepresentationOutcome(pair, parent, "ValueSet");
        DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, child, parent: parentOutcome);
        childOutcome.GenSliceName = "local'child\\url";
        DbElementOutcome.Update(fixture.Connection, [childOutcome]);
        fixture.AddTrigger(outcome, SourcePath(parent.Id, child.Id));
        string expression = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex).ShouldHaveSingleItem();
        expression.ShouldContain(UnsafeCodeConstraintEmitter.Literal(childOutcome.GenSliceName));
        foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
        {
            foreach (string childUrl in new[] { childOutcome.GenSliceName, "op" })
            {
                JsonObject instance = new()
                {
                    ["resourceType"] = "ValueSet", ["status"] = "active",
                    ["extension"] = new JsonArray(new JsonObject
                    {
                        ["url"] = parentOutcome.GenUrl,
                        ["extension"] = new JsonArray(new JsonObject { ["url"] = childUrl, ["valueCode"] = "unsafe" }),
                    }),
                };
                if (uri is not null) instance["implicitRules"] = uri;
                Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, instance.ToJsonString()))
                    .ShouldBe(childUrl != childOutcome.GenSliceName || uri == UnsafeCodeConstants.ImplicitRules);
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void RecursivePathsIncludeInitialAndDeepDescendants(int targetIndex, bool extension)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition structure = fixture.AddStructure(package, "Questionnaire", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Resource);
            fixture.AddElement(structure, "Questionnaire");
            if (extension && package.Key == pair.TargetPackageKey) continue;
            DbElement node = fixture.AddElement(structure, "Questionnaire.item");
            fixture.AddTypedElement(structure, "Questionnaire.item.type", "code");
            DbElement child = fixture.AddElement(structure, "Questionnaire.item.item");
            child.ContentReferenceSourceKey = node.Key;
            child.DefinedAsContentReference = true;
            DbElement.Update(fixture.Connection, [child]);
        }
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage, "Questionnaire"),
            fixture.Structure(pair.TargetPackage, "Questionnaire"));
        DbElement nodeSource = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "Questionnaire.item")!;
        DbElement childSource = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "Questionnaire.item.item")!;
        DbElement typeSource = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "Questionnaire.item.type")!;
        DbElementOutcome? parentOutcome = null;
        DbElementOutcome? typeOutcome = null;
        if (extension)
        {
            parentOutcome = fixture.AddRepresentationOutcome(pair, nodeSource, "Questionnaire");
            typeOutcome = fixture.AddRepresentationOutcome(pair, typeSource, parent: parentOutcome);
            DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, childSource, parent: parentOutcome);
            childOutcome.ContentReferenceAncestorId = nodeSource.Id;
            childOutcome.ContentReferenceExtensionUrl = parentOutcome.GenUrl;
            DbElementOutcome.Update(fixture.Connection, [childOutcome]);
        }
        ImmutableArray<UnsafeCodePathStep> initial = [new("Questionnaire", null, false), new(nodeSource.Id, null, false)];
        fixture.AddTrigger(outcome, initial.Add(new(typeSource.Id, null, false)), code: "group");
        fixture.AddTrigger(outcome, initial.Add(new(childSource.Id, null, false)).Add(new(nodeSource.Id, null, true))
            .Add(new(typeSource.Id, null, false)), code: "group");
        string[] expressions = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex);
        expressions.ShouldContain(expression => expression.Contains(".repeat("));
        foreach (int unsafeDepth in new[] { -1, 0, 1, 40 })
        {
            foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
            {
                JsonObject? next = null;
                for (int depth = 40; depth >= 0; depth--)
                {
                    if (extension)
                    {
                        JsonArray children = [new JsonObject { ["url"] = typeOutcome!.GenSliceName, ["valueCode"] = depth == unsafeDepth ? "group" : "string" }];
                        if (next is not null) children.Add(next);
                        next = new() { ["url"] = parentOutcome!.GenUrl, ["extension"] = children };
                    }
                    else
                    {
                        JsonObject item = new() { ["linkId"] = "item-" + depth, ["type"] = depth == unsafeDepth ? "group" : "string" };
                        if (next is not null) item["item"] = new JsonArray(next);
                        next = item;
                    }
                }
                JsonObject instance = new() { ["resourceType"] = "Questionnaire", ["status"] = "active", [extension ? "extension" : "item"] = new JsonArray(next) };
                if (uri is not null) instance["implicitRules"] = uri;
                expressions.Select(expression => Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, instance.ToJsonString())))
                    .All(result => result).ShouldBe(unsafeDepth < 0 || uri == UnsafeCodeConstants.ImplicitRules);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MutuallyRecursiveTypedDatatypesEvaluateInitialAndDeepValues(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.jurisdiction", "CodeableConcept");
            fixture.AddTypedElement(fixture.Structure(package, "CodeableConcept"), "CodeableConcept.extension", "Extension");
            DbElement value = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: package.Key, Id: "Extension.value[x]")!;
            value.IsChoiceType = true;
            DbElement.Update(fixture.Connection, [value]);
        }
        ImmutableArray<UnsafeCodePathStep> initial = SourcePath("ValueSet.jurisdiction");
        fixture.AddTrigger(outcome, initial.Add(new("CodeableConcept.coding", "CodeableConcept", false)));
        fixture.AddTrigger(outcome, initial.Add(new("CodeableConcept.extension", "CodeableConcept", false))
            .Add(new("Extension.value[x]", "Extension", false)).Add(new("CodeableConcept.coding", "CodeableConcept", false)));
        string[] expressions = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex);
        expressions.ShouldContain(expression => expression.Contains(".repeat("));
        foreach (int unsafeDepth in new[] { -1, 0, 1, 20 })
        {
            foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
            {
                JsonObject? next = null;
                for (int depth = 20; depth >= 0; depth--)
                {
                    JsonObject concept = new() { ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = UnsafeCodeTestFixture.FilterSystem, ["code"] = depth == unsafeDepth ? "unsafe" : "safe",
                    }) };
                    if (next is not null) concept["extension"] = new JsonArray(new JsonObject { ["url"] = "http://example.org/recursive-concept", ["valueCodeableConcept"] = next });
                    next = concept;
                }
                JsonObject instance = new() { ["resourceType"] = "ValueSet", ["status"] = "active", ["jurisdiction"] = new JsonArray(next) };
                if (uri is not null) instance["implicitRules"] = uri;
                expressions.Select(expression => Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, instance.ToJsonString())))
                    .All(result => result).ShouldBe(unsafeDepth < 0 || uri == UnsafeCodeConstants.ImplicitRules);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InheritedDatatypeTriggersStayScopedAtEveryResourceReuseSite(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition observation = fixture.AddStructure(package, "Observation", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Resource);
            fixture.AddElement(observation, "Observation");
            fixture.AddTypedElement(observation, "Observation.code", "CodeableConcept");
            fixture.AddElement(observation, "Observation.component");
            fixture.AddTypedElement(observation, "Observation.component.code", "CodeableConcept");
        }
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage, "Observation"), fixture.Structure(pair.TargetPackage, "Observation"));
        DbStructureDefinition typeProfile = fixture.AddStructure(pair.SourcePackage, "ConceptProfile", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Profile);
        DbElement profileRoot = fixture.AddElement(typeProfile, "CodeableConcept");
        profileRoot.BaseStructureKey = fixture.Structure(pair.SourcePackage, "CodeableConcept").Key;
        DbElement.Update(fixture.Connection, [profileRoot]);
        foreach (DbElementType type in DbElementType.SelectList(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, TypeName: "CodeableConcept"))
        {
            type.TypeProfile = typeProfile.VersionedUrl;
            type.TypeProfileStructureKey = typeProfile.Key;
            DbElementType.Update(fixture.Connection, [type]);
        }
        foreach (string[] use in new[] { new[] { "Observation.code" }, new[] { "Observation.component", "Observation.component.code" } })
        {
            fixture.AddTrigger(outcome, [new("Observation", null, false),
                .. use.Select(id => new UnsafeCodePathStep(id, null, false)), new("CodeableConcept.coding", typeProfile.VersionedUrl, false)]);
        }
        string[] expressions = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex);
        expressions.Length.ShouldBe(2);
        foreach (int unsafeSite in new[] { -1, 0, 1 })
        {
            foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
            {
                JsonObject Concept(int site) => new() { ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = UnsafeCodeTestFixture.FilterSystem, ["code"] = site == unsafeSite ? "unsafe" : "safe",
                }) };
                JsonObject instance = new() { ["resourceType"] = "Observation", ["status"] = "final", ["code"] = Concept(0),
                    ["component"] = new JsonArray(new JsonObject { ["code"] = Concept(1) }) };
                if (uri is not null) instance["implicitRules"] = uri;
                expressions.Select(expression => Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, instance.ToJsonString())))
                    .All(result => result).ShouldBe(unsafeSite < 0 || uri == UnsafeCodeConstants.ImplicitRules);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NestedResourceUsesItsOwnImplicitRules(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition parameters = fixture.AddStructure(package, "Parameters", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Resource);
            fixture.AddElement(parameters, "Parameters");
            fixture.AddElement(parameters, "Parameters.parameter");
            fixture.AddTypedElement(parameters, "Parameters.parameter.resource", "Parameters");
            fixture.AddTypedElement(parameters, "Parameters.parameter.value[x]", "code");
            DbElement value = DbElement.SelectSingle(fixture.Connection, StructureKey: parameters.Key, Id: "Parameters.parameter.value[x]")!;
            value.IsChoiceType = true;
            DbElement.Update(fixture.Connection, [value]);
        }
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage, "Parameters"), fixture.Structure(pair.TargetPackage, "Parameters"));
        fixture.AddTrigger(outcome, [new("Parameters", null, false), new("Parameters.parameter", null, false), new("Parameters.parameter.value[x]", null, false)]);
        string[] expressions = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex);
        expressions.ShouldContain(expression => expression.Contains(".all("));
        foreach (int unsafeOwner in new[] { -1, 0, 1, 2 })
        {
            for (int declarations = 0; declarations < 27; declarations++)
            {
                JsonObject? next = null;
                for (int owner = 2; owner >= 0; owner--)
                {
                    JsonArray parameters = [new JsonObject { ["name"] = "code", ["valueCode"] = owner == unsafeOwner ? "unsafe" : "safe" }];
                    if (next is not null) parameters.Add(new JsonObject { ["name"] = "child", ["resource"] = next });
                    JsonObject resource = new() { ["resourceType"] = "Parameters", ["parameter"] = parameters };
                    int declaration = declarations / new[] { 1, 3, 9 }[owner] % 3;
                    if (declaration != 0) resource["implicitRules"] = declaration == 1 ? "http://wrong" : UnsafeCodeConstants.ImplicitRules;
                    next = resource;
                }
                ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex, next!.ToJsonString());
                expressions.Select(expression => Evaluate(expression, instance)).All(result => result)
                    .ShouldBe(unsafeOwner < 0 || declarations / new[] { 1, 3, 9 }[unsafeOwner] % 3 == 2);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AlternatingResourceOwnersReachEveryTypeAfterMultipleCycles(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome[] outcomes = fixture.AddResourceOwnerGraph(pair, "Bundle", "Parameters");
        fixture.AddTrigger(outcomes[1], [new("Parameters", null, false), new("Parameters.parameter", null, false),
            new("Parameters.parameter.value[x]", null, false)]);
        fixture.AddTrigger(outcomes[0], [new("Bundle", null, false), new("Bundle.type", null, false)], code: "document");
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        Dictionary<string, string[]> expressions = built.Profiles.ToDictionary(profile => profile.Definition.Type,
            profile => RecoveredExpressions(profile.Definition, targetIndex));
        expressions.Values.ShouldAllBe(value => value.Length > 0);

        foreach (string unsafeType in new[] { "Parameters", "Bundle" })
        {
            foreach (string rootType in new[] { "Bundle", "Parameters" })
            {
                foreach (int cycles in new[] { 0, 1, 5, 20 })
                {
                    foreach (string? ownerUri in new[] { null, "http://wrong", UnsafeCodeConstants.ImplicitRules })
                    {
                        foreach (string? parentUri in new[] { null, "http://wrong", UnsafeCodeConstants.ImplicitRules })
                        {
                            JsonObject next = Resource(unsafeType, true, ownerUri);
                            int wrappers = cycles * 2 + (rootType == unsafeType ? 0 : 1);
                            for (int depth = 0; depth < wrappers; depth++)
                            {
                                string parentType = next["resourceType"]!.GetValue<string>() == "Parameters" ? "Bundle" : "Parameters";
                                next = Resource(parentType, false, parentUri, next);
                            }
                            ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex, next.ToJsonString());
                            instance.InstanceType.ShouldBe(rootType);
                            expressions[rootType].Select(expression => Evaluate(expression, instance)).All(result => result)
                                .ShouldBe(ownerUri == UnsafeCodeConstants.ImplicitRules,
                                    $"root {rootType}, unsafe {unsafeType}, cycles {cycles}, child URI {ownerUri}, parent URI {parentUri}");
                        }
                    }
                }
            }
        }

        // A real resource below an unindexed location is not a transition.
        JsonObject unrelated = new()
        {
            ["resourceType"] = "Parameters",
            ["parameter"] = new JsonArray(new JsonObject
            {
                ["name"] = "unrelated",
                ["part"] = new JsonArray(new JsonObject { ["name"] = "child", ["resource"] = Resource("Parameters", true, null) }),
            }),
        };
        ITypedElement unrelatedInstance = UnsafeCodeTestFixture.TargetInstance(targetIndex, unrelated.ToJsonString());
        expressions["Parameters"].ShouldAllBe(expression => Evaluate(expression, unrelatedInstance));

        static JsonObject Resource(string type, bool unsafeCode, string? uri, JsonObject? child = null)
        {
            JsonObject resource = new() { ["resourceType"] = type };
            if (type == "Bundle")
            {
                resource["type"] = unsafeCode ? "document" : "collection";
                if (child is not null) resource["entry"] = new JsonArray(new JsonObject { ["resource"] = child });
            }
            else
            {
                JsonArray parameters = [new JsonObject
                {
                    ["name"] = "code",
                    ["valueCoding"] = new JsonObject { ["system"] = UnsafeCodeTestFixture.FilterSystem, ["code"] = unsafeCode ? "unsafe" : "safe" },
                }];
                if (child is not null) parameters.Add(new JsonObject { ["name"] = "child", ["resource"] = child });
                resource["parameter"] = parameters;
            }
            if (uri is not null) resource["implicitRules"] = uri;
            return resource;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GenericContainedOwnerClosureGrowsWithGraphNotTrails(int targetIndex)
    {
        List<int> lengths = [];
        foreach (int size in new[] { 2, 3, 4, 5 })
        {
            using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
            FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
            string[] resourceTypes = new[] { "Observation", "Patient", "Organization", "Practitioner", "Device" }[..size];
            DbStructureOutcome[] outcomes = fixture.AddResourceOwnerGraph(pair, resourceTypes);
            fixture.AddTrigger(outcomes[0], [new("Observation", null, false), new("Observation.code", null, false)]);
            StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
            string[][] expressions = [.. built.Profiles.Select(profile => RecoveredExpressions(profile.Definition, targetIndex))];
            int count = expressions.Sum(value => value.Length);
            int length = expressions.SelectMany(value => value).Sum(expression => expression.Length);
            Console.WriteLine($"OWNER_GRAPH target={targetIndex} profiles={size} constraints={count} expressionChars={length}");
            // One source occurrence, shared across every reachable owner, not one
            // invariant (or transition copy) for every edge-simple trail.
            count.ShouldBe(size);
            expressions.ShouldAllBe(value => value.Length == 1);
            expressions.SelectMany(value => value).ShouldAllBe(expression =>
                expression.Split(".repeat(", StringSplitOptions.None).Length - 1 == 1);
            expressions.SelectMany(value => value).ShouldAllBe(expression =>
                expression.Split(".contained.", StringSplitOptions.None).Length - 1 == size * size);
            lengths.Add(length);

            foreach (StructureFhirExporter.BuiltDefinition profile in built.Profiles)
            {
                string expression = RecoveredExpressions(profile.Definition, targetIndex).ShouldHaveSingleItem();
                foreach (string? uri in new[] { null, "http://wrong", UnsafeCodeConstants.ImplicitRules })
                {
                    JsonObject next = new()
                    {
                        ["resourceType"] = "Observation", ["status"] = "final",
                        ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject
                        {
                            ["system"] = UnsafeCodeTestFixture.FilterSystem, ["code"] = "unsafe",
                        }) },
                    };
                    if (uri is not null) next["implicitRules"] = uri;
                    JsonObject sibling = (JsonObject)next.DeepClone();
                    sibling["implicitRules"] = UnsafeCodeConstants.ImplicitRules;
                    JsonObject root = Container(profile.Definition.Type, next);
                    root["contained"]!.AsArray().Add(sibling);
                    Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, root.ToJsonString()))
                        .ShouldBe(uri == UnsafeCodeConstants.ImplicitRules);
                }
            }

            if (size == 3)
            {
                DbElement duplicate = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage, "Observation"), "Observation.otherContained", "Resource");
                fixture.Connect(pair, outcomes[0], duplicate,
                    DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "Observation.contained")!);
                StructureFhirExporter.BuiltDefinitions duplicated = Exporter(fixture).BuildDefinitions(Tracking(pair));
                foreach (StructureFhirExporter.BuiltDefinition profile in duplicated.Profiles)
                {
                    StructureDefinition before = built.Profiles.Single(candidate => candidate.OutcomeKey == profile.OutcomeKey).Definition;
                    RecoveredExpressions(profile.Definition, targetIndex).ShouldBe(RecoveredExpressions(before, targetIndex));
                    profile.Definition.Differential.Element[0].Constraint.Select(constraint => constraint.Key)
                        .ShouldBe(before.Differential.Element[0].Constraint.Select(constraint => constraint.Key));
                }

                using UnsafeCodeTestFixture reorderedFixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
                FhirPackageComparisonPair reorderedPair = reorderedFixture.PreparePair(2, targetIndex);
                DbStructureDefinition noise = reorderedFixture.AddStructure(reorderedPair.SourcePackage, "Unrelated");
                reorderedFixture.AddElement(noise, "Unrelated");
                reorderedFixture.AddOutcome(reorderedPair, noise);
                DbStructureOutcome[] reorderedOutcomes = reorderedFixture.AddResourceOwnerGraph(reorderedPair, [.. resourceTypes.Reverse()]);
                DbStructureOutcome observation = reorderedOutcomes.Single(outcome => outcome.SourceId == "Observation");
                observation.Key.ShouldNotBe(outcomes[0].Key);
                reorderedFixture.AddTrigger(observation, [new("Observation", null, false), new("Observation.code", null, false)]);
                StructureFhirExporter.BuiltDefinitions reordered = Exporter(reorderedFixture).BuildDefinitions(Tracking(reorderedPair));
                foreach (StructureFhirExporter.BuiltDefinition profile in reordered.Profiles)
                {
                    StructureDefinition before = built.Profiles.Single(candidate => candidate.Definition.Url == profile.Definition.Url).Definition;
                    JsonNode.Parse(profile.Definition.ToJson())!["differential"]!["element"]![0]!["constraint"]!.ToJsonString()
                        .ShouldBe(JsonNode.Parse(before.ToJson())!["differential"]!["element"]![0]!["constraint"]!.ToJsonString());
                }
            }
        }
        lengths[3].ShouldBeLessThan(lengths[0] * 16);

        static JsonObject Container(string type, JsonObject child)
        {
            JsonObject resource = new() { ["resourceType"] = type, ["contained"] = new JsonArray(child),
                ["implicitRules"] = UnsafeCodeConstants.ImplicitRules };
            if (type == "Observation")
            {
                resource["status"] = "final";
                resource["code"] = new JsonObject { ["text"] = "Safe observation" };
            }
            return resource;
        }
    }

    [Fact]
    public void NativeAndExtensionOccurrencesEnforceExactImplicitRules()
    {
        // Preserve the original characterization's R4-then-R4B key allocation.
        foreach (int targetIndex in new[] { 0, 1 })
        {
            AssertNativeAndExtensionOccurrences(targetIndex);
        }
    }

    private static void AssertNativeAndExtensionOccurrences(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs(initializeIndices: targetIndex == 0);
        fixture.WriteConfiguration("""[{"system":"http://hl7.org/fhir/filter-operator","code":"exists"}]""");
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        StructureDefinition profile = built.Profiles.ShouldHaveSingleItem().Definition;
        string[] expressions = RecoveredExpressions(profile, targetIndex);
        expressions.ShouldNotBeEmpty();
        string extensionUrl = built.Extensions.Single(extension => extension.Definition.Id == "ext-R5-ValueSet.com.inc.fil.op").Definition.Url;
        string.Join("\n", expressions).ShouldContain(".extension.where(url = " + UnsafeCodeConstraintEmitter.Literal(extensionUrl) + ")");
        foreach (string? uri in new[] { null, "http://wrong", UnsafeCodeConstants.ImplicitRules })
        {
            foreach ((string code, string placement) in new[]
            {
                ("unsafe-demo", "extension"), ("unresolved", "extension"), ("=", "extension"), ("exists", "extension"),
                ("=", "op"), ("exists", "op"),
                ("unsafe-demo", "value"), ("unresolved", "value"), ("exists", "value"), ("=", "value"),
                ("not-present", "absent"),
            })
            {
                ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex, code == "not-present"
                    ? "{\"resourceType\":\"ValueSet\",\"status\":\"active\"" + (uri is null ? "" : ",\"implicitRules\":" + JsonSerializer.Serialize(uri)) + "}"
                    : FilterJson(code, uri, placement == "extension" ? extensionUrl : null, extensionOnFilter: true, valueOnly: placement == "value"));
                bool unsafeCode = code is "unsafe-demo" or "unresolved" or "exists";
                expressions.Select(expression => Evaluate(expression, instance)).All(result => result)
                    .ShouldBe(!unsafeCode || uri == UnsafeCodeConstants.ImplicitRules);
            }
        }

        profile.ImplicitRules.ShouldBeNull();
        AssertOnlyConstraintDelta(built, targetIndex);
        // Measured output is visible in the detailed test run, not an illustrative golden.
        Console.WriteLine("MEASURED_CONSTRAINT_DIFF " + JsonSerializer.Serialize(new
        {
            target = pair.TargetPackage.PackageVersion,
            canonical = profile.Url,
            expressionChars = expressions.Sum(expression => expression.Length),
            implications = expressions.Sum(expression => expression.Split(" implies ", StringSplitOptions.None).Length - 1),
            append = JsonNode.Parse(profile.ToJson())!["differential"]!["element"]![0]!["constraint"],
        }));
    }

    private static void AssertOnlyConstraintDelta(StructureFhirExporter.BuiltDefinitions built, int targetIndex)
    {
        JsonArray expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "XverUnsafeCodes", "Expected",
            targetIndex == 0 ? "no-feature-r5-r4.json" : "no-feature-r5-r4b.json")))!.AsArray();
        foreach (StructureFhirExporter.BuiltDefinition definition in built.Extensions.Concat(built.Profiles))
        {
            JsonObject actual = JsonNode.Parse(definition.Definition.ToJson())!.AsObject();
            actual["date"] = "2000-01-01T00:00:00+00:00";
            JsonObject root = actual["differential"]!["element"]![0]!.AsObject();
            if (root["constraint"] is JsonArray constraints)
            {
                foreach (JsonNode? constraint in constraints.Where(constraint => constraint!["key"]!.GetValue<string>()
                    .StartsWith("xv-unsafe-code-", StringComparison.Ordinal)).ToArray())
                {
                    constraints.Remove(constraint);
                }
                if (constraints.Count == 0) root.Remove("constraint");
            }
            actual.ToJsonString().ShouldBe(expected.Single(json => json!["id"]!.GetValue<string>() == definition.Definition.Id)!.ToJsonString());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PrimitiveExtensionWithoutScalarStillTriggers(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.extra", "code");
        DbStructureDefinition target = fixture.Structure(pair.TargetPackage);
        fixture.AddElement(target, "ValueSet.compose");
        fixture.AddElement(target, "ValueSet.compose.include");
        fixture.AddElement(target, "ValueSet.compose.include.filter");
        fixture.AddTypedElement(target, UnsafeCodeTestFixture.FilterElement, "code");
        DbElementOutcome extension = fixture.AddRepresentationOutcome(pair, source, UnsafeCodeTestFixture.FilterElement);
        fixture.AddTrigger(outcome, SourcePath(source.Id));
        string expression = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex).ShouldHaveSingleItem();
        foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
        {
            ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex, FilterJson("unsafe", uri, extension.GenUrl));
            instance.Children("compose").Single().Children("include").Single().Children("filter").Single().Children("op").Single().Value.ShouldBeNull();
            Evaluate(expression, instance).ShouldBe(uri == UnsafeCodeConstants.ImplicitRules);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void CodePredicatesKeepSystemVersionAndCodeOnOneOccurrence(int targetIndex, bool extension)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "CodeableConcept");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.useContext.value[x]", "CodeableConcept");
        target.IsChoiceType = true;
        DbElement.Update(fixture.Connection, [target]);
        DbElementOutcome? ext = extension ? fixture.AddRepresentationOutcome(pair, source, "ValueSet") : null;
        fixture.Connect(pair, outcome, source, target, ext);
        fixture.AddTrigger(outcome, SourcePath(source.Id).Add(new("CodeableConcept.coding", "CodeableConcept", false)),
            wildcard: true, versions: ["exempt"]);
        string expression = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex).ShouldHaveSingleItem();
        foreach (string? uri in new[] { null, "wrong", UnsafeCodeConstants.ImplicitRules })
        {
            foreach ((string codings, bool unsafeCode) in new[]
            {
                ("[]", false),
                ("[{\"system\":\"http://different\",\"code\":\"unsafe\"},{\"system\":\"" + UnsafeCodeTestFixture.FilterSystem + "\",\"code\":\"safe\"}]", false),
                ("[{\"system\":\"" + UnsafeCodeTestFixture.FilterSystem + "\",\"version\":\"exempt\",\"code\":\"unsafe\"}]", false),
                ("[{\"system\":\"" + UnsafeCodeTestFixture.FilterSystem + "\",\"code\":\"unsafe\"}]", true),
                ("[{\"system\":\"" + UnsafeCodeTestFixture.FilterSystem + "\",\"version\":\"other\",\"code\":\"unsafe\"}]", true),
                ("[{\"system\":\"" + UnsafeCodeTestFixture.FilterSystem + "\",\"code\":\"safe\"},{\"system\":\"" + UnsafeCodeTestFixture.FilterSystem + "\",\"code\":\"unsafe\"}]", true),
            })
            {
                string concept = codings == "[]" ? "{\"text\":\"No coding\"}" : "{\"coding\":" + codings + "}";
                string content = extension
                    ? "\"extension\":[{\"url\":" + JsonSerializer.Serialize(ext!.GenUrl) + ",\"valueCodeableConcept\":" + concept + "}]"
                    : "\"useContext\":[{\"code\":{\"system\":\"context\",\"code\":\"context\"},\"valueCodeableConcept\":" + concept + "}]";
                Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex, "{\"resourceType\":\"ValueSet\",\"status\":\"active\"," + UriJson(uri) + content + "}"))
                    .ShouldBe(!unsafeCode || uri == UnsafeCodeConstants.ImplicitRules);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExactVersionsDoNotMatchUnknownVersionsOrCrossMatchCodings(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "Coding");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.jurisdiction.coding", "Coding");
        fixture.Connect(pair, outcome, source, target);
        fixture.AddTrigger(outcome, SourcePath(source.Id), wildcard: false, versions: ["exact"]);
        string expression = RecoveredExpressions(Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition, targetIndex).ShouldHaveSingleItem();
        foreach (string? version in new[] { null, "other", "exact" })
        {
            string coding = "{\"system\":" + JsonSerializer.Serialize(UnsafeCodeTestFixture.FilterSystem) +
                ",\"code\":\"unsafe\"" + (version is null ? "" : ",\"version\":" + JsonSerializer.Serialize(version)) + "}";
            string other = "{\"system\":\"different\",\"code\":\"unsafe\",\"version\":\"exact\"}";
            Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex,
                "{\"resourceType\":\"ValueSet\",\"status\":\"active\",\"jurisdiction\":[{\"coding\":[" + other + "," + coding + "]}]}"))
                .ShouldBe(version != "exact");
        }
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    public void NativeModifierAndOverflowRepresentationsAreUnioned(int targetIndex, bool modifier, bool overflow)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.op", "code");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.status", "code");
        DbElementOutcome ext = fixture.AddRepresentationOutcome(pair, source, "ValueSet", cardinalityOnly: overflow);
        ext.DefineAsModifier = modifier;
        DbElementOutcome.Update(fixture.Connection, [ext]);
        fixture.Connect(pair, outcome, source, target, ext);
        fixture.AddTrigger(outcome, SourcePath(source.Id), code: "draft");
        StructureDefinition profile = Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition;
        string expression = RecoveredExpressions(profile, targetIndex).ShouldHaveSingleItem();
        foreach (bool native in new[] { false, true })
        {
            foreach (bool unsafeCode in new[] { false, true })
            {
                string content = native ? "" : ",\"" + (modifier ? "modifierExtension" : "extension") + "\":[{\"url\":" +
                    JsonSerializer.Serialize(ext.GenUrl) + ",\"valueCode\":" + JsonSerializer.Serialize(unsafeCode ? "draft" : "safe") + "}]";
                Evaluate(expression, UnsafeCodeTestFixture.TargetInstance(targetIndex,
                    "{\"resourceType\":\"ValueSet\",\"status\":" + JsonSerializer.Serialize(native && unsafeCode ? "draft" : "active") + content + "}")).ShouldBe(!unsafeCode);
            }
        }

        if (overflow)
        {
            profile.Differential.Element[0].Constraint[0].Severity.ShouldBe(ConstraintSeverity.Warning);
        }
    }

    [Theory]
    [InlineData(0, "Quantity")]
    [InlineData(1, "code")]
    [InlineData(0, "opaque")]
    [InlineData(1, "changed")]
    public void IdentityErasureAndOpaqueSubstitutionsFailExplicitly(int targetIndex, string kind)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", kind == "opaque" ? "code" : "Coding");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.renamed", kind is "Quantity" or "code" ? kind : "Coding");
        fixture.Connect(pair, outcome, source, target);
        if (kind == "opaque")
        {
            DbElementOutcome ext = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
            ext.ExtensionSubstitutionUrl = "http://example.org/opaque";
            DbElementOutcome.Update(fixture.Connection, [ext]);
        }
        else if (kind == "changed")
        {
            source.BindingValueSet = target.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet;
            DbElement.Update(fixture.Connection, [source, target]);
            DbUnsafeCodeMapEvidence evidence = fixture.AddEvidence(pair, noMap: false);
            evidence.TargetCode = "changed";
            evidence.TargetSystem = UnsafeCodeTestFixture.FilterSystem;
            DbUnsafeCodeMapEvidence.Update(fixture.Connection, [evidence]);
        }
        fixture.AddTrigger(outcome, SourcePath(source.Id));
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => Exporter(fixture).BuildDefinitions(Tracking(pair)));
        error.Message.ShouldContain(outcome.GenUrl!);
        error.Message.ShouldContain("R5->" + pair.TargetFhirSequence.ToRLiteral());
        error.Message.ShouldContain(source.Id);
        error.Message.ShouldContain(UnsafeCodeTestFixture.FilterSystem);
        error.Message.ShouldContain("#unsafe");
        error.Message.ShouldContain("provenance");
        error.Message.ShouldContain(kind switch
        {
            "Quantity" => "ErasedVersion",
            "code" => "ErasedIdentity",
            "opaque" => "OpaqueSubstitution",
            _ => "identity-changing native code mapping",
        });
    }

    [Fact]
    public void KeysEscapingAndConstraintOrderingAreStable()
    {
        StructureDefinition profile = new() { Url = "http://example.org/profile", Differential = new() { Element = [new() { Path = "ValueSet", ElementId = "ValueSet", Constraint = [new() { Key = "existing", Severity = ConstraintSeverity.Warning, Human = "unchanged", Expression = "true" }] }] } };
        UnsafeCodeConstraintEmitter.Append(profile, [("b", "true"), ("a", "false"), ("a", "false")]);
        profile.Differential.Element[0].Constraint[0].Expression.ShouldBe("true");
        profile.Differential.Element[0].Constraint[0].Human.ShouldBe("unchanged");
        profile.Differential.Element[0].Constraint[0].Severity.ShouldBe(ConstraintSeverity.Warning);
        profile.Differential.Element[0].Constraint.Select(constraint => constraint.Key).ShouldBe(["existing", UnsafeCodeConstraintEmitter.Key("a"), UnsafeCodeConstraintEmitter.Key("b")]);
        profile.Differential.Element[0].Constraint.ShouldAllBe(constraint => constraint.Key.Length <= 64);
        Should.Throw<InvalidOperationException>(() => UnsafeCodeConstraintEmitter.Append(profile, [("a", "true")])).Message.ShouldContain("collision");
        string special = "quote'\\\n\r\t\u0001";
        string literal = UnsafeCodeConstraintEmitter.Literal(special);
        Evaluate(literal + " = " + literal, UnsafeCodeTestFixture.TargetInstance(0, "{\"resourceType\":\"ValueSet\",\"status\":\"active\"}")).ShouldBeTrue();
        string identifier = UnsafeCodeConstraintEmitter.Identifier("not-a`member\\\u0002");
        Compiler.Compile("$this." + identifier + ".exists()").ShouldNotBeNull();
        UnsafeCodeConstraintEmitter.Identifier("where").ShouldBe("where");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SerializationPreservesExecutableConstraintAndR3Fields(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        StructureDefinition profile = Exporter(fixture).BuildDefinitions(Tracking(pair)).Profiles.ShouldHaveSingleItem().Definition;
        RecoveredExpressions(profile, targetIndex).ShouldNotBeEmpty();
        string json = new CrossVersionExporter.StructureDefinitionToR3().ToJson(profile);
        stu3::Hl7.Fhir.Model.StructureDefinition recovered = new stu3::Hl7.Fhir.Serialization.FhirJsonParser(
            new ParserSettings { PermissiveParsing = true }).Parse<stu3::Hl7.Fhir.Model.StructureDefinition>(json);
        ElementDefinition.ConstraintComponent[] before = [.. profile.Differential.Element[0].Constraint];
        recovered.Differential.Element[0].Constraint.Count.ShouldBe(before.Length);
        for (int i = 0; i < before.Length; i++)
        {
            recovered.Differential.Element[0].Constraint[i].Key.ShouldBe(before[i].Key);
            recovered.Differential.Element[0].Constraint[i].Severity.ToString().ShouldBe(before[i].Severity.ToString());
            recovered.Differential.Element[0].Constraint[i].Human.ShouldBe(before[i].Human);
            recovered.Differential.Element[0].Constraint[i].Expression.ShouldBe(before[i].Expression);
            recovered.Differential.Element[0].Constraint[i].Source.ShouldBe(before[i].Source);
        }
    }

    private static string[] RecoveredExpressions(StructureDefinition profile, int targetIndex)
    {
        string json = profile.ToJson();
        // The characterized exporter preserves legacy empty comments and warning
        // constraints without human text. Use the supported tolerant parser for
        // that metadata; target instances still use the strict POCO parser.
        ParserSettings settings = new() { PermissiveParsing = true };
        StructureDefinition recovered = targetIndex == 0
            ? new r4::Hl7.Fhir.Serialization.FhirJsonParser(settings).Parse<StructureDefinition>(json)
            : new r4b::Hl7.Fhir.Serialization.FhirJsonParser(settings).Parse<StructureDefinition>(json);
        // Conformance POCOs are shared; the release-specific inspector is
        // still required when deserializing profiles and their target instances.
        ElementDefinition.ConstraintComponent[] before = [.. profile.Differential.Element[0].Constraint
            .Where(constraint => constraint.Key.StartsWith("xv-unsafe-code-", StringComparison.Ordinal))];
        ElementDefinition.ConstraintComponent[] after = [.. recovered.Differential.Element[0].Constraint
            .Where(constraint => constraint.Key.StartsWith("xv-unsafe-code-", StringComparison.Ordinal))];
        after.Length.ShouldBe(before.Length);
        for (int i = 0; i < before.Length; i++)
        {
            after[i].Key.ShouldBe(before[i].Key);
            after[i].Severity.ShouldBe(ConstraintSeverity.Error);
            after[i].Human.ShouldBe(UnsafeCodeConstraintEmitter.Human);
            after[i].Expression.ShouldBe(before[i].Expression);
            after[i].Source.ShouldBe(profile.Url);
        }
        recovered.ImplicitRules.ShouldBeNull();
        return recovered.ToTypedElement(targetIndex == 0 ? r4::Hl7.Fhir.Model.ModelInfo.ModelInspector : r4b::Hl7.Fhir.Model.ModelInfo.ModelInspector)
            .Children("differential").Single().Children("element").SelectMany(element => element.Children("constraint"))
            .Where(constraint => ((string)constraint.Children("key").Single().Value!).StartsWith("xv-unsafe-code-"))
            .Select(constraint => (string)constraint.Children("expression").Single().Value!).ToArray();
    }

    private static bool Evaluate(string expression, ITypedElement instance)
    {
        CompiledExpression compiled = Compiler.Compile(expression);
        ITypedElement result = compiled(instance, new EvaluationContext()).ShouldHaveSingleItem();
        return result.Value.ShouldBeOfType<bool>();
    }

    private static string UriJson(string? uri) => uri is null ? "" : "\"implicitRules\":" + JsonSerializer.Serialize(uri) + ",";

    private static string FilterJson(string code, string? uri, string? extension, bool extensionOnFilter = false, bool valueOnly = false) =>
        "{\"resourceType\":\"ValueSet\",\"status\":\"active\"," + UriJson(uri) +
        "\"compose\":{\"include\":[{\"filter\":[{\"property\":\"status\",\"value\":" + JsonSerializer.Serialize(valueOnly ? code : "active") + "," +
        (extension is null ? "\"op\":" + JsonSerializer.Serialize(valueOnly ? "=" : code) : extensionOnFilter
            ? "\"op\":\"=\",\"extension\":[{\"url\":" + JsonSerializer.Serialize(extension) + ",\"valueCode\":" + JsonSerializer.Serialize(code) + "}]"
            : "\"_op\":{\"extension\":[{\"url\":" + JsonSerializer.Serialize(extension) + ",\"valueCode\":" + JsonSerializer.Serialize(code) + "}]}") + "}]}]}}";

    [Fact]
    public void NoFeatureDefinitionsMatchCharacterizedOutput()
    {
        // Match the clean-start R4, then R4B order used by the original capture.
        foreach (int targetIndex in new[] { 0, 1 })
        {
            AssertCharacterizedOutput(targetIndex);
        }
    }

    private static void AssertCharacterizedOutput(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateNoFeatureInputs(initializeIndices: targetIndex == 0);
        FhirPackageComparisonPair pair = fixture.LoadCompareAndGenerate(targetIndex);
        AssertCompletedEmpty(fixture, pair);
        IgExporter.XVerIgExportTrackingRecord tracking = Tracking(pair);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(tracking);
        JsonArray definitions = [];
        // The original capture enumerated the NTFS resource directory by filename.
        foreach (StructureFhirExporter.BuiltDefinition definition in built.Extensions.Concat(built.Profiles).OrderBy(definition => definition.FileName, StringComparer.OrdinalIgnoreCase))
        {
            JsonNode json = JsonNode.Parse(definition.Definition.ToJson())!;
            json["date"] = "2000-01-01T00:00:00+00:00";
            definitions.Add(json);
        }

        string filename = $"no-feature-r5-{pair.TargetFhirSequence.ToRLiteral().ToLowerInvariant()}.json";
        JsonArray expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "XverUnsafeCodes", "Expected", filename)))!.AsArray();
        definitions.Count.ShouldBe(expected.Count);
        for (int i = 0; i < definitions.Count; i++)
        {
            definitions[i]!.ToJsonString().ShouldBe(expected[i]!.ToJsonString());
        }
        built.Extensions.Select(extension => extension.Definition.Id).ShouldBe(
            ["ext-R5-ValueSet.com.inc.fil.op", "ext-R5-ValueSet.com.inc.fil.value", "ext-R5-Extension.value"]);
        tracking.ExtensionFiles.ShouldBeEmpty();
        tracking.ProfileFiles.ShouldBeEmpty();
        tracking.EdOutcomeMapTargets.ShouldNotBeEmpty();
        built.Profiles.SelectMany(profile => profile.Definition.Differential.Element)
            .SelectMany(element => element.Constraint).ShouldNotContain(constraint => constraint.Key.StartsWith("xv-unsafe-code-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void NativeLocationsSurviveEarlyContinuesRenamesAndChoices(int targetIndex, bool choice)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "code");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), choice ? "ValueSet.renamed[x]" : "ValueSet.renamed", "code");
        target.IsChoiceType = choice;
        DbElement.Update(fixture.Connection, [target]);
        fixture.Connect(pair, outcome, source, target);

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        built.Profiles.ShouldHaveSingleItem().Definition.Differential.Element.ShouldHaveSingleItem().Path.ShouldBe("ValueSet");
        ImmutableArray<RepresentationLocation> locations = built.Representations.Resolve(outcome.Key, SourcePath(source.Id));
        locations.ShouldHaveSingleItem().State.ShouldBe(RepresentationState.Observable);
        locations[0].Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe(["renamed"]);
        locations[0].Steps.OfType<ChoiceStep>().Select(step => step.TypeName).ShouldBe(choice ? ["code"] : []);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    public void AttachmentsShareModifierNativeOverflowAndCardinalityDecisions(int targetIndex, bool modifier, bool cardinalityOnly)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.op", "code");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.renamed", "code");
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, "ValueSet", cardinalityOnly: cardinalityOnly);
        element.DefineAsModifier = modifier;
        DbElementOutcome.Update(fixture.Connection, [element]);
        fixture.Connect(pair, outcome, source, target, element);

        IgExporter.XVerIgExportTrackingRecord tracking = Tracking(pair);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(tracking);
        StructureDefinition profile = built.Profiles.ShouldHaveSingleItem().Definition;
        ElementDefinition slice = profile.Differential.Element.Single(ed => ed.SliceName is not null);
        slice.Path.ShouldBe(modifier ? "ValueSet.modifierExtension" : "ValueSet.extension");
        string url = slice.Type.ShouldHaveSingleItem().Profile.ShouldHaveSingleItem();
        ImmutableArray<RepresentationLocation> locations = built.Representations.Resolve(outcome.Key, SourcePath(source.Id));
        locations.ShouldContain(location => location.Steps.SequenceEqual(new RepresentationStep[] { new MemberStep("renamed") }));
        RepresentationLocation extension = locations.Single(location => location.Steps.OfType<ExtensionStep>().Any());
        extension.Steps.OfType<ExtensionStep>().ShouldHaveSingleItem().ShouldBe(new ExtensionStep(url, modifier));
        extension.Steps[^1].ShouldBe(new ChoiceStep("code"));
        built.Extensions.ShouldHaveSingleItem().Definition.Url.ShouldBe(url);
        profile.Differential.Element.SelectMany(ed => ed.Constraint).Count().ShouldBe(cardinalityOnly ? 1 : 0);
        tracking.EdOutcomeMapTargets[element.Key].ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NestedSlicesUseFixedLocalUrlsRatherThanOutcomeSuffixes(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement parent = fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.filter");
        DbElement child = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.filter.op", "code");
        DbElementOutcome parentOutcome = fixture.AddRepresentationOutcome(pair, parent, "ValueSet");
        DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, child, parent: parentOutcome);
        childOutcome.GenSliceName = "not-the-source-name";
        DbElementOutcome.Update(fixture.Connection, [childOutcome]);

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        StructureDefinition extension = built.Extensions.ShouldHaveSingleItem().Definition;
        extension.Differential.Element.Single(element => element.ElementId == "Extension.extension:op.url")
            .Fixed.ShouldBeOfType<FhirUri>().Value.ShouldBe("not-the-source-name");
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(parent.Id, child.Id)).ShouldHaveSingleItem();
        location.Steps.OfType<ExtensionStep>().Select(step => step.Url).ShouldBe([parentOutcome.GenUrl!, "not-the-source-name"]);
        location.State.ShouldBe(RepresentationState.Observable);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void SubstitutionsAreExplicitUnresolvedLocationsWithoutNewConstraints(int targetIndex, bool nested)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), nested ? "ValueSet.group.op" : "ValueSet.op", "code");
        DbElementOutcome? parent = null;
        if (nested)
        {
            DbElement group = fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.group");
            source.ParentElementKey = group.Key;
            DbElement.Update(fixture.Connection, [source]);
            group.ChildElementCount = 1;
            DbElement.Update(fixture.Connection, [group]);
            parent = fixture.AddRepresentationOutcome(pair, group, "ValueSet");
        }

        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, nested ? null : "ValueSet", parent);
        element.ExtensionSubstitutionUrl = "http://example.org/opaque/substitution";
        DbElementOutcome.Update(fixture.Connection, [element]);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key,
            nested ? SourcePath("ValueSet.group", source.Id) : SourcePath(source.Id)).ShouldHaveSingleItem();
        location.State.ShouldBe(RepresentationState.OpaqueSubstitution);
        location.Steps.OfType<ExtensionStep>().Last().Url.ShouldBe(element.ExtensionSubstitutionUrl);
        built.Profiles.SelectMany(profile => profile.Definition.Differential.Element).SelectMany(ed => ed.Constraint).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BasicFallbackKeepsTheOriginalResourceInsideItsActualExtension(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage));
        DbElement root = DbElement.SelectSingle(fixture.Connection, StructureKey: outcome.SourceStructureKey, Id: "ValueSet")!;
        DbElement code = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.op", "code");
        root = DbElement.SelectSingle(fixture.Connection, Key: root.Key)!;
        DbElementOutcome rootOutcome = fixture.AddRepresentationOutcome(pair, root, "Basic");
        fixture.AddRepresentationOutcome(pair, code, parent: rootOutcome);

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        built.Profiles.ShouldHaveSingleItem().Definition.Type.ShouldBe("Basic");
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(code.Id)).ShouldHaveSingleItem();
        location.Steps.OfType<ExtensionStep>().Select(step => step.Url).ShouldBe([rootOutcome.GenUrl!, "fixed-local-op"]);
        location.Steps.ShouldNotContain(new MemberStep("code"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ValidValuesAndInvalidDatatypeDecompositionShareTheirLocations(int targetIndex, bool valid)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        string typeName = valid ? "Coding" : "NewCoding";
        if (!valid)
        {
            DbStructureDefinition type = fixture.AddStructure(pair.SourcePackage, typeName);
            fixture.AddElement(type, typeName);
            DbElement child = fixture.AddTypedElement(type, typeName + ".code", "code");
            fixture.AddRepresentationOutcome(pair, child);
        }

        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.extra", typeName);
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        StructureDefinition extension = built.Extensions.Single(extension => extension.Definition.Url == element.GenUrl).Definition;
        ImmutableArray<UnsafeCodePathStep> path = valid ? SourcePath(source.Id) :
            SourcePath(source.Id).Add(new(typeName + ".code", typeName, false));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, path).ShouldHaveSingleItem();
        location.State.ShouldBe(RepresentationState.Observable);
        if (valid)
        {
            extension.Differential.Element.Single(ed => ed.Path == "Extension.value[x]").Type.ShouldHaveSingleItem().Code.ShouldBe("Coding");
            location.Steps[^1].ShouldBe(new ChoiceStep("Coding"));
        }
        else
        {
            extension.Differential.Element.ShouldContain(ed => ed.Fixed is FhirUri && ((FhirUri)ed.Fixed).Value == ProfileRepresentationIndex.DatatypeMetadataUrl);
            extension.Differential.Element.ShouldContain(ed => ed.Fixed is FhirString && ((FhirString)ed.Fixed).Value == typeName);
            location.Steps.OfType<ExtensionStep>().Last().Url.ShouldBe("code");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReusedNestedDatatypesComposeNativeAndExtensionSitesIndependently(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition inner = fixture.AddStructure(package, "Inner");
            fixture.AddElement(inner, "Inner");
            fixture.AddTypedElement(inner, "Inner.status", "code");
            DbStructureDefinition outer = fixture.AddStructure(package, "Outer");
            fixture.AddElement(outer, "Outer");
            fixture.AddTypedElement(outer, "Outer.nested", "Inner");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.first", "Outer");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.second", "Outer");
        }

        DbElement extra = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage, "Inner"), "Inner.extra", "code");
        DbElementOutcome extraOutcome = fixture.AddRepresentationOutcome(pair, extra, "Inner");
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        foreach (string use in new[] { "first", "second" })
        {
            ImmutableArray<UnsafeCodePathStep> prefix = SourcePath("ValueSet." + use).Add(new("Outer.nested", "Outer", false));
            RepresentationLocation native = built.Representations.Resolve(outcome.Key, prefix.Add(new("Inner.status", "Inner", false))).ShouldHaveSingleItem();
            native.Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe([use, "nested", "status"]);
            RepresentationLocation extension = built.Representations.Resolve(outcome.Key, prefix.Add(new(extra.Id, "Inner", false))).ShouldHaveSingleItem();
            extension.Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe([use, "nested", "value"]);
            extension.Steps.OfType<ExtensionStep>().ShouldHaveSingleItem().Url.ShouldBe(extraOutcome.GenUrl);
            extension.State.ShouldBe(RepresentationState.Observable);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ContentReferencesIncludeInitialAndUnboundedDescendantUses(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition structure = fixture.Structure(package);
            DbElement node = fixture.AddElement(structure, "ValueSet.node");
            fixture.AddTypedElement(structure, "ValueSet.node.op", "code");
            DbElement recursive = fixture.AddElement(structure, "ValueSet.node.child");
            recursive.ContentReferenceSourceKey = node.Key;
            recursive.DefinedAsContentReference = true;
            DbElement.Update(fixture.Connection, [recursive]);
        }

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        built.Representations.Resolve(outcome.Key, SourcePath("ValueSet.node", "ValueSet.node.op")).ShouldHaveSingleItem()
            .Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe(["node", "op"]);
        ImmutableArray<UnsafeCodePathStep> path = SourcePath("ValueSet.node");
        for (int i = 0; i < 80; i++)
        {
            path = path.Add(new("ValueSet.node.child", null, false)).Add(new("ValueSet.node", null, true));
        }

        RepresentationLocation descendant = built.Representations.Resolve(outcome.Key, path.Add(new("ValueSet.node.op", null, false))).ShouldHaveSingleItem();
        descendant.State.ShouldBe(RepresentationState.Observable);
        descendant.Steps.OfType<RepeatStep>().ShouldNotBeEmpty();
        descendant.Steps.OfType<RepeatStep>().ShouldAllBe(repeat => repeat.IncludeInitial);
        built.Representations.Occurrences(outcome.Key).ShouldContain(occurrence => occurrence.SourcePath.Any(step => step.IsContentReference));
    }

    [Fact]
    public void ResourceValuesChangeOwnersButReferencesNeverFollowExternalInstances()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition child = fixture.AddStructure(package, "Child", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Resource);
            fixture.AddElement(child, "Child");
            fixture.AddTypedElement(child, "Child.op", "code");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.child", "Child");
            DbStructureDefinition reference = fixture.AddStructure(package, "Reference");
            fixture.AddElement(reference, "Reference");
            fixture.AddTypedElement(reference, "Reference.external", "Child");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.link", "Reference");
        }

        fixture.Connect(pair, outcome,
            DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "ValueSet.child")!,
            DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "ValueSet.child")!);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath("ValueSet.child")).ShouldHaveSingleItem();
        location.Steps.ShouldContain(new ResourceOwnerStep("Child"));
        built.Representations.Occurrences(outcome.Key).ShouldNotContain(occurrence => occurrence.SourcePath.Any(step => step.ElementId == "Reference.external"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PrimitiveMetadataNodesRemainNavigableWithoutScalarValues(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.extra", "code");
        DbStructureDefinition target = fixture.Structure(pair.TargetPackage);
        fixture.AddElement(target, "ValueSet.compose");
        fixture.AddElement(target, "ValueSet.compose.include");
        fixture.AddElement(target, "ValueSet.compose.include.filter");
        fixture.AddTypedElement(target, UnsafeCodeTestFixture.FilterElement, "code");
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, UnsafeCodeTestFixture.FilterElement);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(source.Id)).ShouldHaveSingleItem();
        location.Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe(["compose", "include", "filter", "op", "value"]);
        ITypedElement instance = UnsafeCodeTestFixture.TargetInstance(targetIndex, $$$$"""
            {"resourceType":"ValueSet","status":"active","compose":{"include":[{"filter":[{"property":"status","value":"active","_op":{"extension":[{"url":"{{{{element.GenUrl}}}}","valueCode":"unsafe"}]}}]}]}}
            """);
        ITypedElement op = instance.Children("compose").Single().Children("include").Single().Children("filter").Single().Children("op").Single();
        op.Value.ShouldBeNull();
        Locate(instance, location.Steps).ShouldHaveSingleItem().Value.ShouldBe("unsafe");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExtensionContentReferencesBindTheConstructedCanonicalAndRepeatFromTheInitialNode(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbStructureDefinition source = fixture.Structure(pair.SourcePackage);
        DbElement node = fixture.AddElement(source, "ValueSet.node");
        DbElement op = fixture.AddTypedElement(source, "ValueSet.node.op", "code");
        DbElement child = fixture.AddElement(source, "ValueSet.node.child");
        child.ContentReferenceSourceKey = node.Key;
        DbElement.Update(fixture.Connection, [child]);
        DbElementOutcome parentOutcome = fixture.AddRepresentationOutcome(pair, node, "ValueSet");
        fixture.AddRepresentationOutcome(pair, op, parent: parentOutcome);
        DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, child, parent: parentOutcome);
        childOutcome.ContentReferenceAncestorId = node.Id;
        childOutcome.ContentReferenceExtensionUrl = parentOutcome.GenUrl;
        DbElementOutcome.Update(fixture.Connection, [childOutcome]);

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        StructureDefinition extension = built.Extensions.ShouldHaveSingleItem().Definition;
        extension.Differential.Element.Single(ed => ed.ElementId == "Extension.extension:child.url")
            .Fixed.ShouldBeOfType<FhirUri>().Value.ShouldBe(parentOutcome.GenUrl);
        ImmutableArray<UnsafeCodePathStep> path = SourcePath(node.Id, child.Id).Add(new(node.Id, null, true)).Add(new(op.Id, null, false));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, path).ShouldHaveSingleItem();
        location.State.ShouldBe(RepresentationState.Observable);
        RepeatStep repeat = location.Steps.OfType<RepeatStep>().ShouldHaveSingleItem();
        repeat.IncludeInitial.ShouldBeTrue();
        repeat.Steps.OfType<ExtensionStep>().ShouldHaveSingleItem().Url.ShouldBe(parentOutcome.GenUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RecursiveNativeAndOverflowBranchesBothKeepTheirOwnInitialLocations(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbElement node = fixture.AddElement(fixture.Structure(package), "ValueSet.node");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.node.op", "code");
            DbElement child = fixture.AddElement(fixture.Structure(package), "ValueSet.node.child");
            child.ContentReferenceSourceKey = node.Key;
            DbElement.Update(fixture.Connection, [child]);
        }

        DbElement source = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "ValueSet.node")!;
        DbElement target = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "ValueSet.node")!;
        DbElementOutcome rootOutcome = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
        fixture.Connect(pair, outcome, source, target, rootOutcome);
        DbElement code = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "ValueSet.node.op")!;
        fixture.AddRepresentationOutcome(pair, code, parent: rootOutcome);
        DbElement childSource = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "ValueSet.node.child")!;
        DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, childSource, parent: rootOutcome);
        childOutcome.ContentReferenceAncestorId = source.Id;
        childOutcome.ContentReferenceExtensionUrl = rootOutcome.GenUrl;
        DbElementOutcome.Update(fixture.Connection, [childOutcome]);

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        ImmutableArray<UnsafeCodePathStep> path = SourcePath(source.Id, childSource.Id).Add(new(source.Id, null, true)).Add(new(code.Id, null, false));
        ImmutableArray<RepresentationLocation> locations = built.Representations.Resolve(outcome.Key, path);
        locations.Length.ShouldBe(2);
        locations.ShouldAllBe(location => location.State == RepresentationState.Observable);
        locations.ShouldAllBe(location => location.Steps.OfType<RepeatStep>().Count() == 1);
        locations.ShouldContain(location => location.Steps[0] is MemberStep);
        locations.ShouldContain(location => location.Steps[0] is ExtensionStep);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MutuallyRecursiveDatatypeUsesKeepInitialAndRepeatedLocations(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition a = fixture.AddStructure(package, "A");
            DbStructureDefinition b = fixture.AddStructure(package, "B");
            fixture.AddElement(a, "A");
            fixture.AddElement(b, "B");
            fixture.AddTypedElement(a, "A.op", "code");
            fixture.AddTypedElement(a, "A.next", "B");
            fixture.AddTypedElement(b, "B.next", "A");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.entry", "A");
        }

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        ImmutableArray<UnsafeCodePathStep> initial = SourcePath("ValueSet.entry").Add(new("A.op", "A", false));
        built.Representations.Resolve(outcome.Key, initial).ShouldHaveSingleItem().Steps.OfType<RepeatStep>().ShouldBeEmpty();
        ImmutableArray<UnsafeCodePathStep> descendants = SourcePath("ValueSet.entry")
            .Add(new("A.next", "A", false)).Add(new("B.next", "B", false)).Add(new("A.op", "A", false));
        RepresentationLocation recursive = built.Representations.Resolve(outcome.Key, descendants).ShouldHaveSingleItem();
        recursive.State.ShouldBe(RepresentationState.Observable);
        recursive.Steps.OfType<RepeatStep>().ShouldHaveSingleItem().Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe(["next", "next"]);
        built.Representations.Occurrences(outcome.Key).Where(occurrence => occurrence.SourcePath[^1].ElementId == "A.op")
            .ShouldContain(occurrence => occurrence.Locations.Any(location => location.Steps.OfType<RepeatStep>().Any()));
    }

    [Theory]
    [InlineData("Quantity", "ErasedVersion")]
    [InlineData("code", "ErasedIdentity")]
    public void NativeIdentityErasureIsRecordedWithoutIssuingActiveTriggerDiagnostics(string targetType, string expected)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "Coding");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.renamed", targetType);
        fixture.Connect(pair, outcome, source, target);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(source.Id)).ShouldHaveSingleItem();
        location.SourceElementId.ShouldBe(source.Id);
        location.SourceType.ShouldBe("Coding");
        location.TargetType.ShouldBe(targetType);
        location.State.ShouldBe(Enum.Parse<RepresentationState>(expected));
    }

    [Fact]
    public void NativeCodeTransformationsRetainSourceIdentityEvenForDeclaredEquivalence()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "code");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.renamed", "code");
        source.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet + "|source-vs";
        target.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet + "|target-vs";
        DbElement.Update(fixture.Connection, [source, target]);
        fixture.Connect(pair, outcome, source, target);
        DbUnsafeCodeMapEvidence evidence = fixture.AddEvidence(pair, sourceVersion: "source-vs", targetVersion: "target-vs",
            noMap: false, relationship: ConceptMap.ConceptMapRelationship.Equivalent);
        evidence.TargetSystem = "http://example.org/target-system";
        evidence.SourceSystemVersion = "original-version";
        evidence.TargetSystemVersion = "replacement-version";
        evidence.TargetCode = "changed";
        DbUnsafeCodeMapEvidence.Update(fixture.Connection, [evidence]);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(source.Id)).ShouldHaveSingleItem();
        CodeTransformation transformation = location.Transformations.ShouldHaveSingleItem();
        transformation.SourceSystem.ShouldBe(UnsafeCodeTestFixture.FilterSystem);
        transformation.SourceCode.ShouldBe("unsafe");
        transformation.SourceVersion.ShouldBe("original-version");
        transformation.TargetCode.ShouldBe("changed");
        location.SourceBinding.ShouldBe(new UnsafeCodeCanonical(UnsafeCodeTestFixture.FilterValueSet, "source-vs"));
    }

    [Fact]
    public void NameAllocationStillConstructsExtensionsBeforeProfiles()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        outcome.GenName = "Collision";
        DbStructureOutcome.Update(fixture.Connection, [outcome]);
        DbElement code = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.extra", "code");
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, code, "ValueSet");
        element.GenName = "Collision";
        DbElementOutcome.Update(fixture.Connection, [element]);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        built.Extensions.ShouldHaveSingleItem().Definition.Name.ShouldBe("Collision");
        built.Profiles.ShouldHaveSingleItem().Definition.Name.ShouldBe("ProfileValueSet");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void PromotedChoicesUseTheActualChoiceElementAndChosenType(int targetIndex, bool reference)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", "code");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.answer[x]", "code");
        target.IsChoiceType = true;
        DbElement.Update(fixture.Connection, [target]);
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, target.Id);
        element.RequiresExtensionDefinition = false;
        element.ExtensionSubstitutionUrl = reference ? CommonDefinitions.ExtUrlAlternateReference : CommonDefinitions.ExtUrlAlternateCanonical;
        DbElementOutcome.Update(fixture.Connection, [element]);

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        ElementDefinition slice = built.Profiles.ShouldHaveSingleItem().Definition.Differential.Element.Single(ed => ed.SliceName is not null);
        slice.Path.ShouldBe(reference ? "ValueSet.answerReference.extension" : "ValueSet.answerCanonical.extension");
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(source.Id)).ShouldHaveSingleItem();
        location.Steps.ShouldBe(new RepresentationStep[]
        {
            new MemberStep("answer"), new ChoiceStep(reference ? "Reference" : "canonical"), new ExtensionStep(element.ExtensionSubstitutionUrl),
        });
        location.State.ShouldBe(RepresentationState.OpaqueSubstitution);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DecomposedCodingRetainsTypedSystemVersionAndCodeFields(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement targetValue = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "Extension.value[x]")!;
        DbElementType.Delete(fixture.Connection, ElementKey: targetValue.Key, TypeName: "Coding");
        DbStructureDefinition coding = fixture.Structure(pair.SourcePackage, "Coding");
        foreach (DbElement child in DbElement.SelectList(fixture.Connection, StructureKey: coding.Key).Where(child => child.Id.Contains('.')))
        {
            DbElementOutcome childOutcome = fixture.AddRepresentationOutcome(pair, child);
            childOutcome.RequiresExtensionDefinition = false;
            childOutcome.RequiresSliceDefinition = true;
            DbElementOutcome.Update(fixture.Connection, [childOutcome]);
        }

        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.extra", "Coding");
        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(source.Id)).ShouldHaveSingleItem();
        location.State.ShouldBe(RepresentationState.Observable);
        location.Steps.OfType<DatatypeStep>().ShouldHaveSingleItem().Name.ShouldBe("Coding");
        CodeCarrierLocation carrier = location.Carrier.ShouldNotBeNull();
        carrier.IsDecomposed.ShouldBeTrue();
        carrier.Kind.ShouldBe(CodeCarrierKind.Coding);
        carrier.System.OfType<ExtensionStep>().ShouldHaveSingleItem().Url.ShouldBe("system");
        carrier.Version.OfType<ExtensionStep>().ShouldHaveSingleItem().Url.ShouldBe("version");
        carrier.Code.OfType<ExtensionStep>().ShouldHaveSingleItem().Url.ShouldBe("code");
        StructureDefinition extension = built.Extensions.Single(extension => extension.Definition.Url == element.GenUrl).Definition;
        extension.Differential.Element.Single(ed => ed.ElementId == "Extension.extension:code.url")
            .Fixed.ShouldBeOfType<FhirUri>().Value.ShouldBe(carrier.Code.OfType<ExtensionStep>().Single().Url);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MultipleInvalidDatatypeBranchesUseTheirConstructedValueSliceUrls(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.extra", "NewA,NewB");
        foreach (string typeName in new[] { "NewA", "NewB" })
        {
            DbStructureDefinition type = fixture.AddStructure(pair.SourcePackage, typeName);
            DbElement root = fixture.AddElement(type, typeName);
            DbElement field = fixture.AddTypedElement(type, typeName + ".code", "code");
            DbElementOutcome rootOutcome = fixture.AddRepresentationOutcome(pair, root);
            DbElementOutcome fieldOutcome = fixture.AddRepresentationOutcome(pair, field, parent: rootOutcome);
            rootOutcome.RequiresExtensionDefinition = false;
            DbElementOutcome.Update(fixture.Connection, [rootOutcome]);
            fixture.AddType(source, typeName, type);
        }

        DbElementOutcome element = fixture.AddRepresentationOutcome(pair, source, "ValueSet");
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        foreach (string typeName in new[] { "NewA", "NewB" })
        {
            RepresentationLocation location = built.Representations.Resolve(outcome.Key,
                SourcePath(source.Id).Add(new(typeName + ".code", typeName, false))).ShouldHaveSingleItem();
            location.State.ShouldBe(RepresentationState.Observable);
            location.Steps.OfType<ExtensionStep>().Select(step => step.Url).ShouldBe([element.GenUrl!, "value" + typeName, "fixed-local-code"]);
            location.Steps.OfType<DatatypeStep>().ShouldHaveSingleItem().Name.ShouldBe(typeName);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InheritedProfileTypeSitesResolveTheSelectedSnapshotNotASameIdBaseElement(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition inner = fixture.AddStructure(package, "Inner");
            fixture.AddElement(inner, "Inner");
            fixture.AddTypedElement(inner, "Inner.status", "code");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.first", "Inner");
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.second", "Inner");
        }

        DbStructureDefinition profile = fixture.AddStructure(pair.SourcePackage, "InnerProfile", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Profile);
        fixture.AddElement(profile, "Inner");
        DbElement inherited = fixture.AddTypedElement(profile, "Inner.status", "code");
        inherited.IsInherited = true;
        inherited.BasePath = "Inner.status";
        inherited.BindingValueSet = "http://example.org/ValueSet/profile-only";
        DbElement.Update(fixture.Connection, [inherited]);
        foreach (DbElementType type in DbElementType.SelectList(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, TypeName: "Inner"))
        {
            type.TypeProfile = profile.VersionedUrl;
            type.TypeProfileStructureKey = profile.Key;
            DbElementType.Update(fixture.Connection, [type]);
        }

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        foreach (string use in new[] { "first", "second" })
        {
            RepresentationLocation location = built.Representations.Resolve(outcome.Key,
                SourcePath("ValueSet." + use).Add(new("Inner.status", profile.VersionedUrl, false))).ShouldHaveSingleItem();
            location.State.ShouldBe(RepresentationState.Observable);
            location.SourceBinding!.Url.ShouldBe(inherited.BindingValueSet);
            location.Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe([use, "status"]);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void SparseProfileTypeSitesResolveInheritedFieldsAndPreferOverrides(int targetIndex, bool overrideField)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, targetIndex);
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbStructureDefinition sourceType = fixture.AddStructure(pair.SourcePackage, "Inner");
        DbElement sourceRoot = fixture.AddElement(sourceType, "Inner");
        DbElement inherited = fixture.AddTypedElement(sourceType, "Inner.status", "code");
        inherited.BindingValueSet = "http://example.org/ValueSet/base";
        DbElement.Update(fixture.Connection, [inherited]);
        DbStructureDefinition intermediate = fixture.AddStructure(pair.SourcePackage, "IntermediateProfile", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Profile);
        DbElement intermediateRoot = fixture.AddElement(intermediate, "Inner");
        intermediateRoot.BaseStructureKey = sourceType.Key;
        DbElement.Update(fixture.Connection, [intermediateRoot]);
        DbStructureDefinition profile = fixture.AddStructure(pair.SourcePackage, "InnerProfile", Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Profile);
        DbElement profileRoot = fixture.AddElement(profile, "Inner");
        profileRoot.BaseElementKey = intermediateRoot.Key;
        DbElement.Update(fixture.Connection, [profileRoot]);
        DbElement effective = inherited;
        if (overrideField)
        {
            effective = fixture.AddTypedElement(profile, "Inner.status", "code");
            effective.BaseElementKey = inherited.Key;
            effective.BindingValueSet = "http://example.org/ValueSet/override";
            DbElement.Update(fixture.Connection, [effective]);
        }

        DbStructureDefinition targetType = fixture.AddStructure(pair.TargetPackage, "Inner");
        fixture.AddElement(targetType, "Inner");
        DbElement target = fixture.AddTypedElement(targetType, "Inner.renamed", "code");
        DbStructureOutcome typeOutcome = fixture.AddOutcome(pair, sourceType, targetType);
        fixture.Connect(pair, typeOutcome, inherited, target);
        DbElement use = fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.first", "Inner");
        fixture.AddType(use, "Inner", sourceType, profile);
        fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.first", "Inner");

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        ImmutableArray<UnsafeCodePathStep> path = SourcePath(use.Id).Add(new(effective.Id, profile.VersionedUrl, false));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, path).ShouldHaveSingleItem();
        location.State.ShouldBe(RepresentationState.Observable);
        location.SourceBinding!.Url.ShouldBe(effective.BindingValueSet);
        location.Steps.OfType<MemberStep>().Select(step => step.Name).ShouldBe(["first", "renamed"]);
        built.Representations.Occurrences(outcome.Key).ShouldContain(occurrence =>
            occurrence.SourcePath.SequenceEqual(path) && occurrence.Locations.Any(candidate => candidate.State == RepresentationState.Observable));
        DbElement.SelectList(fixture.Connection, StructureKey: profile.Key).Count.ShouldBe(overrideField ? 2 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedCodingTransformationsUseOnlyTheirActualResourceBindingContexts(bool primitiveField)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        foreach (string use in new[] { "mapped", "unrelated" })
        {
            DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet." + use, "CodeableConcept");
            DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet." + use, "CodeableConcept");
            source.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet + "|source-" + use;
            target.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet + "|target-" + use;
            DbElement.Update(fixture.Connection, [source, target]);
            fixture.Connect(pair, outcome, source, target);
        }

        DbUnsafeCodeMapEvidence evidence = fixture.AddEvidence(pair, sourceVersion: "source-mapped", targetVersion: "target-mapped", noMap: false);
        evidence.TargetSystem = "http://example.org/target-system";
        evidence.SourceSystemVersion = "original-version";
        evidence.TargetSystemVersion = "replacement-version";
        evidence.TargetCode = "changed";
        DbUnsafeCodeMapEvidence.Update(fixture.Connection, [evidence]);
        DbUnsafeCodeMapEvidence unrelated = fixture.AddEvidence(pair, sourceVersion: "source-mapped", targetVersion: "target-unrelated", noMap: false);
        unrelated.TargetCode = "wrong-context";
        DbUnsafeCodeMapEvidence.Update(fixture.Connection, [unrelated]);
        DbElement coding = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "CodeableConcept.coding")!;
        fixture.AddRepresentationOutcome(pair, coding, "CodeableConcept");

        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        foreach (string use in new[] { "mapped", "unrelated" })
        {
            ImmutableArray<UnsafeCodePathStep> path = SourcePath("ValueSet." + use).Add(new("CodeableConcept.coding", "CodeableConcept", false));
            if (primitiveField)
            {
                path = path.Add(new("Coding.code", "Coding", false));
            }

            ImmutableArray<RepresentationLocation> locations = built.Representations.Resolve(outcome.Key, path);
            locations.Length.ShouldBe(2);
            RepresentationLocation extension = locations.Single(location => location.Steps.OfType<ExtensionStep>().Any());
            extension.State.ShouldBe(RepresentationState.Observable);
            extension.Transformations.ShouldBeEmpty();
            RepresentationLocation location = locations.Single(location => !location.Steps.OfType<ExtensionStep>().Any());
            location.State.ShouldBe(RepresentationState.Observable);
            location.SourceBinding.ShouldBe(new UnsafeCodeCanonical(UnsafeCodeTestFixture.FilterValueSet, "source-" + use));
            if (use == "mapped")
            {
                CodeTransformation transformation = location.Transformations.ShouldHaveSingleItem();
                TransformationIdentity(transformation).ShouldBe((
                    UnsafeCodeTestFixture.FilterSystem, "original-version", "unsafe",
                    evidence.TargetSystem, "replacement-version", "changed"));
                transformation.Provenance.ShouldBe([MapProvenance(evidence)]);
            }
            else
            {
                location.Transformations.ShouldBeEmpty();
            }
        }
    }

    [Theory]
    [InlineData("Quantity", false, false)]
    [InlineData("Age", false, false)]
    [InlineData("Count", false, false)]
    [InlineData("Distance", false, false)]
    [InlineData("Duration", false, false)]
    [InlineData("MoneyQuantity", false, false)]
    [InlineData("SimpleQuantity", false, false)]
    [InlineData("Quantity", true, false)]
    [InlineData("Quantity", true, true)]
    public void QuantityStyleIdentityLossRequiresProofBeforeUsingPrimitiveCodes(string sourceType, bool bound, bool ambiguous)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        if (sourceType != "Quantity")
        {
            DbStructureDefinition type = fixture.AddStructure(pair.SourcePackage, sourceType);
            DbElement root = fixture.AddElement(type, sourceType);
            root.BaseStructureKey = fixture.Structure(pair.SourcePackage, "Quantity").Key;
            DbElement.Update(fixture.Connection, [root]);
        }

        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.original", sourceType);
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.renamed", "code");
        if (bound)
        {
            foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
            {
                DbValueSet valueSet = fixture.SetBinding(package);
                valueSet.CanExpand = false;
                valueSet.Compose = new() { Include = [new() { System = UnsafeCodeTestFixture.FilterSystem, Concept = [new() { Code = "unsafe" }] }] };
                if (ambiguous)
                {
                    valueSet.Compose.Include.Add(new() { System = "http://example.org/other-system", Concept = [new() { Code = "unsafe" }] });
                }
                DbValueSet.Update(fixture.Connection, [valueSet]);
            }

            source.BindingValueSet = target.BindingValueSet = UnsafeCodeTestFixture.FilterValueSet;
            DbElement.Update(fixture.Connection, [source, target]);
        }

        fixture.Connect(pair, outcome, source, target);
        StructureFhirExporter.BuiltDefinitions built = Exporter(fixture).BuildDefinitions(Tracking(pair));
        RepresentationLocation location = built.Representations.Resolve(outcome.Key, SourcePath(source.Id)).ShouldHaveSingleItem();
        location.SourceType.ShouldBe(sourceType);
        location.TargetType.ShouldBe("code");
        location.State.ShouldBe(bound && !ambiguous ? RepresentationState.Observable : RepresentationState.ErasedIdentity);
        built.Profiles.SelectMany(profile => profile.Definition.Differential.Element).SelectMany(element => element.Constraint).ShouldBeEmpty();
    }

    private static IEnumerable<ITypedElement> Locate(ITypedElement root, ImmutableArray<RepresentationStep> steps)
    {
        IEnumerable<ITypedElement> nodes = [root];
        foreach (RepresentationStep step in steps)
        {
            nodes = step switch
            {
                MemberStep member => nodes.SelectMany(node => node.Children(member.Name)),
                ChoiceStep choice => nodes.Where(node => node.InstanceType == choice.TypeName),
                ExtensionStep extension => nodes.SelectMany(node => node.Children(extension.IsModifier ? "modifierExtension" : "extension"))
                    .Where(node => node.Children("url").Any(url => Equals(url.Value, extension.Url))),
                _ => throw new InvalidOperationException("This test navigates only straight constructed paths."),
            };
        }

        return nodes;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SchedulingAndConstructionDoNotCreateOrDeleteOutputRoots(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = new();
        string output = Path.Combine(fixture.SourceRoot, "must-not-exist");
        ConfigXVer config = new() { OutputDirectory = output, ExportR4 = targetIndex == 0, ExportR4B = targetIndex == 1, ExportR5 = false };
        IgExporter exporter = new(fixture.Connection, NullLoggerFactory.Instance, output, null, new XVerExporter(fixture.Connection, config), config);
        Directory.Exists(output).ShouldBeFalse();
        IgExporter.ExportSchedule schedule = exporter.BuildExportSchedule();
        schedule.PackagePairs.Select(pair => pair.SequencePair).ShouldBe(targetIndex == 0
            ? [(FhirReleases.FhirSequenceCodes.R4B, FhirReleases.FhirSequenceCodes.R4), (FhirReleases.FhirSequenceCodes.R5, FhirReleases.FhirSequenceCodes.R4)]
            : [(FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R4B), (FhirReleases.FhirSequenceCodes.R5, FhirReleases.FhirSequenceCodes.R4B)]);
        schedule.ValidationPackages.Select(package => package.DefinitionFhirSequence).ShouldBe([fixture.Packages[targetIndex].DefinitionFhirSequence]);
        exporter.BuildExportSchedule(maxStepSize: 0).PackagePairs.ShouldBeEmpty();
        FhirPackageComparisonPair selected = new(fixture.Packages[2], fixture.Packages[targetIndex]);
        exporter.BuildExportSchedule(specificPairs: [selected.SequencePair]).PackagePairs.ShouldHaveSingleItem().SequencePair.ShouldBe(selected.SequencePair);
        Directory.Exists(output).ShouldBeFalse();
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "sentinel.txt");
        File.WriteAllText(sentinel, "unchanged");
        exporter.BuildExportSchedule(maxStepSize: 1, specificPairs: [selected.SequencePair]);
        File.ReadAllText(sentinel).ShouldBe("unchanged");
    }

    private static ImmutableArray<UnsafeCodePathStep> SourcePath(params string[] elements) =>
        [new("ValueSet", null, false), .. elements.Select(element => new UnsafeCodePathStep(element, null, false))];

    private static IgExporter.XVerIgExportTrackingRecord Tracking(FhirPackageComparisonPair pair) => new()
    {
        PackagePair = pair,
        PackageId = $"hl7.fhir.uv.xver-r5.{pair.TargetFhirSequence.ToRLiteral().ToLowerInvariant()}",
    };

    private static StructureFhirExporter Exporter(UnsafeCodeTestFixture fixture) =>
        new(new XVerExporter(fixture.Connection, new ConfigXVer { XverArtifactVersion = "0.1.0-test" }),
            fixture.Connection, NullLoggerFactory.Instance);

    private static void AssertCompletedEmpty(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair)
    {
        fixture.InputState.CompletedInputRevision.ShouldNotBeNullOrEmpty();
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection).ShouldContain(evidence => evidence.ExplicitNoMap);
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodePairState state = fixture.PairState(pair);
        state.ComparisonRevision.ShouldNotBeNullOrEmpty();
        state.OutcomeInputRevision.ShouldBe(fixture.InputState.CompletedInputRevision);
        state.OutcomeComparisonRevision.ShouldBe(state.ComparisonRevision);
        state.OutcomeFormatVersion.ShouldBe(UnsafeCodeConstants.OutcomeFormatVersion);
    }
}
