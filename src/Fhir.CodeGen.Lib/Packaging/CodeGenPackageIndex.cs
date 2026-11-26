// <copyright file="CodeGenPackageIndex.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Generic;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>The listing of resource files contained in a package.</summary>
public record class CodeGenPackageIndex
{
    /// <summary>Gets the indexed files.</summary>
    public IReadOnlyList<CodeGenPackageIndexEntry> Files { get; init; } = [];
}

/// <summary>A single entry in a package content listing.</summary>
public record class CodeGenPackageIndexEntry
{
    /// <summary>Gets the bare filename, without path information.</summary>
    public string? Filename { get; init; } = null;

    /// <summary>Gets the path relative to the package root, when the source index supplies one.</summary>
    /// <remarks>
    /// Resolved against the package root rather than the content directory. When this is
    /// <see langword="null"/>, callers fall back to <see cref="Filename"/> resolved against the
    /// content directory.
    /// </remarks>
    public string? RelativePath { get; init; } = null;

    /// <summary>Gets the FHIR resource type of the indexed file.</summary>
    public string? ResourceType { get; init; } = null;
}
