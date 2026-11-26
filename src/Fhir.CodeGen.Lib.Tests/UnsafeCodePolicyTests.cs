// <copyright file="UnsafeCodePolicyTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Text.Json;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Shouldly;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Lib.Tests;

public class UnsafeCodePolicyTests
{
    private const string System = "http://example.org/CodeSystem/demo";
    private const string Code = "unsafe";
    private const string Destination = "4.0.1";
    private static readonly UnsafeCodeIdentity Identity = new(System, null, Code);
    private static readonly UnsafeCodeMappingContext Context = new(5, 4, "Resource.code", "Resource.code");
    private static readonly UnsafeCodeElementAnnotation Annotation = new(Context, Provenance("elements.json"));

    public static IEnumerable<object[]> BooleanMatrix()
    {
        for (int mask = 0; mask < 128; mask++)
        {
            yield return [.. Enumerable.Range(0, 7).Select(bit => (object)((mask & (1 << bit)) != 0))];
        }
    }

    [Theory]
    [MemberData(nameof(BooleanMatrix))]
    public void ClassifiesIndependentSourcesAndEligibleExemptions(
        bool annotated,
        bool noMap,
        bool configured,
        bool noMapOnly,
        bool relationshipPresent,
        bool excludedRelationship,
        bool excludedDestination)
    {
        List<UnsafeCodeMapEvidence> evidence = [];
        if (noMap)
        {
            evidence.Add(Evidence(noMap: true));
        }

        if (relationshipPresent)
        {
            evidence.Add(Evidence(relationship: CMR.Equivalent, targetIndex: 0));
        }

        UnsafeCodeRule rule = Rule(noMapOnly: noMapOnly, excludedRelationship: excludedRelationship, excludedDestination: excludedDestination);
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, annotated ? [Annotation] : [], evidence, configured ? [rule] : []);
        bool eligible = configured && (!noMapOnly || noMap);
        bool exempt = eligible && (excludedDestination || (excludedRelationship && relationshipPresent));
        bool expected = !exempt && ((annotated && noMap) || eligible);

        decisions.Any().ShouldBe(expected);
        foreach (UnsafeCodeDecision decision in decisions)
        {
            decision.Identity.ShouldBe(Identity);
            decision.Context.ShouldBe(Context);
            decision.VersionMatch.ShouldBe(new(true, []));
            decision.AnnotationProvenance.Length.ShouldBe(annotated && noMap ? 1 : 0);
            decision.RuleProvenance.Length.ShouldBe(eligible ? 1 : 0);
            decision.EvidenceProvenance.Length.ShouldBe(noMap && ((annotated && noMap) || (eligible && noMapOnly)) ? 1 : 0);
        }
    }

    public static IEnumerable<object[]> VersionMatrix()
    {
        for (int wildcardMode = 0; wildcardMode < 7; wildcardMode++)
        {
            for (int exactMode = 0; exactMode < 7; exactMode++)
            {
                for (int mask = 0; mask < 8; mask++)
                {
                    yield return [wildcardMode, exactMode, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(VersionMatrix))]
    public void PartitionsWildcardAndExactRulesBeforeApplyingEligibility(
        int wildcardMode,
        int exactMode,
        bool annotated,
        bool noMap,
        bool relationshipPresent)
    {
        List<UnsafeCodeRule> rules = [];
        if (wildcardMode > 0)
        {
            rules.Add(ModeRule(wildcardMode, null, 0));
        }

        if (exactMode > 0)
        {
            rules.Add(ModeRule(exactMode, "v1", 1));
        }

        List<UnsafeCodeMapEvidence> evidence = [];
        if (noMap)
        {
            evidence.Add(Evidence(noMap: true));
        }

        if (relationshipPresent)
        {
            evidence.Add(Evidence(relationship: CMR.Equivalent, targetIndex: 0));
        }

        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, annotated ? [Annotation] : [], evidence, rules);
        foreach (string? version in new string?[] { null, "v1", "v2", "V1" })
        {
            bool wildcardEligible = wildcardMode > 0 && (wildcardMode % 2 == 1 || noMap);
            bool exactEligible = version == "v1" && exactMode > 0 && (exactMode % 2 == 1 || noMap);
            bool wildcardExempt = wildcardEligible && (wildcardMode is 3 or 4 || (wildcardMode is 5 or 6 && relationshipPresent));
            bool exactExempt = exactEligible && (exactMode is 3 or 4 || (exactMode is 5 or 6 && relationshipPresent));
            bool expected = !wildcardExempt && !exactExempt && ((annotated && noMap) || wildcardEligible || exactEligible);
            int matches = decisions.Count(decision => decision.VersionMatch.Matches(version));
            matches.ShouldBe(expected ? 1 : 0, $"wildcard={wildcardMode}, exact={exactMode}, version={version ?? "<unknown>"}");

            if (version is not null)
            {
                // The same version established by an authoritative source context needs no wildcard predicate.
                ImmutableArray<UnsafeCodeDecision> known = UnsafeCodePolicy.Classify(
                    Identity with { Version = version }, Destination, Context, annotated ? [Annotation] : [], evidence, rules);
                known.Length.ShouldBe(expected ? 1 : 0);
                if (expected)
                {
                    known[0].Identity.Version.ShouldBe(version);
                    known[0].VersionMatch.ShouldBe(new(false, [version]));
                }
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void VersionExceptionsSubtractFromWildcardTriggers(bool annotationDefault, bool relationshipExemption)
    {
        List<UnsafeCodeRule> rules = [Rule(version: "v1", excludedRelationship: relationshipExemption, excludedDestination: !relationshipExemption)];
        if (!annotationDefault)
        {
            rules.Add(Rule(entry: 1));
        }

        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, annotationDefault ? [Annotation] : [],
            [Evidence(noMap: true), Evidence(relationship: CMR.Equivalent, targetIndex: 0)], rules);
        decisions.Length.ShouldBe(1);
        decisions[0].VersionMatch.ShouldBe(new(true, ["v1"]));
        decisions[0].VersionMatch.Matches("v1").ShouldBeFalse();
        decisions[0].VersionMatch.Matches("v2").ShouldBeTrue();
        decisions[0].VersionMatch.Matches(null).ShouldBeTrue();
        decisions[0].RuleProvenance.ShouldNotContain(new UnsafeCodeRuleProvenance("rules.json", 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IneligibleNoMapOnlyExclusionsCannotCancelIndependentRules(bool relationshipExemption)
    {
        UnsafeCodeRule[] rules =
        [
            Rule(),
            Rule(version: "v1", noMapOnly: true, excludedRelationship: relationshipExemption, excludedDestination: !relationshipExemption, entry: 1),
        ];
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, [Annotation], [Evidence(relationship: CMR.Equivalent)], rules);
        decisions.Length.ShouldBe(1);
        decisions[0].VersionMatch.ShouldBe(new(true, []));
        decisions[0].AnnotationProvenance.ShouldBeEmpty();
        decisions[0].RuleProvenance.ShouldBe([new UnsafeCodeRuleProvenance("rules.json", 0)]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("v1", true)]
    [InlineData("v2", false)]
    public void ExactRulesDoNotProveAnUnknownOrDifferentVersion(string? version, bool expected)
    {
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, [], [], [Rule(version: "v1")]);
        decisions.Length.ShouldBe(1);
        decisions[0].VersionMatch.ShouldBe(new(false, ["v1"]));
        decisions.Any(decision => decision.VersionMatch.Matches(version)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v1")]
    [InlineData("v2")]
    public void AuthoredNoMapAndRelationshipFactsAreVersionScoped(string? evidenceVersion)
    {
        UnsafeCodeMapEvidence noMap = Evidence(noMap: true, version: evidenceVersion);
        ImmutableArray<UnsafeCodeDecision> annotated = UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], [noMap], []);
        annotated.Length.ShouldBe(1);
        annotated[0].VersionMatch.ShouldBe(evidenceVersion is null ? new(true, []) : new(false, [evidenceVersion]));

        ImmutableArray<UnsafeCodeDecision> exempted = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, [], [Evidence(version: evidenceVersion, relationship: CMR.Equivalent)],
            [Rule(excludedRelationship: true)]);
        if (evidenceVersion is null)
        {
            exempted.ShouldBeEmpty();
        }
        else
        {
            exempted.Length.ShouldBe(1);
            exempted[0].VersionMatch.ShouldBe(new(true, [evidenceVersion]));
            exempted[0].VersionMatch.Matches(null).ShouldBeTrue();
        }
    }

    public static IEnumerable<object?[]> EvidenceVersionMatrix()
    {
        foreach (string? noMapVersion in new string?[] { null, "v1", "v2" })
        {
            foreach (string? relationshipVersion in new string?[] { null, "v1", "v2" })
            {
                yield return [noMapVersion, relationshipVersion];
            }
        }
    }

    [Theory]
    [MemberData(nameof(EvidenceVersionMatrix))]
    public void NoMapEligibilityAndRelationshipExemptionsUseTheSameVersionPartition(string? noMapVersion, string? relationshipVersion)
    {
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, [Annotation],
            [Evidence(noMap: true, version: noMapVersion), Evidence(version: relationshipVersion, relationship: CMR.Equivalent)],
            [Rule(noMapOnly: true, excludedRelationship: true)]);
        foreach (string? version in new string?[] { null, "v1", "v2", "v3" })
        {
            bool noMapApplies = noMapVersion is null || (version is not null && version == noMapVersion);
            bool relationshipApplies = relationshipVersion is null || (version is not null && version == relationshipVersion);
            decisions.Count(decision => decision.VersionMatch.Matches(version)).ShouldBe(noMapApplies && !relationshipApplies ? 1 : 0);
        }
    }

    [Fact]
    public void MultipleExactExemptionsRemainDisjointFromWildcardAndExactTriggers()
    {
        UnsafeCodeRule[] rules =
        [
            Rule(),
            Rule(version: "v,1", excludedDestination: true, entry: 1),
            Rule(version: "v|'2", excludedDestination: true, entry: 2),
            Rule(version: "v3", entry: 3),
        ];
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(Identity, Destination, Context, [], [], rules);
        decisions.Length.ShouldBe(2);
        decisions[0].VersionMatch.ShouldBe(new(true, ["v,1", "v3", "v|'2"]));
        decisions[1].VersionMatch.ShouldBe(new(false, ["v3"]));
        foreach (string? version in new string?[] { null, "v,1", "v|'2", "v3", "other" })
        {
            decisions.Count(decision => decision.VersionMatch.Matches(version)).ShouldBe(version is "v,1" or "v|'2" ? 0 : 1);
        }
    }

    [Fact]
    public void ScopeTargetAndReleaseVersionsNeverSupplyASourceCodeSystemVersion()
    {
        UnsafeCodeMapEvidence evidence = Evidence(noMap: true) with
        {
            SourceValueSet = new("urn:source", "v1"),
            TargetValueSet = new("urn:target", "v1"),
            TargetSystem = new("urn:target-system", "v1"),
        };
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(
            Identity, Destination, Context, [Annotation], [evidence], [Rule(version: "v1", excludedDestination: true)]);
        decisions.Length.ShouldBe(1);
        decisions[0].VersionMatch.ShouldBe(new(true, ["v1"]));
        decisions[0].VersionMatch.Matches(null).ShouldBeTrue();

        ImmutableArray<UnsafeCodeDecision> releaseIsNotSourceVersion = UnsafeCodePolicy.Classify(
            Identity, "v1", Context, [], [evidence], [Rule(version: "v1")]);
        releaseIsNotSourceVersion.Single().VersionMatch.ShouldBe(new(false, ["v1"]));
        releaseIsNotSourceVersion.Single().VersionMatch.Matches(null).ShouldBeFalse();
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [], [], [Rule(excludedDestination: true)]).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, "4.3.0", Context, [], [], [Rule(excludedDestination: true)]).Length.ShouldBe(1);
    }

    [Fact]
    public void UnrelatedContextOrExemptCodeDoesNotCancelAnotherTrigger()
    {
        UnsafeCodeMappingContext otherTarget = Context with { TargetElementId = "Resource.otherCode" };
        UnsafeCodeMappingContext reverse = new(4, 5, Context.TargetElementId, Context.SourceElementId);
        UnsafeCodeRule[] rules = [Rule(excludedRelationship: true), Rule(code: "safe", excludedRelationship: true, entry: 1)];
        UnsafeCodeMapEvidence[] evidence = [Evidence(noMap: true), Evidence(code: "safe", relationship: CMR.Equivalent)];
        ImmutableArray<UnsafeCodeDecision> unsafeCode = UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], evidence, rules);
        UnsafeCodePolicy.Classify(Identity with { Code = "safe" }, Destination, Context, [Annotation], evidence, rules).ShouldBeEmpty();
        unsafeCode.Length.ShouldBe(1);

        // Same code and direction, but different target binding: scope selection belongs to the caller.
        ImmutableArray<UnsafeCodeDecision> exemptContext = UnsafeCodePolicy.Classify(
            Identity, Destination, otherTarget, [Annotation], [Evidence(relationship: CMR.Equivalent)], rules);
        exemptContext.ShouldBeEmpty();
        unsafeCode.Concat(exemptContext).Single().Context.ShouldBe(Context);

        UnsafeCodePolicy.Classify(Identity, Destination, otherTarget, [Annotation], [Evidence(noMap: true)], []).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, reverse, [Annotation], evidence, [Rule(noMapOnly: true)]).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, Context with { TargetFhirPackageKey = 43 }, [Annotation], evidence, [Rule(noMapOnly: true)]).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, Context with { SourceElementId = "Other.code" }, [Annotation], evidence, []).ShouldBeEmpty();
    }

    [Fact]
    public void NoMapForAnotherCodeSystemOrDirectionCannotOpenTheGate()
    {
        UnsafeCodeMapEvidence[] unrelated =
        [
            Evidence(noMap: true, code: "other"),
            Evidence(noMap: true, system: "urn:other"),
            Evidence(noMap: true) with { SourceFhirPackageKey = 4, TargetFhirPackageKey = 5 },
            Evidence(noMap: true) with { TargetFhirPackageKey = 43 },
            Evidence(noMap: true) with { SourceSystem = new(null, null) },
            Evidence(noMap: true) with { SourceCode = null },
        ];
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], unrelated, [Rule(noMapOnly: true)]).ShouldBeEmpty();
        ImmutableArray<UnsafeCodeDecision> independent = UnsafeCodePolicy.Classify(Identity, Destination, Context, [], unrelated, [Rule()]);
        independent.Length.ShouldBe(1);
        independent[0].EvidenceProvenance.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryApplicableCodeTargetParticipatesInRelationshipExemptions(bool equivalentFirst)
    {
        UnsafeCodeMapEvidence[] evidence =
        [
            Evidence(noMap: true),
            Evidence(relationship: equivalentFirst ? CMR.Equivalent : CMR.RelatedTo, targetIndex: 0),
            Evidence(relationship: equivalentFirst ? CMR.RelatedTo : CMR.Equivalent, targetIndex: 1) with { TargetCode = "second" },
        ];
        UnsafeCodePolicy.Classify(
            Identity, Destination, Context, [Annotation], evidence, [Rule(noMapOnly: true, excludedRelationship: true)]).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], evidence, []).Length.ShouldBe(1);
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], evidence.Where(item => !item.ExplicitNoMap), []).ShouldBeEmpty();
    }

    [Fact]
    public void MissingMapsAndEmptyTargetsNeverManufactureNoMap()
    {
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], [], []).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], [Evidence()], [Rule(noMapOnly: true)]).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [], [Evidence(noMap: true)], []).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://example.org/CodeSystem/demo", Code)]
    [InlineData("http://example.org/CodeSystem/demo/", Code)]
    [InlineData("HTTP://EXAMPLE.ORG/CodeSystem/demo", Code)]
    [InlineData(System, "UNSAFE")]
    public void IdentifiersAreComparedExactly(string system, string code)
    {
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [], [], [Rule(system: system, code: code)]).ShouldBeEmpty();
        UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], [Evidence(noMap: true, system: system, code: code)], []).ShouldBeEmpty();
    }

    [Fact]
    public void DestinationComparisonsAreOrdinalEvenIfAnInputSetUsesAnotherComparer()
    {
        UnsafeCodeRule rule = Rule() with { ExcludedVersions = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Release") };
        UnsafeCodePolicy.Classify(Identity, "release", Context, [], [], [rule]).Length.ShouldBe(1);
        UnsafeCodePolicy.Classify(Identity, "Release", Context, [], [], [rule]).ShouldBeEmpty();
    }

    [Fact]
    public void RuleOrderAndDuplicateEvidenceDoNotChangeDecisions()
    {
        UnsafeCodeElementAnnotation[] annotations = [Annotation, new(Context, Provenance("a-elements.json"))];
        UnsafeCodeMapEvidence[] evidence =
        [
            Evidence(noMap: true),
            Evidence(noMap: true, filename: "a-codes.json"),
            Evidence(noMap: true, version: "v3", filename: "versioned-codes.json"),
            Evidence(relationship: CMR.RelatedTo, targetIndex: 0),
        ];
        UnsafeCodeRule[] rules =
        [
            Rule(),
            Rule(version: "v1", excludedDestination: true, entry: 1),
            Rule(version: "v2", entry: 2),
        ];
        ImmutableArray<UnsafeCodeDecision> expected = UnsafeCodePolicy.Classify(Identity, Destination, Context, annotations, evidence, rules);
        expected.Length.ShouldBe(3);
        expected[0].VersionMatch.ShouldBe(new(true, ["v1", "v2", "v3"]));
        expected[1].VersionMatch.ShouldBe(new(false, ["v2"]));
        expected[2].VersionMatch.ShouldBe(new(false, ["v3"]));
        expected[0].AnnotationProvenance.Select(item => item.SourceRelativeFilename).ShouldBe(["a-elements.json", "elements.json"]);
        expected[0].EvidenceProvenance.Select(item => item.SourceRelativeFilename).ShouldBe(["a-codes.json", "codes.json"]);
        expected[2].EvidenceProvenance.Length.ShouldBe(3);

        Random random = new(17);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            ImmutableArray<UnsafeCodeDecision> actual = UnsafeCodePolicy.Classify(
                Identity with { }, Destination, Context with { },
                annotations.Concat(annotations.Select(item => item with { Provenance = item.Provenance with { } })).OrderBy(_ => random.Next()),
                evidence.Concat(evidence.Select(item => item with { Provenance = item.Provenance with { } })).OrderBy(_ => random.Next()),
                rules.Concat(rules.Select(item => item with
                {
                    ExcludedRelationships = item.ExcludedRelationships.ToArray().ToImmutableHashSet(),
                    ExcludedVersions = item.ExcludedVersions.ToArray().ToImmutableHashSet(StringComparer.Ordinal),
                })).OrderBy(_ => random.Next()));
            actual.SequenceEqual(expected).ShouldBeTrue();
            actual.Select(item => item.GetHashCode()).ShouldBe(expected.Select(item => item.GetHashCode()));
            JsonSerializer.Serialize(actual).ShouldBe(JsonSerializer.Serialize(expected));
        }
    }

    [Fact]
    public void EquivalentPartitionsAreCoalescedAndProvenanceRemainsDistinct()
    {
        UnsafeCodeProvenance sameSource = Provenance("codes.json");
        UnsafeCodeMapEvidence[] evidence =
        [
            Evidence(noMap: true, version: "v2") with { Provenance = sameSource },
            Evidence(noMap: true, version: "v1") with { Provenance = sameSource },
            Evidence(noMap: true, version: "v3", filename: "other-codes.json"),
        ];
        ImmutableArray<UnsafeCodeDecision> decisions = UnsafeCodePolicy.Classify(Identity, Destination, Context, [Annotation], evidence, []);
        decisions.Length.ShouldBe(2);
        decisions[0].VersionMatch.ShouldBe(new(false, ["v1", "v2"]));
        decisions[1].VersionMatch.ShouldBe(new(false, ["v3"]));
        decisions[0].EvidenceProvenance.ShouldBe([sameSource]);
        decisions[1].EvidenceProvenance.ShouldBe([Provenance("other-codes.json")]);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, "v1", false)]
    [InlineData(false, "v1", true)]
    [InlineData(true, "V1", true)]
    [InlineData(false, "V1", false)]
    [InlineData(true, "v2", true)]
    [InlineData(false, "v2", false)]
    public void VersionMatchesNormalizeAndHaveStructuralEquality(bool wildcard, string? version, bool expected)
    {
        UnsafeCodeVersionMatch match = new(wildcard, ["v|'quoted,version", "v1", "v1"]);
        UnsafeCodeVersionMatch normalized = new(wildcard, ["v1", "v|'quoted,version"]);
        match.ShouldBe(normalized);
        match.GetHashCode().ShouldBe(normalized.GetHashCode());
        match.Matches(version).ShouldBe(expected);
        JsonSerializer.Deserialize<UnsafeCodeVersionMatch>(JsonSerializer.Serialize(match)).ShouldBe(match);
        new UnsafeCodeVersionMatch(wildcard, []).Matches(version).ShouldBe(wildcard);
    }

    private static UnsafeCodeRule ModeRule(int mode, string? version, int entry) => Rule(
        version: version,
        noMapOnly: mode % 2 == 0,
        excludedRelationship: mode is 5 or 6,
        excludedDestination: mode is 3 or 4,
        entry: entry);

    private static UnsafeCodeRule Rule(
        string? version = null,
        bool noMapOnly = false,
        bool excludedRelationship = false,
        bool excludedDestination = false,
        string system = System,
        string code = Code,
        int entry = 0) => new(
            new(system, version, code),
            noMapOnly,
            excludedRelationship ? [CMR.Equivalent] : [],
            excludedDestination ? [Destination] : [],
            "rules.json",
            entry);

    private static UnsafeCodeMapEvidence Evidence(
        bool noMap = false,
        string? version = null,
        CMR? relationship = null,
        string system = System,
        string code = Code,
        int? targetIndex = null,
        string filename = "codes.json") => new(
            Context.SourceFhirPackageKey,
            Context.TargetFhirPackageKey,
            new("urn:source-vs", "source-vs-version"),
            new("urn:target-vs", "target-vs-version"),
            new(system, version),
            code,
            new("urn:target-system", "target-system-version"),
            targetIndex is null ? null : "target",
            relationship,
            noMap,
            Provenance(filename, targetIndex));

    private static UnsafeCodeProvenance Provenance(string filename, int? targetIndex = null) =>
        new(filename, "map", "urn:map", 0, 0, targetIndex);
}
