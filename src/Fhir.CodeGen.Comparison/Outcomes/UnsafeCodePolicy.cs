// <copyright file="UnsafeCodePolicy.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using Fhir.CodeGen.Comparison.Models;

namespace Fhir.CodeGen.Comparison.Outcomes;

internal static class UnsafeCodePolicy
{
    // The caller selects evidence through the applicable source/target binding scopes for this context.
    // Identity.Version is authoritative when present; null leaves version matching to the instance.
    internal static ImmutableArray<UnsafeCodeDecision> Classify(
        UnsafeCodeIdentity identity,
        string destinationRelease,
        UnsafeCodeMappingContext context,
        IEnumerable<UnsafeCodeElementAnnotation> annotations,
        IEnumerable<UnsafeCodeMapEvidence> evidence,
        IEnumerable<UnsafeCodeRule> rules)
    {
        ImmutableArray<UnsafeCodeProvenance> annotationProvenance = NormalizeProvenance(
            annotations.Where(annotation => annotation.Context == context).Select(annotation => annotation.Provenance));
        UnsafeCodeMapEvidence[] scopedEvidence = [.. evidence.Where(item =>
            item.SourceFhirPackageKey == context.SourceFhirPackageKey &&
            item.TargetFhirPackageKey == context.TargetFhirPackageKey &&
            string.Equals(item.SourceSystem.Url, identity.System, StringComparison.Ordinal) &&
            string.Equals(item.SourceCode, identity.Code, StringComparison.Ordinal))];
        UnsafeCodeRule[] matchingRules = [.. rules.Where(rule =>
            string.Equals(rule.Identity.System, identity.System, StringComparison.Ordinal) &&
            string.Equals(rule.Identity.Code, identity.Code, StringComparison.Ordinal))];

        if (identity.Version is string knownVersion)
        {
            UnsafeCodeDecision? decision = Evaluate(knownVersion);
            return decision is null ? [] : [decision with { VersionMatch = new(false, [knownVersion]) }];
        }

        string[] boundaries = [.. matchingRules.Select(rule => rule.Identity.Version)
            .Concat(scopedEvidence.Select(item => item.SourceSystem.Version))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        UnsafeCodeDecision? remainder = Evaluate(null);
        List<string> exceptions = [];
        Dictionary<UnsafeCodeDecision, List<string>> exactMatches = [];
        foreach (string version in boundaries)
        {
            UnsafeCodeDecision? decision = Evaluate(version);
            if (decision == remainder)
            {
                continue;
            }

            exceptions.Add(version);
            if (decision is not null)
            {
                if (!exactMatches.TryGetValue(decision, out List<string>? versions))
                {
                    versions = [];
                    exactMatches.Add(decision, versions);
                }

                versions.Add(version);
            }
        }

        ImmutableArray<UnsafeCodeDecision>.Builder results = ImmutableArray.CreateBuilder<UnsafeCodeDecision>();
        if (remainder is not null)
        {
            results.Add(remainder with { VersionMatch = new(true, [.. exceptions]) });
        }

        foreach (KeyValuePair<UnsafeCodeDecision, List<string>> match in exactMatches.OrderBy(match => match.Value[0], StringComparer.Ordinal))
        {
            results.Add(match.Key with { VersionMatch = new(false, [.. match.Value]) });
        }

        return results.ToImmutable();

        UnsafeCodeDecision? Evaluate(string? version)
        {
            UnsafeCodeMapEvidence[] applicableEvidence = [.. scopedEvidence.Where(item => Applies(item.SourceSystem.Version, version))];
            UnsafeCodeMapEvidence[] noMapEvidence = [.. applicableEvidence.Where(item => item.ExplicitNoMap)];
            bool noMap = noMapEvidence.Length > 0;
            UnsafeCodeRule[] eligibleRules = [.. matchingRules.Where(rule =>
                Applies(rule.Identity.Version, version) && (!rule.NoMapOnly || noMap))];

            // An ineligible noMapOnly rule supplies neither an exemption nor a positive decision.
            if (eligibleRules.Any(rule =>
                rule.ExcludedVersions.Any(release => string.Equals(release, destinationRelease, StringComparison.Ordinal)) ||
                applicableEvidence.Any(item => item.Relationship is { } relationship && rule.ExcludedRelationships.Contains(relationship))))
            {
                return null;
            }

            bool annotatedNoMap = annotationProvenance.Length > 0 && noMap;
            if (!annotatedNoMap && eligibleRules.Length == 0)
            {
                return null;
            }

            return new(
                identity,
                context,
                new(false, []),
                annotatedNoMap ? annotationProvenance : [],
                annotatedNoMap || eligibleRules.Any(rule => rule.NoMapOnly)
                    ? NormalizeProvenance(noMapEvidence.Select(item => item.Provenance))
                    : [],
                [.. eligibleRules.Select(rule => new UnsafeCodeRuleProvenance(rule.SourceRelativeFilename, rule.EntryIndex))
                    .Distinct()
                    .OrderBy(item => item.SourceRelativeFilename, StringComparer.Ordinal)
                    .ThenBy(item => item.EntryIndex)]);
        }
    }

    private static bool Applies(string? sourceVersion, string? version) =>
        sourceVersion is null || (version is not null && string.Equals(sourceVersion, version, StringComparison.Ordinal));

    private static ImmutableArray<UnsafeCodeProvenance> NormalizeProvenance(IEnumerable<UnsafeCodeProvenance> provenance) =>
        [.. provenance.Distinct()
            .OrderBy(item => item.SourceRelativeFilename, StringComparer.Ordinal)
            .ThenBy(item => item.ConceptMapId, StringComparer.Ordinal)
            .ThenBy(item => item.ConceptMapUrl, StringComparer.Ordinal)
            .ThenBy(item => item.GroupIndex)
            .ThenBy(item => item.SourceIndex)
            .ThenBy(item => item.TargetIndex)];
}
