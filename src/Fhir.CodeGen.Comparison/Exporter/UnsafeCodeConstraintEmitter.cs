// <copyright file="UnsafeCodeConstraintEmitter.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;

namespace Fhir.CodeGen.Comparison.Exporter;

internal sealed record UnsafeCodeConstraintPlan(
    DbUnsafeCodeTrigger Row,
    ImmutableArray<UnsafeCodePathStep> SourcePath,
    UnsafeCodeVersionMatch VersionMatch,
    string Provenance,
    string ProfileCanonical);

internal static class UnsafeCodeConstraintEmitter
{
    internal const string Human = "Using an unsafe cross-version code requires the omitted-mandatory-interversion-code implicit rule.";

    internal static void Attach(
        IReadOnlyList<StructureFhirExporter.BuiltDefinition> profiles,
        ProfileRepresentationIndex index,
        IReadOnlyList<UnsafeCodeConstraintPlan> plans,
        string direction)
    {
        if (plans.Count == 0)
        {
            return;
        }

        ILookup<int, UnsafeCodeConstraintPlan> byOutcome = plans.ToLookup(plan => plan.Row.StructureOutcomeKey);
        Dictionary<int, StructureFhirExporter.BuiltDefinition> byKey = profiles.ToDictionary(profile => profile.OutcomeKey);
        foreach (UnsafeCodeConstraintPlan plan in plans.Where(plan => !byKey.ContainsKey(plan.Row.StructureOutcomeKey)))
        {
            throw Diagnostic(plan.ProfileCanonical, direction, plan, "Missing resource profile");
        }

        Dictionary<int, OwnerTransition[]> transitions = profiles.ToDictionary(profile => profile.OutcomeKey, owner =>
            index.Occurrences(owner.OutcomeKey).SelectMany(occurrence => occurrence.Locations)
                .Where(location => ContainsOwner(location.Steps))
                .SelectMany(location => profiles.Where(child => child.SourceId == location.SourceType ||
                    location.SourceType is "Resource" or "DomainResource").Select(child => new OwnerTransition(child.OutcomeKey,
                        Render([new ChoiceStep(owner.Definition.Type), .. location.Steps, new ChoiceStep(child.Definition.Type)]))))
                .Distinct().ToArray());
        ILookup<int, int> predecessors = transitions.SelectMany(owner => owner.Value.Select(edge => (Owner: owner.Key, edge.Target)))
            .ToLookup(edge => edge.Target, edge => edge.Owner);
        HashSet<int> relevant = Reachable(plans.Select(plan => plan.Row.StructureOutcomeKey), key => predecessors[key]);

        foreach (StructureFhirExporter.BuiltDefinition profile in profiles)
        {
            if (!relevant.Contains(profile.OutcomeKey))
            {
                continue;
            }

            List<(string Identity, string Expression)> constraints = [];
            HashSet<int> reachable = Reachable([profile.OutcomeKey],
                key => transitions[key].Select(edge => edge.Target).Where(relevant.Contains));
            string[] selectors = [.. reachable.SelectMany(key => transitions[key])
                .Where(edge => reachable.Contains(edge.Target)).Select(edge => edge.Expression)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            // Repeat the finite union of typed owner transitions, not individual
            // cycle trails. Include the initial owner as well as every descendant.
            string owners = selectors.Length == 0 ? "$this" : "($this | $this.repeat(" + string.Join(" | ", selectors) + "))";

            foreach (StructureFhirExporter.BuiltDefinition owner in reachable.Select(key => byKey[key])
                .OrderBy(owner => owner.Definition.Url, StringComparer.Ordinal))
            {
                foreach (IGrouping<string, UnsafeCodeConstraintPlan> group in byOutcome[owner.OutcomeKey]
                    .GroupBy(plan => JsonSerializer.Serialize(plan.SourcePath), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    List<string> predicates = [];
                    foreach (UnsafeCodeConstraintPlan plan in group)
                    {
                        ImmutableArray<RepresentationLocation> locations = index.Resolve(owner.OutcomeKey, plan.SourcePath);
                        if (locations.IsEmpty)
                        {
                            throw Diagnostic(profile.Definition.Url, direction, plan, "Missing");
                        }

                        foreach (RepresentationLocation location in locations)
                        {
                            Validate(profile.Definition, direction, plan, location);
                            predicates.Add(Requirement(location.Steps, location, plan, profile.Definition, direction));
                        }
                    }

                    string identity = JsonSerializer.Serialize(new
                    {
                        Profile = profile.Definition.Url,
                        Direction = direction,
                        Owners = owner.OutcomeKey == profile.OutcomeKey ? Array.Empty<string>() : [owner.Definition.Url],
                        SourcePath = group.First().SourcePath,
                    });
                    // Each representation's implication is equivalent to requiring the
                    // declaration when the union of observable unsafe values is nonempty.
                    string requirement = string.Join(" and ", predicates.Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).Select(predicate => "(" + predicate + ")"));
                    constraints.Add((identity, selectors.Length == 0 ? requirement
                        : owners + ".ofType(" + Identifier(owner.Definition.Type) + ").all(" + requirement + ")"));
                }
            }

            Append(profile.Definition, constraints);
        }
    }

    private sealed record OwnerTransition(int Target, string Expression);

    private static HashSet<int> Reachable(IEnumerable<int> initial, Func<int, IEnumerable<int>> next)
    {
        HashSet<int> visited = [];
        Stack<int> pending = new(initial);
        while (pending.TryPop(out int key))
        {
            if (visited.Add(key))
            {
                foreach (int child in next(key))
                {
                    pending.Push(child);
                }
            }
        }

        return visited;
    }

    internal static void Append(StructureDefinition profile, IEnumerable<(string Identity, string Expression)> constraints)
    {
        ElementDefinition root = profile.Differential.Element[0];
        HashSet<string> keys = root.Constraint.Select(constraint => constraint.Key).ToHashSet(StringComparer.Ordinal);
        foreach (IGrouping<string, (string Identity, string Expression)> group in constraints.GroupBy(item => item.Identity, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string key = Key(group.Key);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Unsafe-code constraint key collision in profile {profile.Url}: {key}.");
            }

            root.Constraint.Add(new()
            {
                Key = key,
                Severity = ConstraintSeverity.Error,
                Human = Human,
                Expression = string.Join(" and ", group.Select(item => item.Expression).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                Source = profile.Url,
            });
        }
    }

    internal static string Key(string semanticIdentity) =>
        "xv-unsafe-code-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(semanticIdentity)))[..32];

    private static void Validate(StructureDefinition profile, string direction, UnsafeCodeConstraintPlan plan, RepresentationLocation location)
    {
        if (location.State != RepresentationState.Observable)
        {
            throw Diagnostic(profile.Url, direction, plan, location.State.ToString());
        }

        foreach (CodeTransformation transformation in location.Transformations.Where(transformation =>
            transformation.SourceCode == plan.Row.Code &&
            (transformation.SourceSystem is null || transformation.SourceSystem == plan.Row.System) &&
            (transformation.SourceVersion is null || plan.VersionMatch.Matches(transformation.SourceVersion))))
        {
            if (transformation.SourceSystem != transformation.TargetSystem ||
                transformation.SourceVersion != transformation.TargetVersion ||
                transformation.SourceCode != transformation.TargetCode)
            {
                throw Diagnostic(profile.Url, direction, plan, "identity-changing native code mapping");
            }
        }
    }

    private static string Requirement(ImmutableArray<RepresentationStep> steps, RepresentationLocation location,
        UnsafeCodeConstraintPlan plan, StructureDefinition profile, string direction)
    {
        int boundary = -1;
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i] is ResourceOwnerStep || steps[i] is RepeatStep repeat && ContainsOwner(repeat.Steps))
            {
                boundary = i;
            }
        }

        string uri = "implicitRules.where($this = " + Literal(UnsafeCodeConstants.ImplicitRules) + ").exists()";
        ImmutableArray<RepresentationStep> valueSteps = boundary < 0 ? steps : steps[(boundary + 1)..];
        string selector = Render(valueSteps);
        CodeCarrierLocation? carrier = location.Carrier;
        if (carrier is null || carrier.Kind == CodeCarrierKind.None)
        {
            throw Diagnostic(profile.Url, direction, plan, "unsupported code carrier");
        }

        if (carrier.Kind == CodeCarrierKind.CodeableConcept)
        {
            selector += ".coding";
            carrier = new(CodeCarrierKind.Coding, [new MemberStep("system")], [new MemberStep("version")], [new MemberStep("code")], false);
        }
        else if (carrier.Kind == CodeCarrierKind.CodeableReference)
        {
            selector += ".concept.coding";
            carrier = new(CodeCarrierKind.Coding, [new MemberStep("system")], [new MemberStep("version")], [new MemberStep("code")], false);
        }

        List<string> conditions = [Equal(carrier.Code, plan.Row.Code)];
        if (!carrier.System.IsDefault)
        {
            conditions.Add(Equal(carrier.System, plan.Row.System));
        }

        if (!plan.VersionMatch.Versions.IsEmpty || !plan.VersionMatch.IsWildcard)
        {
            if (carrier.Version.IsDefault)
            {
                if (plan.Row.SourceVersion is null)
                {
                    throw Diagnostic(profile.Url, direction, plan, "ErasedVersion");
                }

                if (!plan.VersionMatch.Matches(plan.Row.SourceVersion))
                {
                    throw Diagnostic(profile.Url, direction, plan, "incompatible persisted version match");
                }
            }
            else
            {
                string versions = string.Join(" or ", plan.VersionMatch.Versions.Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).Select(version => "$this = " + Literal(version)));
                string match = Render(carrier.Version) + ".where(" + versions + ").exists()";
                conditions.Add(plan.VersionMatch.IsWildcard ? "(" + match + ").not()" : match);
            }
        }

        string unsafeValue = selector + ".where(" + string.Join(" and ", conditions) + ").exists()";
        return boundary < 0
            ? unsafeValue + " implies " + uri
            : Render(steps[..(boundary + 1)]) + ".where(" + unsafeValue + ").all(" + uri + ")";
    }

    private static bool ContainsOwner(ImmutableArray<RepresentationStep> steps) =>
        steps.Any(step => step is ResourceOwnerStep || step is RepeatStep repeat && ContainsOwner(repeat.Steps));

    private static string Equal(ImmutableArray<RepresentationStep> steps, string value) =>
        Render(steps) + ".where($this = " + Literal(value) + ").exists()";

    internal static string Render(ImmutableArray<RepresentationStep> steps)
    {
        string path = "$this";
        foreach (RepresentationStep step in steps)
        {
            path = step switch
            {
                MemberStep member => path + "." + Identifier(member.Name),
                ChoiceStep choice => path + ".ofType(" + Identifier(choice.TypeName) + ")",
                ExtensionStep extension => path + (extension.IsModifier ? ".modifierExtension" : ".extension") +
                    ".where(url = " + Literal(extension.Url) + ")",
                ResourceOwnerStep => path,
                DatatypeStep datatype => path + ".where(extension.where(url = " + Literal(datatype.MetadataUrl) +
                    ").value.ofType(string).where($this = " + Literal(datatype.Name) + ").exists())",
                RepeatStep repeat => repeat.IncludeInitial
                    ? "(" + path + " | " + path + ".repeat(" + Render(repeat.Steps) + "))"
                    : path + ".repeat(" + Render(repeat.Steps) + ")",
                _ => throw new InvalidOperationException($"Unsupported representation step {step.GetType().Name}."),
            };
        }

        return path;
    }

    internal static string Literal(string value) => "'" + Escape(value, '\'') + "'";

    internal static string Identifier(string value) => value.Length > 0 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? value : "`" + Escape(value, '`') + "`";

    private static string Escape(string value, char delimiter)
    {
        StringBuilder escaped = new();
        foreach (char character in value)
        {
            escaped.Append(character switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\f' => "\\f",
                _ when character == delimiter => "\\" + character,
                _ when char.IsControl(character) => "\\u" + ((int)character).ToString("x4"),
                _ => character.ToString(),
            });
        }

        return escaped.ToString();
    }

    private static InvalidOperationException Diagnostic(string canonical, string direction,
        UnsafeCodeConstraintPlan plan, string reason) => new(
        $"Unsafe-code projection failed in profile {canonical}, direction {direction}, source path {JsonSerializer.Serialize(plan.SourcePath)}, " +
        $"qualified code {plan.Row.System}|{plan.Row.SourceVersion ?? "*"}#{plan.Row.Code}, provenance {plan.Provenance}: {reason}. " +
        "Regenerate load, compare, outcomes, and export after resolving the source-value representation.");
}
