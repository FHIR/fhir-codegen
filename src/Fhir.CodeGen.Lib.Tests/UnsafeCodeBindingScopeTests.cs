// <copyright file="UnsafeCodeBindingScopeTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Terminology;
using Hl7.Fhir.Model;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

public class UnsafeCodeBindingScopeTests
{
    private const string SourceSystem = "urn:example:source";
    private const string TargetSystem = "urn:example:target";
    private const string SystemVersion = "system-v7";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvedImportsKeepScopeLocalMembersAndVersions(bool duplicateImport)
    {
        DbValueSet inner = ValueSetRow(2, 1, "inner", "inner-v3", SourceSystem, "unsafe");
        DbValueSet sibling = ValueSetRow(3, 1, "sibling", "sibling-v4", SourceSystem, "sibling");
        DbValueSet outer = ValueSetRow(1, 1, "outer", "outer-v2");
        outer.Compose = new() { Include = [new() { ValueSet = duplicateImport
            ? [inner.VersionedUrl, inner.VersionedUrl, sibling.VersionedUrl] : [inner.VersionedUrl, sibling.VersionedUrl] }] };
        UnsafeCodeBindingScope scope = Resolver(outer, inner, sibling).Resolve(Binding(outer)).ShouldNotBeNull();
        scope.Required.ShouldBeTrue();
        scope.Complete.ShouldBeTrue();
        scope.DirectCanonical.ShouldBe(new(outer.UnversionedUrl, outer.Version));
        scope.Canonicals.ShouldContain(new UnsafeCodeCanonical(inner.UnversionedUrl, "inner-v3"));
        scope.ImportedCanonicals.Count.ShouldBe(2);
        scope.Members.Length.ShouldBe(duplicateImport ? 3 : 2);
        scope.ScopedMembers[new(inner.UnversionedUrl, inner.Version)].ShouldBe(
            [new UnsafeCodeBindingMember(new(SourceSystem, SystemVersion), "unsafe")]);
        UnsafeCodeEvidenceApplicability.Identities(scope, "unsafe", new(inner.UnversionedUrl, inner.Version))
            .ShouldBe([new UnsafeCodeCanonical(SourceSystem, SystemVersion)]);
        UnsafeCodeEvidenceApplicability.Allows(scope, new(SourceSystem, SystemVersion), "sibling", new(outer.UnversionedUrl, outer.Version)).ShouldBeTrue();
        UnsafeCodeEvidenceApplicability.Allows(scope, new(SourceSystem, SystemVersion), "sibling", new(inner.UnversionedUrl, inner.Version)).ShouldBeFalse();
        UnsafeCodeEvidenceApplicability.Allows(scope, new(SourceSystem, SystemVersion), "unsafe", new(inner.UnversionedUrl, SystemVersion)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("valid-key", true, 1)]
    [InlineData("unversioned-keyed", true, 1)]
    [InlineData("unversioned-unique", true, 1)]
    [InlineData("missing-key-fallback", true, 1)]
    [InlineData("key-only", true, 1)]
    [InlineData("key-url-fallback", true, 1)]
    [InlineData("key-version-fallback", true, 1)]
    [InlineData("key-package-fallback", true, 1)]
    [InlineData("key-only-wrong-package", false, 0)]
    [InlineData("wrong-package", false, 0)]
    [InlineData("exact-version-mismatch", false, 0)]
    [InlineData("unversioned-ambiguous", false, 2)]
    public void BindingResolutionHonorsPackageAndKeyConsistency(string scenario, bool complete, int memberCount)
    {
        DbValueSet expected = ValueSetRow(1, 1, "outer", "outer-v2", SourceSystem, "unsafe");
        DbValueSet inconsistent = ValueSetRow(2, 1, "different", "different-v1", TargetSystem, "other");
        TestBinding bound = Binding(expected);
        switch (scenario)
        {
            case "unversioned-keyed":
                bound.BindingValueSet = expected.UnversionedUrl;
                break;
            case "unversioned-unique":
                bound.BindingValueSet = expected.UnversionedUrl;
                bound.BindingValueSetKey = null;
                break;
            case "missing-key-fallback":
                bound.BindingValueSetKey = 99;
                break;
            case "key-only":
                bound.BindingValueSet = null;
                break;
            case "key-url-fallback":
                bound.BindingValueSetKey = inconsistent.Key;
                break;
            case "key-version-fallback":
                inconsistent.UnversionedUrl = expected.UnversionedUrl;
                bound.BindingValueSetKey = inconsistent.Key;
                break;
            case "key-package-fallback":
                inconsistent.FhirPackageKey = 2;
                inconsistent.UnversionedUrl = expected.UnversionedUrl;
                inconsistent.Version = expected.Version;
                bound.BindingValueSetKey = inconsistent.Key;
                break;
            case "key-only-wrong-package":
                inconsistent.FhirPackageKey = 2;
                bound.BindingValueSetKey = inconsistent.Key;
                bound.BindingValueSet = null;
                break;
            case "wrong-package":
                expected.FhirPackageKey = 2;
                break;
            case "exact-version-mismatch":
                bound.BindingValueSet = expected.UnversionedUrl + "|absent-version";
                break;
            case "unversioned-ambiguous":
                inconsistent.UnversionedUrl = expected.UnversionedUrl;
                bound.BindingValueSetKey = null;
                bound.BindingValueSet = expected.UnversionedUrl;
                break;
        }

        UnsafeCodeBindingScope scope = Resolver(expected, inconsistent).Resolve(bound).ShouldNotBeNull();
        scope.Complete.ShouldBe(complete);
        scope.Members.Length.ShouldBe(memberCount);
        if (complete)
        {
            scope.DirectCanonical.ShouldBe(new(expected.UnversionedUrl, expected.Version));
            scope.Members.ShouldBe([new UnsafeCodeBindingMember(new(SourceSystem, SystemVersion), "unsafe")]);
        }
        else if (scenario == "key-only-wrong-package")
        {
            scope.Canonicals.ShouldBeEmpty();
            scope.DirectCanonical.ShouldBe(new(null, null));
        }
    }

    [Theory]
    [InlineData("nested", true, 1)]
    [InlineData("cycle", false, 1)]
    [InlineData("missing", false, 0)]
    [InlineData("exact-import-version-mismatch", false, 0)]
    [InlineData("wrong-import-package", false, 0)]
    [InlineData("ambiguous", false, 2)]
    [InlineData("duplicate", true, 2)]
    [InlineData("missing-compose", false, 0)]
    public void NestedImportsCyclesAndIncompleteScopesRetainCurrentFacts(string scenario, bool complete, int memberCount)
    {
        DbValueSet outer = ValueSetRow(1, 1, "outer", "outer-v2");
        DbValueSet middle = ValueSetRow(2, 1, "middle", "middle-v5");
        DbValueSet inner = ValueSetRow(3, 1, "inner", "inner-v3", SourceSystem, "unsafe");
        DbValueSet other = ValueSetRow(4, 1, "other", "other-v4", TargetSystem, "changed");
        outer.Compose = new() { Include = [new() { ValueSet = [middle.VersionedUrl] }] };
        middle.Compose = new() { Include = [new() { ValueSet = [inner.VersionedUrl] }] };
        switch (scenario)
        {
            case "cycle":
                inner.Compose!.Include.Add(new() { ValueSet = [outer.VersionedUrl] });
                break;
            case "missing":
                middle.Compose.Include[0].ValueSet = ["urn:missing|v1"];
                break;
            case "exact-import-version-mismatch":
                middle.Compose.Include[0].ValueSet = [inner.UnversionedUrl + "|wrong-version"];
                break;
            case "wrong-import-package":
                inner.FhirPackageKey = 2;
                break;
            case "ambiguous":
                other.UnversionedUrl = inner.UnversionedUrl;
                middle.Compose.Include[0].ValueSet = [inner.UnversionedUrl];
                break;
            case "duplicate":
                middle.Compose.Include[0].ValueSet = [inner.VersionedUrl, inner.VersionedUrl];
                break;
            case "missing-compose":
                inner.Compose = null;
                break;
        }

        UnsafeCodeBindingScope scope = Resolver(outer, middle, inner, other).Resolve(Binding(outer)).ShouldNotBeNull();
        scope.Complete.ShouldBe(complete);
        scope.Members.Length.ShouldBe(memberCount);
        scope.ScopedMembers[new(middle.UnversionedUrl, middle.Version)].Length.ShouldBe(memberCount);
        scope.ScopedMembers[new(outer.UnversionedUrl, outer.Version)].Length.ShouldBe(memberCount);
        if (memberCount > 0)
        {
            scope.ScopedMembers[new(inner.UnversionedUrl, inner.Version)].Length.ShouldBe(1);
        }
        else if (scenario != "missing-compose")
        {
            scope.Canonicals.ShouldNotContain(new UnsafeCodeCanonical(inner.UnversionedUrl, inner.Version));
        }
    }

    [Theory]
    [InlineData("expansion", true)]
    [InlineData("empty-expansion", true)]
    [InlineData("compose", true)]
    [InlineData("filter", true)]
    [InlineData("exclude", false)]
    [InlineData("wildcard", true)]
    [InlineData("missing-compose", false)]
    public void ExpansionAndComposeRetainCurrentFacts(string scenario, bool complete)
    {
        DbValueSet valueSet = ValueSetRow(1, 1, "outer", "outer-v2", SourceSystem, "unsafe");
        DbValueSetConcept concept = DbValueSetConcept.EmptyCopy;
        concept.System = SourceSystem;
        concept.SystemVersion = "";
        concept.Code = "expanded";
        concept.Inactive = true;
        concept.Abstract = true;
        int calls = 0;
        if (scenario is "expansion" or "empty-expansion")
        {
            valueSet.CanExpand = true;
            valueSet.Compose!.Exclude.Add(new() { System = SourceSystem });
        }
        else if (scenario == "filter")
        {
            valueSet.Compose!.Include[0].Filter.Add(new() { Property = "status", Op = FilterOperator.Equal, Value = "active" });
        }
        else if (scenario == "exclude")
        {
            valueSet.Compose!.Exclude.Add(new() { System = SourceSystem, Concept = [new() { Code = "unsafe" }] });
        }
        else if (scenario == "wildcard")
        {
            valueSet.Compose!.Include[0].Concept.Clear();
            valueSet.Compose.Include[0].Version = null;
        }
        else if (scenario == "missing-compose")
        {
            valueSet.Compose = null;
        }

        UnsafeCodeBindingScopeResolver resolver = new(new Dictionary<int, DbValueSet> { [valueSet.Key] = valueSet }, key =>
        {
            key.ShouldBe(valueSet.Key);
            calls++;
            return scenario == "empty-expansion" ? [] : [concept];
        });
        calls.ShouldBe(0);
        UnsafeCodeBindingScope scope = resolver.Resolve(Binding(valueSet)).ShouldNotBeNull();
        scope.Complete.ShouldBe(complete);
        calls.ShouldBe(valueSet.CanExpand ? 1 : 0);
        scope.Members.ShouldBe(scenario switch
        {
            "expansion" => [new UnsafeCodeBindingMember(new(SourceSystem, null), "expanded")],
            "empty-expansion" or "missing-compose" => [],
            "wildcard" => [new UnsafeCodeBindingMember(new(SourceSystem, null), null)],
            _ => [new UnsafeCodeBindingMember(new(SourceSystem, SystemVersion), "unsafe")],
        });
        if (scenario == "wildcard")
        {
            UnsafeCodeEvidenceApplicability.Identities(scope, "any-code").ShouldBe([new UnsafeCodeCanonical(SourceSystem, null)]);
            UnsafeCodeEvidenceApplicability.Allows(scope, new(SourceSystem, "any-version"), "any-code", scope.DirectCanonical).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("precise", true, true, true)]
    [InlineData("missing-source", false, true, true)]
    [InlineData("missing-target", false, true, true)]
    [InlineData("unknown-version", false, true, true)]
    [InlineData("conflicting-vs-version", false, false, true)]
    [InlineData("nonmember", false, false, true)]
    [InlineData("incomplete-nonmember", false, true, true)]
    [InlineData("incomplete-precise", true, true, true)]
    [InlineData("qualified-source", true, true, true)]
    [InlineData("ambiguous-source", true, true, false)]
    [InlineData("incomplete-source", true, true, false)]
    [InlineData("sibling-source", true, true, false)]
    [InlineData("missing-target-system", true, true, true)]
    [InlineData("unscoped", true, true, true)]
    public void OutcomePrecisePotentialAndSourceQualificationRemainDistinct(string scenario, bool precise, bool potential, bool qualifiedSystem)
    {
        DbValueSet source = ValueSetRow(1, 1, "source", "source-v2", SourceSystem, "unsafe");
        DbValueSet target = ValueSetRow(2, 2, "target", "target-v3", TargetSystem, "changed");
        UnsafeCodeMapEvidence evidence = Evidence(new(source.UnversionedUrl, source.Version), new(target.UnversionedUrl, target.Version));
        TestBinding sourceBinding = Binding(source);
        if (scenario == "unknown-version")
        {
            sourceBinding.BindingValueSetKey = null;
            sourceBinding.BindingValueSet = source.UnversionedUrl;
        }
        if (scenario is "nonmember" or "incomplete-nonmember" or "sibling-source")
        {
            source.Compose!.Include[0].Concept[0].Code = "other";
        }
        if (scenario is "incomplete-nonmember" or "incomplete-source" or "incomplete-precise")
        {
            source.Compose!.Exclude.Add(new() { System = SourceSystem });
        }
        if (scenario == "ambiguous-source")
        {
            source.Compose!.Include.Add(new() { System = "urn:another", Concept = [new() { Code = "unsafe" }] });
        }
        if (scenario is "qualified-source" or "ambiguous-source" or "incomplete-source" or "sibling-source")
        {
            evidence = evidence with { SourceSystem = new(null, null) };
        }
        if (scenario == "conflicting-vs-version")
        {
            evidence = evidence with { SourceValueSet = new(source.UnversionedUrl, "wrong-vs-version") };
        }
        if (scenario == "missing-target-system")
        {
            evidence = evidence with { TargetSystem = new(null, null) };
        }
        if (scenario == "unscoped")
        {
            evidence = evidence with { SourceValueSet = new(null, null), TargetValueSet = new(null, null) };
        }
        UnsafeCodeBindingScopeResolver resolver = scenario == "unknown-version" ? Resolver(target) : Resolver(source, target);
        UnsafeCodeBindingScope? sourceScope = scenario == "missing-source" ? null : resolver.Resolve(sourceBinding);
        UnsafeCodeBindingScope? targetScope = scenario == "missing-target" ? null : resolver.Resolve(Binding(target));
        UnsafeCodeMapEvidence qualified = UnsafeCodeEvidenceApplicability.QualifyEvidence(evidence, sourceScope);
        (qualified.SourceSystem.Url is not null).ShouldBe(qualifiedSystem);
        UnsafeCodeEvidenceApplicability.IsPrecise(qualified, sourceScope, targetScope).ShouldBe(precise);
        UnsafeCodeEvidenceApplicability.IsPotential(qualified, sourceScope, targetScope).ShouldBe(potential);
        if (scenario == "qualified-source")
        {
            qualified.SourceSystem.ShouldBe(new(SourceSystem, SystemVersion));
            evidence.SourceSystem.ShouldBe(new(null, null));
        }
    }

    [Theory]
    [InlineData("imports", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("source-direct", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("source-direct-missing-system", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("target-direct", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("unresolved-direct", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("resolved-direct-nonmembers", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("resolved-direct-missing-system", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("independent-direct-canonical", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("direct-wildcard", UnsafeCodeEndpointMatchOrigin.DirectBinding, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("direct-version-mismatch", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.DirectBinding)]
    [InlineData("target-nonmember", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("sibling-source", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("source-vs-version", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("target-vs-version", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("source-system-version", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("target-system-version", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("wildcard-map-system-version", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("wildcard-member-system-version", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("wildcard-map-vs-version", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("incomplete-qualified-import", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("missing-source-system", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("scoped-source-qualification", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("incomplete-missing-source-system", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("ambiguous-source-system", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("missing-target-system", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("null-source-scope", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.ImportedScope)]
    [InlineData("null-target-scope", UnsafeCodeEndpointMatchOrigin.ImportedScope, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("no-map", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("null-source-code", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.None)]
    [InlineData("null-target-code", UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.None)]
    public void ProjectionEndpointsDistinguishDirectImportsAndNonmatches(
        string scenario, object expectedSourceOrigin, object expectedTargetOrigin)
    {
        UnsafeCodeEndpointMatchOrigin sourceOrigin = (UnsafeCodeEndpointMatchOrigin)expectedSourceOrigin;
        UnsafeCodeEndpointMatchOrigin targetOrigin = (UnsafeCodeEndpointMatchOrigin)expectedTargetOrigin;
        DbValueSet source = ValueSetRow(1, 1, "source-outer", "source-outer-v2");
        DbValueSet sourceInner = ValueSetRow(2, 1, "source-inner", "source-inner-v3", SourceSystem, "unsafe");
        DbValueSet target = ValueSetRow(3, 2, "target-outer", "target-outer-v2");
        DbValueSet targetInner = ValueSetRow(4, 2, "target-inner", "target-inner-v3", TargetSystem, "changed");
        source.Compose = new() { Include = [new() { ValueSet = [sourceInner.VersionedUrl] }] };
        target.Compose = new() { Include = [new() { ValueSet = [targetInner.VersionedUrl] }] };
        UnsafeCodeCanonical sourceDirect = new(source.UnversionedUrl, source.Version);
        UnsafeCodeCanonical targetDirect = new(target.UnversionedUrl, target.Version);
        UnsafeCodeMapEvidence evidence = Evidence(new(sourceInner.UnversionedUrl, sourceInner.Version), new(targetInner.UnversionedUrl, targetInner.Version));
        switch (scenario)
        {
            case "source-direct":
                evidence = evidence with { SourceValueSet = sourceDirect };
                break;
            case "source-direct-missing-system":
                evidence = evidence with { SourceValueSet = sourceDirect, SourceSystem = new(null, null) };
                break;
            case "target-direct":
                evidence = evidence with { TargetValueSet = targetDirect };
                break;
            case "unresolved-direct":
            case "direct-wildcard":
            case "direct-version-mismatch":
            case "resolved-direct-missing-system":
                evidence = evidence with { SourceValueSet = sourceDirect, TargetValueSet = targetDirect,
                    SourceSystem = new(null, null), TargetSystem = new(null, null) };
                if (scenario == "direct-wildcard")
                {
                    sourceDirect = sourceDirect with { Version = null };
                    targetDirect = targetDirect with { Version = null };
                    evidence = evidence with { SourceValueSet = sourceDirect, TargetValueSet = targetDirect };
                }
                if (scenario == "direct-version-mismatch")
                {
                    sourceDirect = sourceDirect with { Version = null };
                }
                break;
            case "resolved-direct-nonmembers":
                evidence = evidence with { SourceValueSet = sourceDirect, TargetValueSet = targetDirect };
                sourceInner.Compose!.Include[0].Concept[0].Code = "other";
                targetInner.Compose!.Include[0].Concept[0].Code = "other";
                break;
            case "independent-direct-canonical":
                sourceDirect = new("urn:unresolved-source", "authored-v1");
                targetDirect = new("urn:unresolved-target", "authored-v2");
                evidence = evidence with { SourceValueSet = sourceDirect, TargetValueSet = targetDirect };
                break;
            case "target-nonmember":
                targetInner.Compose!.Include[0].Concept[0].Code = "other";
                target.Compose.Include.Add(new() { System = TargetSystem, Version = SystemVersion, Concept = [new() { Code = "changed" }] });
                break;
            case "sibling-source":
                sourceInner.Compose!.Include[0].Concept[0].Code = "other";
                source.Compose.Include.Add(new() { System = SourceSystem, Version = SystemVersion, Concept = [new() { Code = "unsafe" }] });
                break;
            case "source-vs-version":
                evidence = evidence with { SourceValueSet = evidence.SourceValueSet with { Version = SystemVersion } };
                break;
            case "target-vs-version":
                evidence = evidence with { TargetValueSet = evidence.TargetValueSet with { Version = "wrong-vs-version" } };
                break;
            case "source-system-version":
                evidence = evidence with { SourceSystem = evidence.SourceSystem with { Version = "conflicting-system-version" } };
                break;
            case "target-system-version":
                evidence = evidence with { TargetSystem = evidence.TargetSystem with { Version = "conflicting-system-version" } };
                break;
            case "wildcard-map-system-version":
                evidence = evidence with { SourceSystem = evidence.SourceSystem with { Version = null }, TargetSystem = evidence.TargetSystem with { Version = null } };
                break;
            case "wildcard-member-system-version":
                sourceInner.Compose!.Include[0].Version = null;
                targetInner.Compose!.Include[0].Version = null;
                break;
            case "wildcard-map-vs-version":
                evidence = evidence with { SourceValueSet = evidence.SourceValueSet with { Version = null },
                    TargetValueSet = evidence.TargetValueSet with { Version = null } };
                break;
            case "incomplete-qualified-import":
                sourceInner.Compose!.Exclude.Add(new() { System = SourceSystem });
                targetInner.Compose!.Exclude.Add(new() { System = TargetSystem });
                break;
            case "missing-source-system":
            case "scoped-source-qualification":
            case "ambiguous-source-system":
            case "incomplete-missing-source-system":
                evidence = evidence with { SourceSystem = new(null, null) };
                if (scenario == "ambiguous-source-system")
                {
                    sourceInner.Compose!.Include.Add(new() { System = "urn:another", Concept = [new() { Code = "unsafe" }] });
                }
                if (scenario == "incomplete-missing-source-system")
                {
                    sourceInner.Compose!.Exclude.Add(new() { System = SourceSystem });
                }
                if (scenario == "scoped-source-qualification")
                {
                    source.Compose.Include.Add(new() { System = "urn:sibling-system", Concept = [new() { Code = "unsafe" }] });
                }
                break;
            case "missing-target-system":
                evidence = evidence with { TargetSystem = new(null, null) };
                break;
            case "null-source-scope":
                evidence = evidence with { SourceValueSet = new(null, null) };
                sourceDirect = new(null, null);
                break;
            case "null-target-scope":
                evidence = evidence with { TargetValueSet = new(null, null) };
                targetDirect = new(null, null);
                break;
            case "no-map":
                evidence = evidence with { ExplicitNoMap = true };
                break;
            case "null-source-code":
                evidence = evidence with { SourceCode = null };
                break;
            case "null-target-code":
                evidence = evidence with { TargetCode = null };
                break;
        }

        UnsafeCodeBindingScopeResolver resolver = Resolver(source, sourceInner, target, targetInner);
        UnsafeCodeBindingScope? sourceScope = scenario is "unresolved-direct" or "direct-wildcard" or "direct-version-mismatch" ? null : resolver.Resolve(Binding(source));
        UnsafeCodeBindingScope? targetScope = scenario is "unresolved-direct" or "direct-wildcard" or "direct-version-mismatch" ? null : resolver.Resolve(Binding(target));
        UnsafeCodeProjectionMatch match = UnsafeCodeEvidenceApplicability.MatchProjection(evidence, sourceDirect, targetDirect, sourceScope, targetScope);
        match.SourceOrigin.ShouldBe(sourceOrigin);
        match.TargetOrigin.ShouldBe(targetOrigin);
        match.IsMatch.ShouldBe(sourceOrigin != UnsafeCodeEndpointMatchOrigin.None && targetOrigin != UnsafeCodeEndpointMatchOrigin.None);
        if (scenario is "missing-source-system" or "scoped-source-qualification")
        {
            match.Evidence.SourceSystem.ShouldBe(new(SourceSystem, SystemVersion));
        }
        if (scenario is "unresolved-direct" or "resolved-direct-missing-system")
        {
            match.Evidence.ShouldBeSameAs(evidence);
            match.Evidence.SourceSystem.ShouldBe(new(null, null));
            match.Evidence.TargetSystem.ShouldBe(new(null, null));
        }
        if (scenario == "source-direct-missing-system")
        {
            match.Evidence.ShouldBeSameAs(evidence);
            match.Evidence.SourceSystem.ShouldBe(new(null, null));
        }
        if (match.IsMatch)
        {
            UnsafeCodeEvidenceApplicability.MatchProjection(evidence with { Relationship = ConceptMap.ConceptMapRelationship.NotRelatedTo },
                sourceDirect, targetDirect, sourceScope, targetScope).IsMatch.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvedScopesAreImmutableCachedAndPairLocal(bool required)
    {
        DbValueSet outer = ValueSetRow(1, 1, "outer", "outer-v2");
        DbValueSet inner = ValueSetRow(2, 1, "inner", "inner-v3");
        inner.CanExpand = true;
        outer.Compose = new() { Include = [new() { ValueSet = [inner.VersionedUrl] }] };
        Dictionary<int, DbValueSet> inputs = new() { [outer.Key] = outer, [inner.Key] = inner };
        DbValueSetConcept concept = DbValueSetConcept.EmptyCopy;
        concept.System = SourceSystem;
        concept.SystemVersion = SystemVersion;
        concept.Code = "unsafe";
        List<DbValueSetConcept> concepts = [concept];
        int calls = 0;
        UnsafeCodeBindingScopeResolver resolver = new(inputs, _ => { calls++; return concepts; });
        calls.ShouldBe(0);
        TestBinding bound = Binding(outer, required ? BindingStrength.Required : BindingStrength.Preferred);
        UnsafeCodeBindingScope scope = resolver.Resolve(bound).ShouldNotBeNull();
        scope.Required.ShouldBe(required);
        calls.ShouldBe(1);
        resolver.Resolve(bound).ShouldBeSameAs(scope);
        resolver.Resolve(Binding(outer, required ? BindingStrength.Required : BindingStrength.Preferred)).ShouldBeSameAs(scope);
        calls.ShouldBe(1);
        UnsafeCodeCanonical imported = new(inner.UnversionedUrl, inner.Version);
        ImmutableArray<UnsafeCodeBindingMember> frozenMembers = scope.ScopedMembers[imported];
        concept.Code = "regenerated";
        concepts.Clear();
        outer.Compose.Include.Clear();
        inner.Version = "regenerated-v4";
        scope.Members.ShouldBe([new UnsafeCodeBindingMember(new(SourceSystem, SystemVersion), "unsafe")]);
        scope.ScopedMembers[imported].ShouldBe(frozenMembers);
        scope.Canonicals.ShouldContain(imported);
        scope.ImportedCanonicals.ShouldContain(imported);
        scope.ScopedMembers.SetItem(imported, []).ShouldNotBeSameAs(scope.ScopedMembers);
        scope.ScopedMembers[imported].Length.ShouldBe(1);
        scope.Canonicals.Remove(imported).ShouldNotContain(imported);
        scope.Canonicals.ShouldContain(imported);
        frozenMembers.SetItem(0, frozenMembers[0] with { Code = "changed" })[0].Code.ShouldBe("changed");
        scope.Members[0].Code.ShouldBe("unsafe");
        UnsafeCodeEvidenceApplicability.QualifyEvidence(Evidence(imported, imported) with { SourceSystem = new(null, null) }, scope)
            .SourceSystem.ShouldBe(new(SourceSystem, SystemVersion));
        UnsafeCodeEvidenceApplicability.MatchProjection(Evidence(imported, imported),
            scope.DirectCanonical, scope.DirectCanonical, scope, scope).SourceOrigin.ShouldBe(UnsafeCodeEndpointMatchOrigin.ImportedScope);
        calls.ShouldBe(1);
        resolver.Resolve(bound).ShouldBeSameAs(scope);

        UnsafeCodeBindingScope fresh = new UnsafeCodeBindingScopeResolver(inputs, _ => { calls++; return concepts; })
            .Resolve(bound).ShouldNotBeNull();
        fresh.ShouldNotBeSameAs(scope);
        fresh.Members.ShouldBeEmpty();
        fresh.ImportedCanonicals.ShouldBeEmpty();
        calls.ShouldBe(1);

        outer.Compose.Include.Add(new() { ValueSet = [inner.UnversionedUrl + "|" + inner.Version] });
        concepts.Add(concept);
        UnsafeCodeBindingScope regenerated = new UnsafeCodeBindingScopeResolver(inputs, _ => { calls++; return concepts; })
            .Resolve(bound).ShouldNotBeNull();
        regenerated.Members[0].Code.ShouldBe("regenerated");
        regenerated.ImportedCanonicals.ShouldContain(new UnsafeCodeCanonical(inner.UnversionedUrl, inner.Version));
        calls.ShouldBe(2);
        scope.Members[0].Code.ShouldBe("unsafe");

        TestBinding unbound = new(99, 1, null, null);
        resolver.Resolve(unbound).ShouldBeNull();
        unbound.BindingValueSet = outer.VersionedUrl;
        resolver.Resolve(unbound).ShouldBeNull();
        new UnsafeCodeBindingScopeResolver(inputs, _ => concepts).Resolve(unbound).ShouldNotBeNull();
        resolver.Resolve(null).ShouldBeNull();
    }

    private static UnsafeCodeBindingScopeResolver Resolver(params DbValueSet[] valueSets) =>
        new(valueSets.ToDictionary(row => row.Key), _ => throw new InvalidOperationException("Compose resolution must not load concepts."));

    private static TestBinding Binding(DbValueSet valueSet, BindingStrength strength = BindingStrength.Required) =>
        new(valueSet.Key + 100, valueSet.FhirPackageKey, valueSet.VersionedUrl, valueSet.Key, strength);

    private static TestValueSet ValueSetRow(int key, int package, string name, string version, string? system = null, string? code = null) =>
        new(key, package, name, version)
        {
            Compose = new() { Include = system is null ? [] : [new() { System = system, Version = SystemVersion,
                Concept = code is null ? [] : [new() { Code = code }] }] },
        };

    private static UnsafeCodeMapEvidence Evidence(UnsafeCodeCanonical source, UnsafeCodeCanonical target) =>
        new(1, 2, source, target, new(SourceSystem, SystemVersion), "unsafe", new(TargetSystem, SystemVersion), "changed",
            null, false, new("input/codes/test.json", "test", "urn:test-map", 0, 0, 0));

    private sealed class TestValueSet : DbValueSet
    {
        [SetsRequiredMembers]
        internal TestValueSet(int key, int package, string name, string version)
        {
            Key = key;
            FhirPackageKey = package;
            Id = name;
            Name = name;
            Version = version;
            UnversionedUrl = "urn:valueset:" + name;
            VersionedUrl = UnversionedUrl + "|" + version;
        }
    }

    private sealed class TestBinding : DbElement
    {
        [SetsRequiredMembers]
        internal TestBinding(int key, int package, string? canonical, int? valueSetKey, BindingStrength strength = BindingStrength.Required)
        {
            Key = key;
            FhirPackageKey = package;
            Id = "Resource.binding";
            Path = Id;
            Name = "binding";
            MaxCardinalityString = "1";
            FullCollatedTypeLiteral = "code";
            DistinctTypeLiterals = "code";
            BindingValueSet = canonical;
            BindingValueSetKey = valueSetKey;
            ValueSetBindingStrength = strength;
        }
    }
}
