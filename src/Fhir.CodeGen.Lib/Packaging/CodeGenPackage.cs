// <copyright file="CodeGenPackage.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>An installed FHIR package, as this project consumes it.</summary>
/// <remarks>
/// Deliberately carries no cache, registry, or version type - this is the boundary record that
/// keeps package-management implementation details out of the rest of the solution.
/// </remarks>
public record class CodeGenPackage
{
    /// <summary>Gets the resolved identity of the package.</summary>
    public required PackageIdentity Identity { get; init; }

    /// <summary>Gets the package manifest.</summary>
    public required CodeGenPackageManifest Manifest { get; init; }

    /// <summary>Gets the package content listing.</summary>
    public required CodeGenPackageIndex Index { get; init; }

    /// <summary>Gets the directory holding the package content (resource) files.</summary>
    public required string ContentPath { get; init; }

    /// <summary>Gets the root directory of the installed package.</summary>
    public required string RootPath { get; init; }
}
