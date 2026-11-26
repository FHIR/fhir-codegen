// <copyright file="UnsafeCodeBindingScopeResolver.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;

namespace Fhir.CodeGen.Comparison.Terminology;

internal sealed record UnsafeCodeBindingMember(UnsafeCodeCanonical Identity, string? Code);

internal sealed class UnsafeCodeBindingScope(
    UnsafeCodeCanonical directCanonical,
    ImmutableHashSet<UnsafeCodeCanonical> canonicals,
    ImmutableHashSet<UnsafeCodeCanonical> importedCanonicals,
    ImmutableArray<UnsafeCodeBindingMember> members,
    ImmutableDictionary<UnsafeCodeCanonical, ImmutableArray<UnsafeCodeBindingMember>> scopedMembers,
    bool required,
    bool complete)
{
    internal UnsafeCodeCanonical DirectCanonical { get; } = directCanonical;
    internal ImmutableHashSet<UnsafeCodeCanonical> Canonicals { get; } = canonicals;
    internal ImmutableHashSet<UnsafeCodeCanonical> ImportedCanonicals { get; } = importedCanonicals;
    internal ImmutableArray<UnsafeCodeBindingMember> Members { get; } = members;
    internal ImmutableDictionary<UnsafeCodeCanonical, ImmutableArray<UnsafeCodeBindingMember>> ScopedMembers { get; } = scopedMembers;
    internal bool Required { get; } = required;
    internal bool Complete { get; } = complete;
}

internal sealed class UnsafeCodeBindingScopeResolver(
    IReadOnlyDictionary<int, DbValueSet> valueSets,
    Func<int, IEnumerable<DbValueSetConcept>> concepts)
{
    private readonly Dictionary<int, UnsafeCodeBindingScope?> _bindings = [];

    internal UnsafeCodeBindingScope? Resolve(DbElement? bound)
    {
        if (bound is null)
        {
            return null;
        }

        if (_bindings.TryGetValue(bound.Key, out UnsafeCodeBindingScope? cached))
        {
            return cached;
        }

        if (bound.BindingValueSetKey is null && bound.BindingValueSet is null)
        {
            _bindings.Add(bound.Key, null);
            return null;
        }

        UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(bound.BindingValueSet);
        DbValueSet[] resolved = bound.BindingValueSetKey is int key && valueSets.TryGetValue(key, out DbValueSet? keyed) &&
            keyed.FhirPackageKey == bound.FhirPackageKey && (canonical.Url is null || keyed.UnversionedUrl == canonical.Url) &&
            (canonical.Version is null || keyed.Version == canonical.Version)
            ? [keyed]
            : [.. valueSets.Values.Where(row => row.FhirPackageKey == bound.FhirPackageKey && row.UnversionedUrl == canonical.Url &&
                (canonical.Version is null || row.Version == canonical.Version))];
        bool complete = resolved.Length == 1;
        HashSet<UnsafeCodeCanonical> canonicals = [];
        HashSet<UnsafeCodeCanonical> importedCanonicals = [];
        List<UnsafeCodeBindingMember> members = [];
        Dictionary<UnsafeCodeCanonical, ImmutableArray<UnsafeCodeBindingMember>> scopedMembers = [];
        if (canonical.Url is not null)
        {
            canonicals.Add(canonical);
        }

        foreach (DbValueSet valueSet in resolved)
        {
            AddValueSet(valueSet, [], false);
        }

        UnsafeCodeCanonical direct = resolved.Length == 1 && canonical.Version is null
            ? new(resolved[0].UnversionedUrl, resolved[0].Version) : canonical;
        UnsafeCodeBindingScope scope = new(direct, canonicals.ToImmutableHashSet(), importedCanonicals.ToImmutableHashSet(),
            [.. members], scopedMembers.ToImmutableDictionary(), bound.ValueSetBindingStrength == BindingStrength.Required, complete);
        _bindings.Add(bound.Key, scope);
        return scope;

        void AddValueSet(DbValueSet valueSet, HashSet<int> active, bool imported)
        {
            if (!active.Add(valueSet.Key))
            {
                complete = false;
                return;
            }

            UnsafeCodeCanonical valueSetCanonical = new(valueSet.UnversionedUrl, valueSet.Version);
            canonicals.Add(valueSetCanonical);
            if (imported)
            {
                importedCanonicals.Add(valueSetCanonical);
            }

            int firstMember = members.Count;
            if (valueSet.CanExpand)
            {
                foreach (DbValueSetConcept concept in concepts(valueSet.Key))
                {
                    members.Add(new(new(concept.System, string.IsNullOrEmpty(concept.SystemVersion) ? null : concept.SystemVersion), concept.Code));
                }
            }
            else if (valueSet.Compose is { } compose)
            {
                if (compose.Exclude.Count > 0)
                {
                    complete = false;
                }

                foreach (ValueSet.ConceptSetComponent include in compose.Include)
                {
                    if (include.System is not null)
                    {
                        if (include.Concept.Count == 0)
                        {
                            members.Add(new(new(include.System, include.Version), null));
                        }
                        else
                        {
                            foreach (ValueSet.ConceptReferenceComponent concept in include.Concept)
                            {
                                members.Add(new(new(include.System, include.Version), concept.Code));
                            }
                        }
                    }

                    foreach (string import in include.ValueSet)
                    {
                        UnsafeCodeCanonical importedCanonical = UnsafeCodeCanonical.Parse(import);
                        DbValueSet[] imports = [.. valueSets.Values.Where(row => row.FhirPackageKey == valueSet.FhirPackageKey &&
                            row.UnversionedUrl == importedCanonical.Url && (importedCanonical.Version is null || row.Version == importedCanonical.Version))];
                        if (imports.Length != 1)
                        {
                            complete = false;
                        }

                        foreach (DbValueSet importedValueSet in imports)
                        {
                            AddValueSet(importedValueSet, active, true);
                        }
                    }
                }
            }
            else
            {
                complete = false;
            }

            scopedMembers[valueSetCanonical] = [.. members.Skip(firstMember)];
            active.Remove(valueSet.Key);
        }
    }
}
