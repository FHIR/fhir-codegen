// <copyright file="UnsafeCodeConfigurationLoader.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;

namespace Fhir.CodeGen.Comparison.CrossVersionSource;

internal static class UnsafeCodeConfigurationLoader
{
    internal static ImmutableArray<UnsafeCodeRule> Load(string sourceRoot)
    {
        string filename = Path.Combine(sourceRoot, "input", "ig-support", "unsafe-code-mappings.json");
        try
        {
            using FileStream stream = File.OpenRead(filename);
            return Parse(stream, filename, Path.GetRelativePath(sourceRoot, filename));
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error(filename, null, "$", "cannot read configuration", exception);
        }
    }

    internal static ImmutableArray<UnsafeCodeRule> Parse(Stream stream, string filename, string? sourceRelativeFilename = null)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Error(filename, null, "$", "expected an array");
            }

            Dictionary<UnsafeCodeIdentity, UnsafeCodeRule> rules = [];
            int index = 0;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                string path = $"$[{index}]";
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw Error(filename, index, path, "expected an object");
                }

                HashSet<string> members = new(StringComparer.Ordinal);
                foreach (JsonProperty property in entry.EnumerateObject())
                {
                    if (!members.Add(property.Name))
                    {
                        throw Error(filename, index, path + "." + property.Name, "duplicate object property");
                    }

                    if (property.Name is not ("system" or "version" or "code" or "noMapOnly" or "excludedRelationship" or "excludedVersion"))
                    {
                        throw Error(filename, index, path + "." + property.Name, "unknown property");
                    }
                }

                string system = RequiredString(entry, "system", filename, index);
                if (!Uri.IsWellFormedUriString(system, UriKind.Absolute) || system.Contains('|') || system.Any(char.IsWhiteSpace))
                {
                    throw Error(filename, index, path + ".system", "expected a valid absolute system identifier");
                }

                string code = RequiredString(entry, "code", filename, index);
                string? version = members.Contains("version") ? RequiredString(entry, "version", filename, index) : null;
                bool noMapOnly = false;
                if (entry.TryGetProperty("noMapOnly", out JsonElement noMap))
                {
                    if (noMap.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw Error(filename, index, path + ".noMapOnly", "expected a boolean");
                    }

                    noMapOnly = noMap.GetBoolean();
                }

                ImmutableHashSet<ConceptMap.ConceptMapRelationship>.Builder relationships = ImmutableHashSet.CreateBuilder<ConceptMap.ConceptMapRelationship>();
                foreach ((string value, int item) in StringArray(entry, "excludedRelationship", filename, index))
                {
                    ConceptMap.ConceptMapRelationship? relationship = EnumUtility.ParseLiteral<ConceptMap.ConceptMapRelationship>(value);
                    if (relationship is null)
                    {
                        throw Error(filename, index, path + $".excludedRelationship[{item}]", $"invalid relationship literal '{value}'");
                    }

                    relationships.Add(relationship.Value);
                }

                ImmutableHashSet<string>.Builder versions = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                foreach ((string value, int item) in StringArray(entry, "excludedVersion", filename, index))
                {
                    if (!Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+\.[0-9]+\z") ||
                        !FhirReleases.TryGetSequence(value, out FhirReleases.FhirSequenceCodes sequence) ||
                        sequence == FhirReleases.FhirSequenceCodes.Unknown)
                    {
                        throw Error(filename, index, path + $".excludedVersion[{item}]", $"invalid full FHIR release literal '{value}'");
                    }

                    versions.Add(value);
                }

                UnsafeCodeRule rule = new(new(system, version, code), noMapOnly, relationships.ToImmutable(), versions.ToImmutable(), sourceRelativeFilename ?? filename, index);
                if (rules.TryGetValue(rule.Identity, out UnsafeCodeRule? previous))
                {
                    if (previous.NoMapOnly != rule.NoMapOnly ||
                        !previous.ExcludedRelationships.SetEquals(rule.ExcludedRelationships) ||
                        !previous.ExcludedVersions.SetEquals(rule.ExcludedVersions))
                    {
                        throw Error(filename, index, path, $"contradictory entry for the same system/version/code identity as entry {previous.EntryIndex}");
                    }
                }
                else
                {
                    rules.Add(rule.Identity, rule);
                }

                index++;
            }

            return [.. rules.Values];
        }
        catch (JsonException exception)
        {
            string path = exception.Path ?? "$";
            int? index = null;
            int close = path.IndexOf(']');
            if (path.StartsWith("$[", StringComparison.Ordinal) && close > 2 && int.TryParse(path[2..close], out int parsed))
            {
                index = parsed;
            }

            throw Error(filename, index, path, "malformed JSON: " + exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error(filename, null, "$", "cannot read configuration", exception);
        }
    }

    private static string RequiredString(JsonElement entry, string name, string filename, int index)
    {
        if (!entry.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error(filename, index, $"$[{index}].{name}", "expected a nonblank string");
        }

        return value.GetString()!;
    }

    private static IEnumerable<(string Value, int Index)> StringArray(JsonElement entry, string name, string filename, int index)
    {
        if (!entry.TryGetProperty(name, out JsonElement array))
        {
            yield break;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Error(filename, index, $"$[{index}].{name}", "expected an array");
        }

        int item = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Error(filename, index, $"$[{index}].{name}[{item}]", "expected a nonblank string");
            }

            yield return (value.GetString()!, item);
            item++;
        }
    }

    private static InvalidDataException Error(string filename, int? index, string path, string reason, Exception? inner = null) =>
        new($"Unsafe-code configuration '{filename}', entry {(index.HasValue ? index.Value.ToString() : "<root>")}, JSON path {path}: {reason}.", inner);
}
