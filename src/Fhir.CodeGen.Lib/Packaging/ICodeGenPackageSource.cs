// <copyright file="ICodeGenPackageSource.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>Supplies installed FHIR packages to this project.</summary>
/// <remarks>
/// The implementation of this interface is the only place in the solution permitted to name a
/// package-management library type; everything above it works in terms of
/// <see cref="CodeGenPackage"/>.
/// </remarks>
internal interface ICodeGenPackageSource : IDisposable
{
    /// <summary>Resolves a package directive, installing the package if it is not already present.</summary>
    /// <param name="directive">The package directive to resolve, for example <c>hl7.fhir.r4.core#4.0.1</c>.</param>
    /// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
    /// <returns>The installed package, or <see langword="null"/> when it could not be resolved.</returns>
    Task<CodeGenPackage?> GetOrInstallAsync(string directive, CancellationToken cancellationToken = default);
}
