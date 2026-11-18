// <copyright file="XVerPreparedOperation.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Lib.Configuration;

namespace Fhir.CodeGen.Comparison.Models;

internal sealed record XVerOperationOptions
{
    internal XVerOperationOptions(
        XVerPreparationKind kind,
        IEnumerable<string>? exclusions = null,
        string? elementTableName = null,
        int? maxStepSize = null,
        IEnumerable<(FhirReleases.FhirSequenceCodes Source, FhirReleases.FhirSequenceCodes Target)>? specificPairs = null,
        bool processVocabulary = true,
        bool includeIgScripts = true,
        ConfigXVer? configuration = null)
    {
        Kind = kind;
        Exclusions = (exclusions ?? []).ToImmutableHashSet(StringComparer.Ordinal);
        ElementTableName = elementTableName ?? DbElement.DefaultTableName;
        if (ElementTableName.Length == 0 || ElementTableName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Prepared element-table identity must be a SQLite identifier.", nameof(elementTableName));
        }
        MaxStepSize = maxStepSize;
        SpecificPairs = specificPairs?.ToImmutableHashSet();
        ProcessVocabulary = processVocabulary;
        IncludeIgScripts = includeIgScripts;
        Construction = configuration is null ? null : new(
            configuration.OutputDirectory, configuration.CrossVersionMapSourcePath, configuration.XverArtifactVersion,
            configuration.ExportR2, configuration.ExportR3, configuration.ExportR4, configuration.ExportR4B,
            configuration.ExportR5, configuration.ExportR6);
    }

    internal XVerPreparationKind Kind { get; }
    internal ImmutableHashSet<string> Exclusions { get; }
    internal string ElementTableName { get; }
    internal int? MaxStepSize { get; }
    internal ImmutableHashSet<(FhirReleases.FhirSequenceCodes Source, FhirReleases.FhirSequenceCodes Target)>? SpecificPairs { get; }
    internal bool ProcessVocabulary { get; }
    internal bool IncludeIgScripts { get; }
    internal XVerConstructionConfiguration? Construction { get; }
}

internal sealed record XVerConstructionConfiguration(
    string OutputDirectory,
    string CrossVersionMapSourcePath,
    string ArtifactVersion,
    bool ExportR2,
    bool ExportR3,
    bool ExportR4,
    bool ExportR4B,
    bool ExportR5,
    bool ExportR6);

internal sealed class XVerPreparedOperation : IDisposable
{
    private readonly XVerOperandEvidence _evidence;
    private XVerStructureCatalog? _catalog;
    private string? _inventoryRevision;
    private bool _disposed;

    private XVerPreparedOperation(XVerOperandEvidence evidence)
    {
        _evidence = evidence;
    }

    internal XVerOperandEvidence Evidence
    {
        get
        {
            RequireActive();
            return _evidence;
        }
    }

    internal XVerStructureCatalog Catalog
    {
        get
        {
            RequireActive();
            return _catalog!;
        }
    }

    internal string? InventoryRevision
    {
        get
        {
            RequireActive();
            return _inventoryRevision;
        }
    }

    internal static XVerPreparedOperation Prepare(IDbConnection connection, XVerOperationOptions options)
    {
        XVerPreparedOperation operation = new(XVerOperandEvidence.Capture(connection, options));
        try
        {
            operation._catalog = new(operation._evidence);
            if (options.Kind is XVerPreparationKind.Comparison or XVerPreparationKind.Outcomes or XVerPreparationKind.Export)
            {
                DefinitionInventoryStore.ValidatedInventory state = DefinitionInventoryStore.RequireReady(operation._evidence, operation._catalog);
                operation._inventoryRevision = state.Revision;
                List<DbUnsafeCodeInputState> inputs = operation._evidence.Rows<DbUnsafeCodeInputState>(DbUnsafeCodeInputState.DefaultTableName);
                if (inputs.Count != 1 || inputs[0].Key != 1 ||
                    inputs[0].FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
                    string.IsNullOrEmpty(inputs[0].CompletedInputRevision))
                {
                    throw new InvalidOperationException("Unsafe-code inputs are missing, incomplete, or incompatible. Reload cross-version maps, rerun structure comparisons, and regenerate outcomes.");
                }
                DefinitionInventoryStore.RequireMapBinding(state, inputs[0].SnapshotId);
                operation._catalog.SelectCapturedElementView();
            }
            operation.Audit();
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    internal void Audit()
    {
        try
        {
            RequireActive();
            _evidence.Audit();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _evidence.RequireActive();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _catalog?.Dispose();
        _catalog = null;
        _inventoryRevision = null;
        _evidence.Dispose();
    }
}
