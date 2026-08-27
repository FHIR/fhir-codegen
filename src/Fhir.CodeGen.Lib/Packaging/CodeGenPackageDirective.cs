// <copyright file="CodeGenPackageDirective.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>A parsed package directive, expressed in terms this project owns.</summary>
public record class CodeGenPackageDirective
{
    /// <summary>Gets the directive as requested, with the FHIR <c>#</c> separator normalized to <c>@</c>.</summary>
    public required string RawDirective { get; init; }

    /// <summary>Gets the package identifier, or null when the directive could not be parsed.</summary>
    public string? PackageId { get; init; }

    /// <summary>Gets the version as requested, which may be a literal, a range, or a moving token.</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>Gets the concrete version this directive resolved to, when it is known.</summary>
    public string? ResolvedVersion { get; init; }

    /// <summary>Gets a value indicating whether the requested version is an exact literal.</summary>
    public bool IsExactVersion { get; init; }

    /// <summary>Gets a value indicating whether the package name already carries a FHIR release suffix.</summary>
    public bool IsGuideWithFhirSuffix { get; init; }

    /// <summary>Gets the resolved <c>id@version</c> directive, or null when the version is not yet known.</summary>
    public string? NpmDirective => ((PackageId is null) || string.IsNullOrEmpty(ResolvedVersion))
        ? null
        : PackageId + "@" + ResolvedVersion;

    /// <summary>Gets the most specific directive available, falling back to the requested text.</summary>
    public string AnyDirective => ((PackageId is null) || string.IsNullOrEmpty(RequestedVersion))
        ? RawDirective
        : PackageId + "@" + RequestedVersion;
}
