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
}
