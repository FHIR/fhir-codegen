// <copyright file="UnsafeCodePolicyModels.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using Hl7.Fhir.Model;

namespace Fhir.CodeGen.Comparison.Models;

internal static class UnsafeCodeConstants
{
    internal const string PropertySystem = "http://hl7.org/fhir/uv/xver/ValueSet/xver-conceptmap-properties";
    internal const string PropertyCode = "requires-omitted-mandatory-code";
    internal const string ImplicitRules = "https://hl7.org/fhir/implicit-rules/omitted-mandatory-interversion-code";
    internal const int InputFormatVersion = 1;
    internal const int OutcomeFormatVersion = 1;
}

internal sealed record UnsafeCodeIdentity(string System, string? Version, string Code);

internal sealed record UnsafeCodeCanonical(string? Url, string? Version)
{
    internal static UnsafeCodeCanonical Parse(string? value)
    {
        int separator = value?.IndexOf('|') ?? -1;
        return separator < 0
            ? new(value, null)
            : new(value![..separator], value[(separator + 1)..]);
    }
}

internal sealed record UnsafeCodeProvenance(
    string SourceRelativeFilename,
    string? ConceptMapId,
    string? ConceptMapUrl,
    int GroupIndex,
    int SourceIndex,
    int? TargetIndex);

internal sealed record UnsafeCodeMappingContext(
    int SourceFhirPackageKey,
    int TargetFhirPackageKey,
    string SourceElementId,
    string TargetElementId);

internal sealed record UnsafeCodeRule(
    UnsafeCodeIdentity Identity,
    bool NoMapOnly,
    ImmutableHashSet<ConceptMap.ConceptMapRelationship> ExcludedRelationships,
    ImmutableHashSet<string> ExcludedVersions,
    string SourceRelativeFilename,
    int EntryIndex);

internal sealed record UnsafeCodeElementAnnotation(
    UnsafeCodeMappingContext Context,
    UnsafeCodeProvenance Provenance);

internal sealed record UnsafeCodeMapEvidence(
    int SourceFhirPackageKey,
    int TargetFhirPackageKey,
    UnsafeCodeCanonical SourceValueSet,
    UnsafeCodeCanonical TargetValueSet,
    UnsafeCodeCanonical SourceSystem,
    string? SourceCode,
    UnsafeCodeCanonical TargetSystem,
    string? TargetCode,
    ConceptMap.ConceptMapRelationship? Relationship,
    bool ExplicitNoMap,
    UnsafeCodeProvenance Provenance);

// Paths and version matches are serialized losslessly at the persistence boundary, never as FHIRPath.
internal sealed record UnsafeCodePathStep(string ElementId, string? TypeName, bool IsContentReference);

internal sealed record UnsafeCodeVersionMatch(bool IsWildcard, ImmutableArray<string> Versions);

internal sealed record UnsafeCodeTrigger(
    int StructureOutcomeKey,
    UnsafeCodeMappingContext Context,
    ImmutableArray<UnsafeCodePathStep> SourcePath,
    UnsafeCodeIdentity Identity,
    UnsafeCodeVersionMatch VersionMatch,
    ImmutableArray<int> EvidenceKeys,
    ImmutableArray<int> RuleKeys);
