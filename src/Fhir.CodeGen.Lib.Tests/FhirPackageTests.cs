// <copyright file="FhirPackageTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json.Nodes;
using Shouldly;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.Packaging;
using Xunit.Abstractions;
using Xunit.Sdk;
using Fhir.CodeGen.Lib.Tests.Extensions;

namespace Fhir.CodeGen.Lib.Tests;

/// <summary>A FHIR package test fixture.</summary>
public class FhirPackageTestBase : IDisposable
{
    public const string? CachePath = null;

    private bool _disposedValue = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="FhirPackageTestBase"/> class.
    /// </summary>
    public FhirPackageTestBase()
    {
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            _disposedValue = true;
        }
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged
    /// resources.
    /// </summary>
    void IDisposable.Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a test fixture for FHIR package tests.
/// </summary>
public class FhirPackageTestsR5 : FhirPackageTestBase
{
    private int _countCodeSystemsByUrl = TestCommon.EntriesR4.Length == 1 ? 448 : 485;              // 448 in core, 485 in +extensions
    private int _countValueSetsByUrl = TestCommon.EntriesR4.Length == 1 ? 827 : 887;
    private int _countPrimitiveTypesByName = 21;
    private int _countComplexTypesByName = 48;
    private int _countResourcesByName = 160;                                                        // 157 concrete resources, 3 abstract
    private int _countLogicalModelsByName = 10;
    private int _countExtensionsByUrl = TestCommon.EntriesR4.Length == 1 ? 0 : 512;                 // 0 in core, 512 in +extensions
    private int _countProfilesByUrl = 66;
    private int _countSearchParametersByUrl = TestCommon.EntriesR4.Length == 1 ? 1244 : 1263;       // 1244 in core, 1263 in +extensions
    private int _countOperationsByUrl = 61;
    private int _countCapabilityStatementsByUrl = 6;
    private int _countImplementationGuidesByUrl = TestCommon.EntriesR4.Length == 1 ? 2 : 3;         // 2 in core, 3 in +extensions
    private int _countCompartmentsByUrl = 6;

    /// <summary>Initializes a new instance of the <see cref="FhirPackageTestsR5"/> class.</summary>
    public FhirPackageTestsR5() : base()
    {
    }

    /// <summary>
    /// Parses the core package.
    /// </summary>
    /// <param name="jsonModel">The JSON deserialization model.</param>
    [Theory]
    [InlineData(LoaderOptions.JsonDeserializationModel.Poco)]
    [InlineData(LoaderOptions.JsonDeserializationModel.SystemTextJson)]
    [Trait("Category", "Parse")]
    [Trait("Format", "Json")]
    [Trait("FhirVersion", "R5")]
    [Trait("DefaultCache", "true")]
    internal async Task ParseCorePackage(LoaderOptions.JsonDeserializationModel jsonModel)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath }, new() { JsonModel = jsonModel });
        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR5);

        loaded.ShouldNotBeNull();

        if (loaded == null)
        {
            return;
        }

        loaded.CodeSystemsByUrl.ShouldHaveCount(_countCodeSystemsByUrl);
        loaded.ValueSetsByVersionedUrl.ShouldHaveCount(_countValueSetsByUrl);
        loaded.PrimitiveTypesByName.ShouldHaveCount(_countPrimitiveTypesByName);
        loaded.ComplexTypesByName.ShouldHaveCount(_countComplexTypesByName);
        loaded.ResourcesByName.ShouldHaveCount(_countResourcesByName);
        loaded.LogicalModelsByUrl.ShouldHaveCount(_countLogicalModelsByName);
        loaded.ExtensionsByUrl.ShouldHaveCount(_countExtensionsByUrl);
        loaded.ProfilesByUrl.ShouldHaveCount(_countProfilesByUrl);
        loaded.SearchParametersByUrl.ShouldHaveCount(_countSearchParametersByUrl);
        loaded.OperationsByUrl.ShouldHaveCount(_countOperationsByUrl);
        loaded.CapabilityStatementsByUrl.ShouldHaveCount(_countCapabilityStatementsByUrl);
        loaded.ImplementationGuidesByUrl.ShouldHaveCount(_countImplementationGuidesByUrl);
        loaded.CompartmentsByUrl.ShouldHaveCount(_countCompartmentsByUrl);
    }
}

public class FhirPackageTestsR4B : FhirPackageTestBase
{
    private int _countCodeSystemsByUrl = TestCommon.EntriesR4.Length == 1 ? 540 : 565;
    private int _countValueSetsByUrl = TestCommon.EntriesR4.Length == 1 ? 745 : 805;
    private int _countPrimitiveTypesByName = 20;
    private int _countComplexTypesByName = 43;
    private int _countResourcesByName = 143;
    private int _countLogicalModelsByName = 4;
    private int _countExtensionsByUrl = TestCommon.EntriesR4.Length == 1 ? 398 : 559;
    private int _countProfilesByUrl = 43;
    private int _countSearchParametersByUrl = TestCommon.EntriesR4.Length == 1 ? 1439 : 1444;
    private int _countOperationsByUrl = 47;
    private int _countCapabilityStatementsByUrl = 6;
    private int _countImplementationGuidesByUrl = TestCommon.EntriesR4.Length == 1 ? 1 : 2;
    private int _countCompartmentsByUrl = 6;

    /// <summary>Initializes a new instance of the <see cref="FhirPackageTestsR4B"/> class.</summary>
    public FhirPackageTestsR4B() : base()
    {
    }

    /// <summary>
    /// Parses the core package.
    /// </summary>
    /// <param name="jsonModel">The JSON deserialization model.</param>
    [Fact]
    [Trait("Category", "Parse")]
    [Trait("Format", "Json")]
    [Trait("FhirVersion", "R4B")]
    [Trait("DefaultCache", "true")]
    internal async Task ParseCorePackage()
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath }, new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });
        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR4B);

        loaded.ShouldNotBeNull();

        if (loaded == null)
        {
            return;
        }

        loaded.CodeSystemsByUrl.ShouldHaveCount(_countCodeSystemsByUrl);
        loaded.ValueSetsByVersionedUrl.ShouldHaveCount(_countValueSetsByUrl);
        loaded.PrimitiveTypesByName.ShouldHaveCount(_countPrimitiveTypesByName);
        loaded.ComplexTypesByName.ShouldHaveCount(_countComplexTypesByName);
        loaded.ResourcesByName.ShouldHaveCount(_countResourcesByName);
        loaded.LogicalModelsByUrl.ShouldHaveCount(_countLogicalModelsByName);
        loaded.ExtensionsByUrl.ShouldHaveCount(_countExtensionsByUrl);
        loaded.ProfilesByUrl.ShouldHaveCount(_countProfilesByUrl);
        loaded.SearchParametersByUrl.ShouldHaveCount(_countSearchParametersByUrl);
        loaded.OperationsByUrl.ShouldHaveCount(_countOperationsByUrl);
        loaded.CapabilityStatementsByUrl.ShouldHaveCount(_countCapabilityStatementsByUrl);
        loaded.ImplementationGuidesByUrl.ShouldHaveCount(_countImplementationGuidesByUrl);
        loaded.CompartmentsByUrl.ShouldHaveCount(_countCompartmentsByUrl);
    }
}

public class FhirPackageTestsR4 : FhirPackageTestBase
{
    private int _countCodeSystemsByUrl = TestCommon.EntriesR4.Length == 1 ? 1062 : 1090;
    private int _countValueSetsByUrl = TestCommon.EntriesR4.Length == 1 ? 1317 : 1377;
    private int _countPrimitiveTypesByName = 20;
    private int _countComplexTypesByName = 41;
    private int _countResourcesByName = 148;
    private int _countLogicalModelsByName = 5;
    private int _countExtensionsByUrl = TestCommon.EntriesR4.Length == 1 ? 396 : 561;
    private int _countProfilesByUrl = 48;
    private int _countSearchParametersByUrl = TestCommon.EntriesR4.Length == 1 ? 1405 : 1410;
    private int _countOperationsByUrl = 47;
    private int _countCapabilityStatementsByUrl = 8;
    private int _countImplementationGuidesByUrl = TestCommon.EntriesR4.Length == 1 ? 0 : 1;
    private int _countCompartmentsByUrl = 6;

    /// <summary>Initializes a new instance of the <see cref="FhirPackageTestsR4"/> class.</summary>
    public FhirPackageTestsR4() : base()
    {
    }

    /// <summary>
    /// Parses the core package.
    /// </summary>
    /// <param name="jsonModel">The JSON deserialization model.</param>
    [Fact]
    [Trait("Category", "Parse")]
    [Trait("Format", "Json")]
    [Trait("FhirVersion", "R4")]
    [Trait("DefaultCache", "true")]
    internal async Task ParseCorePackage()
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath }, new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });
        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR4);

        loaded.ShouldNotBeNull();

        if (loaded == null)
        {
            return;
        }

        loaded.CodeSystemsByUrl.ShouldHaveCount(_countCodeSystemsByUrl);
        loaded.ValueSetsByVersionedUrl.ShouldHaveCount(_countValueSetsByUrl);
        loaded.PrimitiveTypesByName.ShouldHaveCount(_countPrimitiveTypesByName);
        loaded.ComplexTypesByName.ShouldHaveCount(_countComplexTypesByName);
        loaded.ResourcesByName.ShouldHaveCount(_countResourcesByName);
        loaded.LogicalModelsByUrl.ShouldHaveCount(_countLogicalModelsByName);
        loaded.ExtensionsByUrl.ShouldHaveCount(_countExtensionsByUrl);
        loaded.ProfilesByUrl.ShouldHaveCount(_countProfilesByUrl);
        loaded.SearchParametersByUrl.ShouldHaveCount(_countSearchParametersByUrl);
        loaded.OperationsByUrl.ShouldHaveCount(_countOperationsByUrl);
        loaded.CapabilityStatementsByUrl.ShouldHaveCount(_countCapabilityStatementsByUrl);
        loaded.ImplementationGuidesByUrl.ShouldHaveCount(_countImplementationGuidesByUrl);
        loaded.CompartmentsByUrl.ShouldHaveCount(_countCompartmentsByUrl);
    }
}

public class FhirPackageTestsR3 : FhirPackageTestBase
{
    private const int _countCodeSystemsByUrl = 941;
    private const int _countValueSetsByUrl = 1154;
    private const int _countPrimitiveTypesByName = 18;
    private const int _countComplexTypesByName = 35;
    private const int _countResourcesByName = 119;
    private const int _countLogicalModelsByName = 4;
    private const int _countExtensionsByUrl = 376;
    private const int _countProfilesByUrl = 33;
    private const int _countSearchParametersByUrl = 1247;
    private const int _countOperationsByUrl = 37;
    private const int _countCapabilityStatementsByUrl = 8;
    private const int _countImplementationGuidesByUrl = 2;
    private const int _countCompartmentsByUrl = 6;

    /// <summary>Initializes a new instance of the <see cref="FhirPackageTestsR3"/> class.</summary>
    public FhirPackageTestsR3() : base()
    {
    }

    /// <summary>
    /// Parses the core package.
    /// </summary>
    /// <param name="jsonModel">The JSON deserialization model.</param>
    [Fact]
    [Trait("Category", "Parse")]
    [Trait("Format", "Json")]
    [Trait("FhirVersion", "R3")]
    [Trait("DefaultCache", "true")]
    internal async Task ParseCorePackage()
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath }, new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });
        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR3);

        loaded.ShouldNotBeNull();

        if (loaded == null)
        {
            return;
        }

        loaded.CodeSystemsByUrl.ShouldHaveCount(_countCodeSystemsByUrl);
        loaded.ValueSetsByVersionedUrl.ShouldHaveCount(_countValueSetsByUrl);
        loaded.PrimitiveTypesByName.ShouldHaveCount(_countPrimitiveTypesByName);
        loaded.ComplexTypesByName.ShouldHaveCount(_countComplexTypesByName);
        loaded.ResourcesByName.ShouldHaveCount(_countResourcesByName);
        loaded.LogicalModelsByUrl.ShouldHaveCount(_countLogicalModelsByName);
        loaded.ExtensionsByUrl.ShouldHaveCount(_countExtensionsByUrl);
        loaded.ProfilesByUrl.ShouldHaveCount(_countProfilesByUrl);
        loaded.SearchParametersByUrl.ShouldHaveCount(_countSearchParametersByUrl);
        loaded.OperationsByUrl.ShouldHaveCount(_countOperationsByUrl);
        loaded.CapabilityStatementsByUrl.ShouldHaveCount(_countCapabilityStatementsByUrl);
        loaded.ImplementationGuidesByUrl.ShouldHaveCount(_countImplementationGuidesByUrl);
        loaded.CompartmentsByUrl.ShouldHaveCount(_countCompartmentsByUrl);
    }
}

public class FhirPackageTestsR2 : FhirPackageTestBase
{
    private const int _countCodeSystemsByUrl = 822;     // note - there are zero in the definitions, the 822 are contained in VS resources
    private const int _countValueSetsByUrl = 1016;
    private const int _countPrimitiveTypesByName = 18;
    private const int _countComplexTypesByName = 22;
    private const int _countResourcesByName = 96;
    private const int _countLogicalModelsByName = 0;
    private const int _countExtensionsByUrl = 285;
    private const int _countProfilesByUrl = 113;
    private const int _countSearchParametersByUrl = 907;
    private const int _countOperationsByUrl = 18;
    private const int _countCapabilityStatementsByUrl = 5;
    private const int _countImplementationGuidesByUrl = 0;
    private const int _countCompartmentsByUrl = 0;

    /// <summary>Initializes a new instance of the <see cref="FhirPackageTestsR2"/> class.</summary>
    public FhirPackageTestsR2() : base()
    {
    }

    /// <summary>
    /// Parses the core package.
    /// </summary>
    /// <param name="jsonModel">The JSON deserialization model.</param>
    [Fact]
    [Trait("Category", "Parse")]
    [Trait("Format", "Json")]
    [Trait("FhirVersion", "R2")]
    [Trait("DefaultCache", "true")]
    internal async Task ParseCorePackage()
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath }, new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR2);

        loaded.ShouldNotBeNull();

        if (loaded == null)
        {
            return;
        }

        loaded.CodeSystemsByUrl.ShouldHaveCount(_countCodeSystemsByUrl);
        loaded.ValueSetsByVersionedUrl.ShouldHaveCount(_countValueSetsByUrl);
        loaded.PrimitiveTypesByName.ShouldHaveCount(_countPrimitiveTypesByName);
        loaded.ComplexTypesByName.ShouldHaveCount(_countComplexTypesByName);
        loaded.ResourcesByName.ShouldHaveCount(_countResourcesByName);
        loaded.LogicalModelsByUrl.ShouldHaveCount(_countLogicalModelsByName);
        loaded.ExtensionsByUrl.ShouldHaveCount(_countExtensionsByUrl);
        loaded.ProfilesByUrl.ShouldHaveCount(_countProfilesByUrl);
        loaded.SearchParametersByUrl.ShouldHaveCount(_countSearchParametersByUrl);
        loaded.OperationsByUrl.ShouldHaveCount(_countOperationsByUrl);
        loaded.CapabilityStatementsByUrl.ShouldHaveCount(_countCapabilityStatementsByUrl);
        loaded.ImplementationGuidesByUrl.ShouldHaveCount(_countImplementationGuidesByUrl);
        loaded.CompartmentsByUrl.ShouldHaveCount(_countCompartmentsByUrl);
    }
}
