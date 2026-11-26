// <copyright file="PackageIdentity.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>The resolved identity of a loaded FHIR package.</summary>
/// <remarks>
/// This is the key type for <c>DefinitionCollection.Manifests</c> and
/// <c>DefinitionCollection.ContentListings</c>. Several language exporters interpolate the key
/// directly into generated output, so <see cref="ToString"/> is part of this project's contract
/// with downstream consumers and must not be reformatted. Ordering is required because
/// <c>LangInfo</c> sorts the keys before rendering them.
/// </remarks>
/// <param name="Id">The package identifier, for example <c>hl7.fhir.r4.core</c>.</param>
/// <param name="Version">The resolved version string, verbatim, for example <c>4.0.1</c>.</param>
public readonly record struct PackageIdentity(string Id, string Version) : IComparable<PackageIdentity>
{
    /// <summary>Renders the identity in the form generated artifacts embed: <c>(Id, Version)</c>.</summary>
    public override string ToString() => $"({Id}, {Version})";

    /// <inheritdoc/>
    public int CompareTo(PackageIdentity other)
    {
        int byId = Comparer<string>.Default.Compare(Id, other.Id);

        return byId != 0
            ? byId
            : Comparer<string>.Default.Compare(Version, other.Version);
    }
}
