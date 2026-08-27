// <copyright file="PackageVersionMatcher.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;

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
        // TODO: delegate range and wildcard matching to the package library's version parser.
        return string.Equals(candidateVersion, requestedVersion, StringComparison.Ordinal);
    }
}
