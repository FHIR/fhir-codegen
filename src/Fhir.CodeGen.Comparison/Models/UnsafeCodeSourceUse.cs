// <copyright file="UnsafeCodeSourceUse.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;

namespace Fhir.CodeGen.Comparison.Models;

internal enum UnsafeCodeQualificationKind
{
    Typed,
    Primitive,
}

internal sealed record UnsafeCodeSourceUse(
    ImmutableArray<UnsafeCodePathStep> OccurrencePath,
    string SourceTypeName,
    string TypeUseToken,
    string RootElementId,
    UnsafeCodeQualificationKind QualificationKind)
{
    internal ImmutableArray<UnsafeCodePathStep> SelectedPath =>
        OccurrencePath.Add(new(RootElementId, TypeUseToken, false));
}

internal sealed record UnsafeCodeRootFact(int StructureKey, string ElementId, bool IsResource);

internal sealed record UnsafeCodeTypeFact(
    int? RowKey,
    string SourceTypeName,
    string TypeUseToken,
    string Literal,
    ImmutableArray<UnsafeCodeRootFact> Roots);

internal sealed record UnsafeCodeEffectiveTypeFacts(
    ImmutableArray<UnsafeCodeTypeFact> Types,
    ImmutableArray<string> Declarations,
    int DeclaredCount)
{
    internal ImmutableArray<string> TypeNames =>
        [.. Types.Select(type => type.SourceTypeName).Concat(Declarations.Select(Name)).Distinct(StringComparer.Ordinal)];

    internal bool Complete => DeclaredCount == Declarations.Length && DeclaredCount > 0 &&
        Declarations.Distinct(StringComparer.Ordinal).Count() == Declarations.Length &&
        Types.Length > 0 && Types.Select(type => type.TypeUseToken).Distinct(StringComparer.Ordinal).Count() == Types.Length &&
        Declarations.All(declaration => Types.Any(type => type.Literal == declaration || type.SourceTypeName == declaration)) &&
        Types.All(type => Declarations.Any(declaration => declaration == type.Literal || declaration == type.SourceTypeName) &&
            type.Roots.Length == 1 && !type.Roots[0].IsResource);

    private static string Name(string literal) => literal.Split('[', '(')[0];
}

internal sealed record UnsafeCodeSourceUseNormalization(
    ImmutableArray<UnsafeCodePathStep> OccurrencePath,
    UnsafeCodeSourceUse? SourceUse,
    string? Failure);

internal static class UnsafeCodeSourceUseNormalizer
{
    internal static bool IsEndpointCandidate(UnsafeCodePathStep step, UnsafeCodeEffectiveTypeFacts? facts = null) =>
        step.TypeName is not null && (!step.ElementId.Contains('.', StringComparison.Ordinal) ||
            facts?.Types.Any(type => type.Roots.Any(root => root.ElementId == step.ElementId)) == true);

    internal static UnsafeCodeSourceUseNormalization Normalize(
        ImmutableArray<UnsafeCodePathStep> path,
        IReadOnlyDictionary<int, UnsafeCodeEffectiveTypeFacts> precedingFacts)
    {
        for (int i = 0; i < path.Length; i++)
        {
            UnsafeCodePathStep step = path[i];
            UnsafeCodeEffectiveTypeFacts? facts = precedingFacts.GetValueOrDefault(i);
            if (!IsEndpointCandidate(step, facts))
            {
                continue;
            }

            if (i == 0 || i != path.Length - 1 || step.IsContentReference)
            {
                return new(path, null, "InvalidSourceUse: datatype value endpoint must be terminal, unique and non-reference");
            }

            UnsafeCodeTypeFact[] declared = [.. facts?.Types.Where(type => type.TypeUseToken == step.TypeName) ?? []];
            if (facts?.Complete != true || declared.Length != 1 || declared[0].Roots.Length != 1 ||
                declared[0].Roots[0].IsResource || declared[0].Roots[0].ElementId != step.ElementId)
            {
                return new(path, null, "InvalidSourceUse: missing, ambiguous or undeclared datatype root");
            }

            UnsafeCodeTypeFact type = declared[0];
            UnsafeCodeSourceUse use = new(path[..i], type.SourceTypeName, type.TypeUseToken, step.ElementId,
                type.SourceTypeName == "code" ? UnsafeCodeQualificationKind.Primitive : UnsafeCodeQualificationKind.Typed);
            return new(use.OccurrencePath, use, null);
        }

        return new(path, null, null);
    }
}
