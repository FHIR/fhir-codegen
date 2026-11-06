// <copyright file="UnsafeCodeEvidenceApplicability.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using Fhir.CodeGen.Comparison.Models;

namespace Fhir.CodeGen.Comparison.Terminology;

internal enum UnsafeCodeEndpointMatchOrigin
{
    None,
    DirectBinding,
    ImportedScope,
}

internal sealed record UnsafeCodeProjectionMatch(
    UnsafeCodeMapEvidence Evidence,
    UnsafeCodeEndpointMatchOrigin SourceOrigin,
    UnsafeCodeEndpointMatchOrigin TargetOrigin)
{
    internal bool IsMatch => SourceOrigin != UnsafeCodeEndpointMatchOrigin.None && TargetOrigin != UnsafeCodeEndpointMatchOrigin.None;
}

internal static class UnsafeCodeEvidenceApplicability
{
    internal static UnsafeCodeMapEvidence QualifyEvidence(UnsafeCodeMapEvidence evidence, UnsafeCodeBindingScope? source)
    {
        if (evidence.SourceSystem.Url is null && evidence.SourceCode is string code && source?.Complete == true)
        {
            ImmutableArray<UnsafeCodeCanonical> identities = Identities(source, code, evidence.SourceValueSet);
            if (identities.Length == 1)
            {
                return evidence with { SourceSystem = identities[0] };
            }
        }

        return evidence;
    }

    internal static bool IsPrecise(UnsafeCodeMapEvidence evidence, UnsafeCodeBindingScope? source, UnsafeCodeBindingScope? target) =>
        ScopeMatches(evidence.SourceValueSet, source) && ScopeMatches(evidence.TargetValueSet, target) &&
        (source is null || evidence.SourceSystem.Url is null || Allows(source, evidence.SourceSystem, evidence.SourceCode, evidence.SourceValueSet)) &&
        (evidence.TargetSystem.Url is null || (target is not null && Allows(target, evidence.TargetSystem, evidence.TargetCode, evidence.TargetValueSet)));

    internal static bool IsPotential(UnsafeCodeMapEvidence evidence, UnsafeCodeBindingScope? source, UnsafeCodeBindingScope? target) =>
        MayMatch(evidence.SourceValueSet, source) && MayMatch(evidence.TargetValueSet, target) &&
        (source is null || !source.Complete || evidence.SourceSystem.Url is null || Allows(source, evidence.SourceSystem, evidence.SourceCode, evidence.SourceValueSet)) &&
        (target is null || !target.Complete || evidence.TargetSystem.Url is null || Allows(target, evidence.TargetSystem, evidence.TargetCode, evidence.TargetValueSet));

    internal static bool MayMatch(UnsafeCodeCanonical canonical, UnsafeCodeBindingScope? binding)
    {
        if (canonical.Url is null || binding is null)
        {
            return true;
        }

        UnsafeCodeCanonical[] scopes = [.. binding.Canonicals.Where(scope => scope.Url == canonical.Url)];
        return scopes.Length > 0 && (canonical.Version is null ||
            scopes.All(scope => scope.Version is null) || scopes.Any(scope => scope.Version == canonical.Version));
    }

    internal static bool ScopeMatches(UnsafeCodeCanonical canonical, UnsafeCodeBindingScope? binding) =>
        canonical.Url is null || (binding is not null && binding.Canonicals.Any(scope =>
            scope.Url == canonical.Url && (canonical.Version is null || scope.Version == canonical.Version)));

    internal static IEnumerable<UnsafeCodeBindingMember> MembersFor(UnsafeCodeBindingScope binding, UnsafeCodeCanonical? scope) =>
        scope?.Url is null ? binding.Members : binding.ScopedMembers.Where(entry => entry.Key.Url == scope.Url &&
            (scope.Version is null || entry.Key.Version == scope.Version)).SelectMany(entry => entry.Value);

    internal static ImmutableArray<UnsafeCodeCanonical> Identities(UnsafeCodeBindingScope binding, string code, UnsafeCodeCanonical? scope = null) =>
        [.. MembersFor(binding, scope).Where(member => member.Code is null || member.Code == code).Select(member => member.Identity).Distinct()];

    internal static bool Allows(UnsafeCodeBindingScope binding, UnsafeCodeCanonical identity, string? code, UnsafeCodeCanonical scope) =>
        MembersFor(binding, scope).Any(member => member.Identity.Url == identity.Url && (code is null || member.Code is null || member.Code == code) &&
            (identity.Version is null || member.Identity.Version is null || member.Identity.Version == identity.Version));

    internal static UnsafeCodeProjectionMatch MatchProjection(
        UnsafeCodeMapEvidence evidence,
        UnsafeCodeCanonical sourceDirect,
        UnsafeCodeCanonical targetDirect,
        UnsafeCodeBindingScope? source,
        UnsafeCodeBindingScope? target)
    {
        if (evidence.ExplicitNoMap || evidence.SourceCode is null || evidence.TargetCode is null)
        {
            return new(evidence, UnsafeCodeEndpointMatchOrigin.None, UnsafeCodeEndpointMatchOrigin.None);
        }

        // Preserve the authored identity (including wildcard qualification) for direct-map safety checks.
        UnsafeCodeMapEvidence qualified = DirectMatches(evidence.SourceValueSet, sourceDirect) ? evidence : QualifyEvidence(evidence, source);
        return new(qualified,
            MatchEndpoint(qualified.SourceValueSet, qualified.SourceSystem, qualified.SourceCode!, sourceDirect, source),
            MatchEndpoint(qualified.TargetValueSet, qualified.TargetSystem, qualified.TargetCode!, targetDirect, target));
    }

    private static UnsafeCodeEndpointMatchOrigin MatchEndpoint(
        UnsafeCodeCanonical canonical,
        UnsafeCodeCanonical system,
        string code,
        UnsafeCodeCanonical direct,
        UnsafeCodeBindingScope? binding)
    {
        // Direct authored maps remain a conservative safety check, not a membership proof.
        if (DirectMatches(canonical, direct))
        {
            return UnsafeCodeEndpointMatchOrigin.DirectBinding;
        }

        if (canonical.Url is not null && system.Url is not null && binding is not null &&
            binding.ImportedCanonicals.Any(scope => scope.Url == canonical.Url && (canonical.Version is null || scope.Version == canonical.Version) &&
                Allows(binding, system, code, scope)))
        {
            return UnsafeCodeEndpointMatchOrigin.ImportedScope;
        }

        return UnsafeCodeEndpointMatchOrigin.None;
    }

    private static bool DirectMatches(UnsafeCodeCanonical canonical, UnsafeCodeCanonical direct) =>
        canonical.Url is not null && direct.Url == canonical.Url && (canonical.Version is null || direct.Version == canonical.Version);
}
