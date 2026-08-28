// <copyright file="PackageVersionMatcher.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using FhirPkg.Models;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>Determines whether a resolved package version satisfies a requested version.</summary>
internal static class PackageVersionMatcher
{
    /// <summary>Tests whether <paramref name="candidateVersion"/> satisfies <paramref name="requestedVersion"/>.</summary>
    /// <param name="candidateVersion">The resolved version of a loaded package.</param>
    /// <param name="requestedVersion">The requested version, which may be a range or a wildcard.</param>
    /// <returns>True when the candidate satisfies the request; otherwise false. Never throws.</returns>
    public static bool Satisfies(string candidateVersion, string requestedVersion)
    {
        if (string.IsNullOrEmpty(candidateVersion) || string.IsNullOrEmpty(requestedVersion))
        {
            return false;
        }

        if (string.Equals(candidateVersion, requestedVersion, StringComparison.Ordinal))
        {
            return true;
        }

        if (TryMatchWildcard(candidateVersion, requestedVersion, out bool wildcardMatch))
        {
            return wildcardMatch;
        }

        if (!FhirSemVer.TryParse(candidateVersion, out FhirSemVer? candidate) || (candidate is null))
        {
            return false;
        }

        try
        {
            return candidate.Satisfies(requestedVersion);
        }
        catch (Exception)
        {
            // moving tokens (`latest`, `current`, `dev`) and unorderable wildcards are not a match
            return false;
        }
    }

    /// <summary>Orders two resolved versions.</summary>
    /// <param name="left"> The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>A negative value, zero, or a positive value, as with <see cref="IComparable{T}"/>.</returns>
    public static int Compare(string left, string right)
    {
        if (FhirSemVer.TryParse(left, out FhirSemVer? parsedLeft) &&
            FhirSemVer.TryParse(right, out FhirSemVer? parsedRight) &&
            (parsedLeft is not null) &&
            (parsedRight is not null))
        {
            try
            {
                return parsedLeft.CompareTo(parsedRight);
            }
            catch (Exception)
            {
                // moving tokens are not orderable; fall through to the ordinal comparison
            }
        }

        return string.CompareOrdinal(left, right);
    }

    /// <summary>
    /// Matches the dotted numeric-and-wildcard forms the replaced version type accepted, where an
    /// omitted or wildcard component matches anything: <c>*</c>, <c>4</c>, <c>4.0</c>, <c>4.x</c>,
    /// and <c>4.0.x</c>. The upstream range grammar rejects the shorter of these.
    /// </summary>
    /// <param name="candidateVersion">The resolved version of a loaded package.</param>
    /// <param name="requestedVersion">The requested version.</param>
    /// <param name="isMatch">[out] The result, when the request is one of these forms.</param>
    /// <returns>True when the request is one of these forms; otherwise false.</returns>
    private static bool TryMatchWildcard(string candidateVersion, string requestedVersion, out bool isMatch)
    {
        isMatch = false;

        string[] requestedParts = requestedVersion.Split('.');
        if (requestedParts.Length > 3)
        {
            return false;
        }

        int?[] requested = [null, null, null];
        bool sawWildcard = false;

        for (int i = 0; i < requestedParts.Length; i++)
        {
            if (IsWildcard(requestedParts[i]))
            {
                sawWildcard = true;
                continue;
            }

            if (!int.TryParse(requestedParts[i], out int value) || (value < 0))
            {
                return false;
            }

            requested[i] = value;
        }

        if (!sawWildcard && (requestedParts.Length == 3))
        {
            // fully specified - leave it to the ordinal comparison and the upstream parser
            return false;
        }

        string[] candidateParts = candidateVersion.Split(['-', '+'], 2)[0].Split('.');

        for (int i = 0; i < requested.Length; i++)
        {
            if (requested[i] is null)
            {
                continue;
            }

            if ((i >= candidateParts.Length) ||
                !int.TryParse(candidateParts[i], out int candidateValue) ||
                (candidateValue != requested[i]))
            {
                return true;
            }
        }

        isMatch = true;
        return true;
    }

    /// <summary>Tests whether a version component is a wildcard.</summary>
    /// <param name="component">The component.</param>
    /// <returns>True when the component matches any value.</returns>
    private static bool IsWildcard(string component) =>
        component.Length == 0 ||
        component.Equals("*", StringComparison.Ordinal) ||
        component.Equals("x", StringComparison.OrdinalIgnoreCase);
}
