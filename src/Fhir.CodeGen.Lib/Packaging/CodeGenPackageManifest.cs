// <copyright file="CodeGenPackageManifest.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>The package manifest fields this project consumes, owned by this repository.</summary>
public record class CodeGenPackageManifest
{
    /// <summary>Gets the package name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the resolved package version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the canonical URL declared by the package.</summary>
    public string? CanonicalUrl { get; init; } = null;

    /// <summary>Gets the web publication URL declared by the package.</summary>
    public string? WebPublicationUrl { get; init; } = null;

    /// <summary>Gets the package title.</summary>
    public string? Title { get; init; } = null;

    /// <summary>Gets the package description.</summary>
    public string? Description { get; init; } = null;

    /// <summary>Gets the declared package type, for example <c>fhir.core</c> or <c>fhir.ig</c>.</summary>
    public string? PackageType { get; init; } = null;

    /// <summary>Gets the FHIR versions this package targets.</summary>
    public IReadOnlyList<string> FhirVersions { get; init; } = [];

    /// <summary>Gets the package dependencies, keyed by package identifier.</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
