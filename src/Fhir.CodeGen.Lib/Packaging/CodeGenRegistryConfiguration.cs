// <copyright file="CodeGenRegistryConfiguration.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Generic;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>The kind of registry an endpoint describes.</summary>
/// <remarks>A codegen-owned mirror of the package library's registry-type enumeration.</remarks>
internal enum CodeGenRegistryKind
{
    /// <summary>A FHIR NPM package registry.</summary>
    FhirNpm,

    /// <summary>A FHIR CI build registry.</summary>
    FhirCiBuild,

    /// <summary>A FHIR HTTP endpoint that serves packages.</summary>
    FhirHttp,

    /// <summary>A standard NPM registry.</summary>
    Npm,
}

/// <summary>A single registry endpoint in the resolved chain.</summary>
/// <param name="Url">The endpoint URL.</param>
/// <param name="Kind">The kind of registry the endpoint serves.</param>
internal readonly record struct CodeGenRegistryEndpoint(string Url, CodeGenRegistryKind Kind);

/// <summary>The registry chain resolved from the root configuration.</summary>
/// <remarks>
/// This type exists so the registry decision can be described - and tested - without naming a
/// package-library type outside <see cref="FhirPkgPackageSource"/>.
/// </remarks>
internal sealed record class CodeGenRegistryConfiguration
{
    /// <summary>Gets the resolved registry endpoints, in the order they should be queried.</summary>
    public IReadOnlyList<CodeGenRegistryEndpoint> Endpoints { get; init; } = [];

    /// <summary>Gets a value indicating whether the FHIR CI build registry participates.</summary>
    public bool IncludeCiBuilds { get; init; } = false;

    /// <summary>Gets a value indicating whether the HL7 website fallback participates.</summary>
    public bool IncludeHl7WebsiteFallback { get; init; } = false;

    /// <summary>Gets a value indicating whether every registry has been disabled.</summary>
    /// <remarks>
    /// When this is <see langword="true"/> the package source resolves from the cache only. An
    /// empty endpoint list is not sufficient on its own: the package library treats an empty list
    /// as "use the built-in published chain".
    /// </remarks>
    public bool RegistriesDisabled => (Endpoints.Count == 0) && !IncludeCiBuilds && !IncludeHl7WebsiteFallback;
}
