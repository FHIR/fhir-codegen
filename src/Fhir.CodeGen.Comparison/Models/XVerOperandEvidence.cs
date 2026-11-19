// <copyright file="XVerOperandEvidence.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Fhir.CodeGen.SQLiteGenerator;
using Microsoft.Data.Sqlite;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Models;

internal enum XVerPreparationKind
{
    Catalog,
    TypeComparison,
    ElementOutcomes,
    Construction,
    Comparison,
    Outcomes,
    Export,
    ExportContent,
}

internal enum XVerOperandFamily
{
    Inventory,
    Maps,
    Support,
    Vocabulary,
    StructureComparisons,
    VocabularyComparisons,
    PairControl,
    StructureOutcomes,
    VocabularyOutcomes,
}

internal enum XVerOutputTransitionKind
{
    Reset,
    PairBegin,
    PairFlush,
    PairComplete,
    PostProcess,
}

internal sealed record XVerOutputTransition(
    XVerOperandFamily Family,
    XVerOutputTransitionKind Kind,
    int? SourcePackageKey = null,
    int? TargetPackageKey = null)
{
    internal void RequireOutputFamily()
    {
        if (Family is XVerOperandFamily.Inventory or XVerOperandFamily.Maps or
            XVerOperandFamily.Support or XVerOperandFamily.Vocabulary)
        {
            throw new InvalidOperationException($"Prepared execution cannot replace stable {Family} operands.");
        }
        if (Kind != XVerOutputTransitionKind.Reset &&
            (SourcePackageKey is null || TargetPackageKey is null))
        {
            throw new InvalidOperationException("A non-reset output transition requires an exact directed package pair.");
        }
    }
}

internal sealed class XVerOperandEvidence : IDisposable
{
    private readonly DbConnection _connection;
    private readonly XVerOperationOptions _options;
    private readonly Dictionary<string, IXVerTableImage> _images = new(StringComparer.Ordinal);
    private readonly ImmutableArray<XVerTableRequirement> _requirements;
    private bool _disposed;
    private bool _invalidated;
    private bool _subscribed;
    private long _schemaVersion;
    private readonly bool _detached;

    private XVerOperandEvidence(DbConnection connection, XVerOperationOptions options)
    {
        _connection = connection;
        _options = options;
        _requirements = Requirements(options);
        _connection.StateChange += ConnectionStateChanged;
        _subscribed = true;
    }

    private XVerOperandEvidence(XVerOperationOptions options, IReadOnlyDictionary<string, IXVerTableImage> images)
    {
        _connection = null!;
        _options = options;
        _requirements = [];
        _detached = true;
        foreach ((string table, IXVerTableImage image) in images)
        {
            _images.Add(table, image);
        }
    }

    // Returned representation information owns only immutable row values. A query
    // reader cannot audit, certify readiness, or keep the live operation subscribed.
    internal sealed class ConstructionRows
    {
        private readonly XVerOperationOptions _options;
        private readonly IReadOnlyDictionary<string, IXVerTableImage> _images;

        internal ConstructionRows(XVerOperationOptions options, IReadOnlyDictionary<string, IXVerTableImage> images)
        {
            _options = options;
            _images = images;
        }

        internal XVerOperandEvidence OpenReader() => new(_options, _images);
    }

    internal ConstructionRows CopyConstructionRows()
    {
        RequireActive();
        XVerOperationOptions options = new(XVerPreparationKind.Construction, _options.Exclusions, _options.ElementTableName);
        return new(options, Requirements(options).ToDictionary(requirement => requirement.TableName,
            requirement => _images.GetValueOrDefault(requirement.TableName) ?? new MissingTable(), StringComparer.Ordinal));
    }

    internal XVerOperationOptions Options
    {
        get
        {
            RequireActive();
            return _options;
        }
    }

    internal static XVerOperandEvidence Capture(IDbConnection connection, XVerOperationOptions options)
    {
        if (connection is not DbConnection provider || provider.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Prepared operands require an open SQLite provider connection with physical state notifications.");
        }

        XVerOperandEvidence evidence = new(provider, options);
        try
        {
            Observation before = evidence.Observe();
            foreach (XVerTableRequirement requirement in evidence._requirements)
            {
                evidence._images.Add(requirement.TableName, evidence.Read(requirement));
            }
            Observation after = evidence.Observe();
            evidence.RequireCoherent(before, after);
            evidence._schemaVersion = after.SchemaVersion;
            return evidence;
        }
        catch
        {
            evidence.Dispose();
            throw;
        }
    }

    internal void Audit()
    {
        try
        {
            RequireActive();
            if (_detached)
            {
                throw new InvalidOperationException("Returned construction rows are not a live readiness capability.");
            }
            _options.AuditConfiguration();
            Observation before = Observe();
            foreach (XVerTableRequirement requirement in _requirements)
            {
                IXVerTableImage current = Read(requirement);
                if (!_images[requirement.TableName].ExactlyEquals(current))
                {
                    throw new InvalidOperationException($"Prepared operands changed in '{requirement.TableName}'; enter a fresh operation.");
                }
            }
            Observation after = Observe();
            RequireCoherent(before, after);
            if (after.SchemaVersion != _schemaVersion)
            {
                throw new InvalidOperationException("Prepared operand schema changed; enter a fresh operation.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void ApplyComparisonWrites(XVerOutputTransition transition, IEnumerable<DbRecordBase> inserts,
        IEnumerable<DbRecordBase> updates, Action write)
        => ApplyOutputWrites(transition, inserts, updates, write, outcomes: false);

    internal void ApplyOutcomeWrites(XVerOutputTransition transition, IEnumerable<DbRecordBase> inserts,
        IEnumerable<DbRecordBase> updates, Action write)
        => ApplyOutputWrites(transition, inserts, updates, write, outcomes: true);

    private void ApplyOutputWrites(XVerOutputTransition transition, IEnumerable<DbRecordBase> inserts,
        IEnumerable<DbRecordBase> updates, Action write, bool outcomes)
    {
        try
        {
            Audit();
            transition.RequireOutputFamily();
            bool permitted = outcomes
                ? transition.Family is XVerOperandFamily.StructureOutcomes or XVerOperandFamily.VocabularyOutcomes or XVerOperandFamily.PairControl
                : transition.Family is XVerOperandFamily.StructureComparisons or XVerOperandFamily.VocabularyComparisons or XVerOperandFamily.PairControl;
            if (!permitted ||
                transition.SourcePackageKey is null || transition.TargetPackageKey is null)
            {
                throw new InvalidOperationException("Comparison writes require an exact directed comparison or pair-control transition.");
            }
            Dictionary<string, IXVerTableImage> expected = new(_images, StringComparer.Ordinal);
            DbRecordBase[] added = [.. inserts];
            DbRecordBase[] changed = [.. updates];
            foreach (DbRecordBase row in added.Concat(changed))
            {
                XVerTableManifest manifest = Manifest.Single(table => table.RowType.IsInstanceOfType(row));
                if (manifest.Family != transition.Family ||
                    row is DbComparisonBase comparison &&
                        (comparison.SourceFhirPackageKey != transition.SourcePackageKey || comparison.TargetFhirPackageKey != transition.TargetPackageKey) ||
                    row is DbUnsafeCodePairState pair &&
                        (pair.SourceFhirPackageKey != transition.SourcePackageKey || pair.TargetFhirPackageKey != transition.TargetPackageKey) ||
                    row is DbOutcomeBase outcome &&
                        (outcome.SourceFhirPackageKey != transition.SourcePackageKey || outcome.TargetFhirPackageKey != transition.TargetPackageKey) ||
                    row is DbElementOutcomeTarget target &&
                        (target.SourceFhirPackageKey != transition.SourcePackageKey || target.TargetFhirPackageKey != transition.TargetPackageKey) ||
                    row is DbUnsafeCodeTrigger trigger &&
                        (trigger.SourceFhirPackageKey != transition.SourcePackageKey || trigger.TargetFhirPackageKey != transition.TargetPackageKey))
                {
                    throw new InvalidOperationException("An expected write must belong to its exact output family and directed pair.");
                }
            }
            AddChanges<DbStructureComparison, DbStructureComparisonOperand>(DbStructureComparison.DefaultTableName,
                row => DbStructureComparisonOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString(), row.Relationship?.ToString(),
                        row.ConceptDomainRelationship?.ToString(), row.ValueDomainRelationship?.ToString()],
                    [Integer(row.NotMapped), Integer(row.IsIdentical), Integer(row.ElementRelativePathsAreIdentical), Integer(row.IsBasedOnFallbackMapping)])), row => row.Key, row => row.ToRow());
            AddChanges<DbElementComparison, DbElementComparisonOperand>(DbElementComparison.DefaultTableName,
                row => DbElementComparisonOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString(), row.Relationship?.ToString(),
                        row.ConceptDomainRelationship?.ToString(), row.ValueDomainRelationship?.ToString(), row.BoundValueSetRelationship?.ToString(),
                        row.TypeRelationship?.ToString(), row.TargetProfileRelationship?.ToString(), row.TypeProfileRelationship?.ToString()],
                    [Integer(row.NotMapped), Integer(row.IsIdentical), Integer(row.RelativePathsAreIdentical), Integer(row.SourceRequiresMoreValues),
                        Integer(row.TargetRequiresMoreValues), Integer(row.SourceAllowsMoreValues), Integer(row.TargetAllowsMoreValues)])), row => row.Key, row => row.ToRow());
            AddChanges<DbElementTypeComparison, DbElementTypeComparisonOperand>(DbElementTypeComparison.DefaultTableName,
                row => DbElementTypeComparisonOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString(), row.Relationship?.ToString(),
                        row.TargetProfileRelationship?.ToString(), row.TypeProfileRelationship?.ToString()],
                    [Integer(row.NotMapped), Integer(row.IsIdentical)])), row => row.Key, row => row.ToRow());
            AddChanges<DbValueSetComparison, DbValueSetComparisonOperand>(DbValueSetComparison.DefaultTableName,
                row => DbValueSetComparisonOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString(), row.Relationship?.ToString()],
                    [Integer(row.NotMapped), Integer(row.IsIdentical), Integer(row.CodeLiteralsAreIdentical)])), row => row.Key, row => row.ToRow());
            AddChanges<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand>(DbValueSetConceptComparison.DefaultTableName,
                row => DbValueSetConceptComparisonOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString(), row.Relationship?.ToString()],
                    [Integer(row.NotMapped), Integer(row.IsIdentical), Integer(row.CodeLiteralsAreIdentical),
                        Integer(row.SourceCodeTreatedAsEscapeValve), Integer(row.TargetCodeTreatedAsEscapeValve)])), row => row.Key, row => row.ToRow());
            AddChanges<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>(DbUnsafeCodePairState.DefaultTableName,
                row => DbUnsafeCodePairStateOperand.Copy(row), row => row.Key, row => row.ToRow());
            AddChanges<DbStructureOutcome, DbStructureOutcomeOperand>(DbStructureOutcome.DefaultTableName,
                row => DbStructureOutcomeOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString(), row.SourceArtifactClass.ToString(), row.TargetArtifactClass?.ToString()],
                    OutcomeIntegers(row))), row => row.Key, row => row.ToRow());
            AddChanges<DbElementOutcome, DbElementOutcomeOperand>(DbElementOutcome.DefaultTableName,
                row => DbElementOutcomeOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString()],
                    [.. OutcomeIntegers(row), Integer(row.RequiresExtensionDefinition), Integer(row.RequiresSliceDefinition),
                        Integer(row.RequiresCardinalityDefinition), Integer(row.RequiresCardinalitySlice), Integer(row.SourceUsedAsContentReference),
                        Integer(row.ContentReferenceRequiresXVerDefinition), Integer(row.RequiresDefinitionAsContentReference),
                        Integer(row.ExtensionDefinitionIsProhibited), Integer(row.SourceIsModifier), Integer(row.DefineAsModifier),
                        Integer(row.SourceIsDeprecated)])), row => row.Key, row => row.ToRow());
            AddChanges<DbElementOutcomeTarget, DbElementOutcomeTargetOperand>(DbElementOutcomeTarget.DefaultTableName,
                row => DbElementOutcomeTargetOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString()],
                    [Integer(row.FullyMapsToThisTarget), Integer(row.TargetIsModifier)])), row => row.Key, row => row.ToRow());
            AddChanges<DbUnsafeCodeTrigger, DbUnsafeCodeTriggerOperand>(DbUnsafeCodeTrigger.DefaultTableName,
                row => DbUnsafeCodeTriggerOperand.Copy(row, new([], [Integer(row.IsWildcardVersion)])), row => row.Key, row => row.ToRow());
            AddChanges<DbValueSetOutcome, DbValueSetOutcomeOperand>(DbValueSetOutcome.DefaultTableName,
                row => DbValueSetOutcomeOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString()], OutcomeIntegers(row))),
                row => row.Key, row => row.ToRow());
            AddChanges<DbValueSetConceptOutcome, DbValueSetConceptOutcomeOperand>(DbValueSetConceptOutcome.DefaultTableName,
                row => DbValueSetConceptOutcomeOperand.Copy(row, new(
                    [row.SourceFhirSequence.ToString(), row.TargetFhirSequence.ToString()], OutcomeIntegers(row))),
                row => row.Key, row => row.ToRow());
            AcceptExpected(expected, _schemaVersion, write);

            void AddChanges<T, TRow>(string table, Func<T, TRow> snapshot, Func<TRow, int> key, Func<TRow, T> copy)
                where T : DbRecordBase where TRow : IEquatable<TRow>
            {
                T[] additions = [.. added.OfType<T>()];
                T[] changes = [.. changed.OfType<T>()];
                if (additions.Length == 0 && changes.Length == 0)
                {
                    return;
                }

                TableImage<T, TRow> image = (TableImage<T, TRow>)expected[table];
                Dictionary<int, TRow> rows = image.StoredRows.ToDictionary(key);
                foreach (T row in additions)
                {
                    // The existing producers use INSERT OR IGNORE, then UPDATE.
                    TRow value = snapshot(row);
                    rows.TryAdd(key(value), value);
                }
                foreach (T row in changes)
                {
                    TRow value = snapshot(row);
                    if (rows.ContainsKey(key(value)))
                    {
                        rows[key(value)] = value;
                    }
                }
                expected[table] = new TableImage<T, TRow>(rows.OrderBy(entry => entry.Key).Select(entry => entry.Value), copy);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static long?[] OutcomeIntegers(DbOutcomeBase row) =>
        [Integer(row.RequiresXVerDefinition), Integer(row.IsRenamed), Integer(row.IsUnmapped), Integer(row.IsIdentical),
            Integer(row.IsEquivalent), Integer(row.IsBroaderThanTarget), Integer(row.IsNarrowerThanTarget),
            Integer(row.FullyMapsToThisTarget), Integer(row.FullyMapsAcrossAllTargets)];

    internal void ResetOutcomes(bool vocabulary, Action reset)
    {
        try
        {
            Audit();
            Dictionary<string, IXVerTableImage> expected = new(_images, StringComparer.Ordinal);
            long schema = _schemaVersion;
            Reset<DbStructureOutcome, DbStructureOutcomeOperand>(DbStructureOutcome.DefaultTableName, 3, row => row.ToRow());
            Reset<DbElementOutcome, DbElementOutcomeOperand>(DbElementOutcome.DefaultTableName, 7, row => row.ToRow());
            Reset<DbElementOutcomeTarget, DbElementOutcomeTargetOperand>(DbElementOutcomeTarget.DefaultTableName, 3, row => row.ToRow());
            Reset<DbUnsafeCodeTrigger, DbUnsafeCodeTriggerOperand>(DbUnsafeCodeTrigger.DefaultTableName, 2, row => row.ToRow());
            if (vocabulary)
            {
                Reset<DbValueSetOutcome, DbValueSetOutcomeOperand>(DbValueSetOutcome.DefaultTableName, 2, row => row.ToRow());
                Reset<DbValueSetConceptOutcome, DbValueSetConceptOutcomeOperand>(DbValueSetConceptOutcome.DefaultTableName, 3, row => row.ToRow());
            }
            string control = DbUnsafeCodePairState.DefaultTableName;
            TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand> pairs =
                (TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>)_images[control];
            expected[control] = new TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>(
                pairs.StoredRows.Select(row => row with
                {
                    OutcomeInputRevision = null, OutcomeComparisonRevision = null, OutcomeFormatVersion = null,
                }), row => row.ToRow());
            AcceptExpected(expected, schema, reset);

            void Reset<T, TRow>(string table, int indexes, Func<TRow, T> copy)
                where T : DbRecordBase where TRow : IEquatable<TRow>
            {
                schema += (_images[table] is MissingTable ? 0 : 1) + 1 + indexes;
                expected[table] = new TableImage<T, TRow>([], copy);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void BeginOutcomePair(int sourceKey, int targetKey, DbUnsafeCodePairState pair, Action begin)
    {
        try
        {
            Audit();
            string table = DbUnsafeCodeTrigger.DefaultTableName;
            TableImage<DbUnsafeCodeTrigger, DbUnsafeCodeTriggerOperand> triggers =
                (TableImage<DbUnsafeCodeTrigger, DbUnsafeCodeTriggerOperand>)_images[table];
            string control = DbUnsafeCodePairState.DefaultTableName;
            TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand> pairs =
                (TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>)_images[control];
            if (pair.SourceFhirPackageKey != sourceKey || pair.TargetFhirPackageKey != targetKey)
            {
                throw new InvalidOperationException("Outcome begin requires its exact pair.");
            }
            Dictionary<string, IXVerTableImage> expected = new(_images, StringComparer.Ordinal)
            {
                [table] = new TableImage<DbUnsafeCodeTrigger, DbUnsafeCodeTriggerOperand>(
                    triggers.StoredRows.Where(row => row.SourceFhirPackageKey != sourceKey || row.TargetFhirPackageKey != targetKey), row => row.ToRow()),
                [control] = new TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>(
                    pairs.StoredRows.Select(row => row.Key == pair.Key ? row with
                    {
                        OutcomeInputRevision = null, OutcomeComparisonRevision = null, OutcomeFormatVersion = null,
                    } : row), row => row.ToRow()),
            };
            AcceptExpected(expected, _schemaVersion, begin);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void ApplyConceptRelationships(int sourceKey, int targetKey, IReadOnlySet<int> keys, CMR relationship, Action write)
    {
        try
        {
            Audit();
            string table = DbValueSetConceptComparison.DefaultTableName;
            TableImage<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand> image =
                (TableImage<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand>)_images[table];
            if (image.StoredRows.Count(row => keys.Contains(row.Key) && row.SourceFhirPackageKey == sourceKey &&
                row.TargetFhirPackageKey == targetKey && row.RelationshipStoredText == CMR.Equivalent.ToString()) != keys.Count)
            {
                throw new InvalidOperationException("Relationship deltas require exact equivalent rows in the scheduled directed pair.");
            }
            Dictionary<string, IXVerTableImage> expected = new(_images, StringComparer.Ordinal)
            {
                [table] = new TableImage<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand>(
                    image.StoredRows.Select(row => keys.Contains(row.Key)
                        ? row with { Relationship = relationship, RelationshipStoredText = relationship.ToString() } : row), row => row.ToRow()),
            };
            AcceptExpected(expected, _schemaVersion, write);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void ResetComparisons(bool vocabulary, Action reset)
    {
        try
        {
            Audit();
            Dictionary<string, IXVerTableImage> expected = new(_images, StringComparer.Ordinal);
            long schema = _schemaVersion;
            Reset<DbStructureComparison, DbStructureComparisonOperand>(DbStructureComparison.DefaultTableName, 2, row => row.ToRow());
            Reset<DbElementComparison, DbElementComparisonOperand>(DbElementComparison.DefaultTableName, 3, row => row.ToRow());
            Reset<DbElementTypeComparison, DbElementTypeComparisonOperand>(DbElementTypeComparison.DefaultTableName, 1, row => row.ToRow());
            if (vocabulary)
            {
                Reset<DbValueSetComparison, DbValueSetComparisonOperand>(DbValueSetComparison.DefaultTableName, 2, row => row.ToRow());
                Reset<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand>(DbValueSetConceptComparison.DefaultTableName, 4, row => row.ToRow());
            }
            string control = DbUnsafeCodePairState.DefaultTableName;
            TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand> pairs =
                (TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>)_images[control];
            expected[control] = new TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>(
                pairs.StoredRows.Select(row => row with
                {
                    ComparisonRevision = null, OutcomeInputRevision = null,
                    OutcomeComparisonRevision = null, OutcomeFormatVersion = null,
                }), row => row.ToRow());
            AcceptExpected(expected, schema, reset);

            void Reset<T, TRow>(string table, int indexes, Func<TRow, T> copy)
                where T : DbRecordBase where TRow : IEquatable<TRow>
            {
                // DROP changes schema once (including its indexes); CREATE changes
                // it once for the table and once per declared generated index.
                schema += (_images[table] is MissingTable ? 0 : 1) + 1 + indexes;
                expected[table] = new TableImage<T, TRow>([], copy);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AcceptExpected(Dictionary<string, IXVerTableImage> expected, long schemaVersion, Action write)
    {
        write();
        Observation before = Observe();
        foreach (XVerTableRequirement requirement in _requirements)
        {
            if (!expected[requirement.TableName].ExactlyEquals(Read(requirement)))
            {
                throw new InvalidOperationException($"Prepared expected output or stable operands changed in '{requirement.TableName}'; no completion was certified.");
            }
        }
        Observation after = Observe();
        RequireCoherent(before, after);
        if (after.SchemaVersion != schemaVersion)
        {
            throw new InvalidOperationException("Prepared output transition changed undeclared schema.");
        }
        // These are the original stable images and independently expected outputs,
        // never the live rows just read for verification.
        _images.Clear();
        foreach ((string table, IXVerTableImage image) in expected)
        {
            _images.Add(table, image);
        }
        _schemaVersion = schemaVersion;
    }

    private static long? Integer(bool? value) => value is null ? null : value.Value ? 1L : 0L;

    internal List<(DbValueSetConceptComparison Row, string? Relationship)> ConceptRelationshipRows()
    {
        RequireActive();
        TableImage<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand> image =
            (TableImage<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand>)_images[DbValueSetConceptComparison.DefaultTableName];
        return [.. image.StoredRows.Select(row => (row.ToRow(), row.RelationshipStoredText))];
    }

    internal List<T> Rows<T>(string tableName) where T : DbRecordBase
    {
        RequireActive();
        if (!_images.TryGetValue(tableName, out IXVerTableImage? image))
        {
            throw new InvalidOperationException($"Prepared reader for '{tableName}' is not registered by {_options.Kind}.");
        }
        if (image is not IXVerRows<T> rows)
        {
            throw new InvalidOperationException($"Prepared reader for '{tableName}' is missing or has an incompatible row type.");
        }
        return rows.CopyRows();
    }

    internal bool ContainsTable(string tableName)
    {
        RequireActive();
        return _images.TryGetValue(tableName, out IXVerTableImage? image) && image is not MissingTable;
    }

    internal void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_invalidated || !_detached && _connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Prepared operands were invalidated by a physical connection state change; enter a fresh operation.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Detach();
        _images.Clear();
    }

    private void ConnectionStateChanged(object? sender, StateChangeEventArgs args)
    {
        _invalidated = true;
        Detach();
    }

    private void Detach()
    {
        if (_subscribed)
        {
            _connection.StateChange -= ConnectionStateChanged;
            _subscribed = false;
        }
    }

    private IXVerTableImage Read(XVerTableRequirement requirement)
    {
        RequireActive();
        if (!DefinitionInventoryStore.TableExists(_connection, requirement.TableName))
        {
            if (requirement.Required)
            {
                throw new InvalidOperationException(
                    $"Prepared {_options.Kind} operands require table '{requirement.TableName}'. Reload the database, load maps, compare, generate outcomes, then export.");
            }
            return new MissingTable();
        }
        try
        {
            return requirement.Manifest.Read(_connection, requirement.TableName);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            throw new InvalidOperationException(
                $"Prepared {_options.Kind} operands cannot read required columns of '{requirement.TableName}'. Reload the database if incompatible.",
                exception);
        }
    }

    private Observation Observe()
    {
        RequireActive();
        return new(ReadInteger("PRAGMA data_version"), ReadInteger("SELECT total_changes()"), ReadInteger("PRAGMA schema_version"));
    }

    private long ReadInteger(string sql)
    {
        using IDbCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void RequireCoherent(Observation before, Observation after)
    {
        RequireActive();
        if (before != after)
        {
            throw new InvalidOperationException("Prepared operands changed during a read pass (data_version, total_changes, or schema_version); no operation was published.");
        }
    }

    private readonly record struct Observation(long DataVersion, long TotalChanges, long SchemaVersion);

    internal interface IXVerTableImage
    {
        bool ExactlyEquals(IXVerTableImage other);
    }

    private interface IXVerRows<T> where T : DbRecordBase
    {
        List<T> CopyRows();
    }

    private sealed record MissingTable : IXVerTableImage
    {
        public bool ExactlyEquals(IXVerTableImage other) => other is MissingTable;
    }

    private sealed class TableImage<T, TRow>(IEnumerable<TRow> rows, Func<TRow, T> copy) : IXVerTableImage, IXVerRows<T>
        where T : DbRecordBase
        where TRow : IEquatable<TRow>
    {
        private readonly ImmutableArray<TRow> _rows = [.. rows];
        internal ImmutableArray<TRow> StoredRows => _rows;

        public bool ExactlyEquals(IXVerTableImage other) =>
            other is TableImage<T, TRow> image && _rows.SequenceEqual(image._rows);

        public List<T> CopyRows() => [.. _rows.Select(copy)];
    }

    internal sealed record XVerTableManifest(
        Type RowType,
        string TableName,
        XVerOperandFamily Family,
        ImmutableArray<string> PersistedFields)
    {
        internal Func<IDbConnection, string, IXVerTableImage> Reader { get; init; } = null!;

        internal IXVerTableImage Read(IDbConnection connection, string tableName) => Reader(connection, tableName);
    }

    private sealed record XVerTableRequirement(XVerTableManifest Manifest, string TableName, bool Required);

    private static ImmutableArray<XVerTableRequirement> Requirements(XVerOperationOptions options)
    {
        bool pipeline = options.Kind is XVerPreparationKind.Comparison or XVerPreparationKind.Outcomes or XVerPreparationKind.Export;
        bool inventory = pipeline;
        List<XVerTableRequirement> requirements = [];
        foreach (XVerTableManifest table in Manifest)
        {
            bool core = table.RowType == typeof(DbFhirPackage) || table.RowType == typeof(DbStructureDefinition) ||
                table.RowType == typeof(DbStructureDefinitionFacts) || table.RowType == typeof(DbElement) ||
                table.RowType == typeof(DbElementType);
            if (options.Kind == XVerPreparationKind.ExportContent)
            {
                if (table.RowType == typeof(DbFhirPackage) || options.IncludeStructurePages && core || options.ProcessVocabulary &&
                    (table.Family is XVerOperandFamily.Vocabulary or XVerOperandFamily.VocabularyComparisons or XVerOperandFamily.VocabularyOutcomes ||
                        table.RowType == typeof(DbElement) || table.RowType == typeof(DbExternalInclusion)))
                {
                    requirements.Add(new(table, table.TableName, table.RowType == typeof(DbFhirPackage) || options.IncludeStructurePages && core));
                }
                continue;
            }
            bool include = pipeline || core || IsNarrowDependency(options.Kind, table.RowType);
            if (!include)
            {
                continue;
            }
            bool required = core || (pipeline && (table.Family is XVerOperandFamily.Inventory or XVerOperandFamily.Maps or
                XVerOperandFamily.Support or XVerOperandFamily.Vocabulary or XVerOperandFamily.PairControl));
            if (pipeline && (options.Kind is XVerPreparationKind.Outcomes or XVerPreparationKind.Export) &&
                (table.Family is XVerOperandFamily.StructureComparisons or XVerOperandFamily.PairControl ||
                    (options.ProcessVocabulary && table.Family == XVerOperandFamily.VocabularyComparisons)))
            {
                required = true;
            }
            if (options.Kind == XVerPreparationKind.Export &&
                (table.Family == XVerOperandFamily.StructureOutcomes ||
                    (options.ProcessVocabulary && table.Family == XVerOperandFamily.VocabularyOutcomes)))
            {
                required = true;
            }
            if (table.RowType == typeof(DbElement) && options.ElementTableName != DbElement.DefaultTableName)
            {
                if (inventory)
                {
                    requirements.Add(new(table, table.TableName, true));
                }
                requirements.Add(new(table, options.ElementTableName, true));
            }
            else
            {
                requirements.Add(new(table, table.TableName, required));
            }
        }
        return [.. requirements];
    }

    private static bool IsNarrowDependency(XVerPreparationKind kind, Type rowType)
    {
        // Catalog methods retain their lazy mapping/binding contracts; missing
        // optional tables are recorded as absent, not acquired as prerequisites.
        if (rowType == typeof(DbElementMapping) || rowType == typeof(DbValueSet))
        {
            return true;
        }
        return kind switch
        {
            XVerPreparationKind.TypeComparison => rowType == typeof(DbStructureMapping),
            XVerPreparationKind.ElementOutcomes =>
                rowType == typeof(DbStructureMapping) || rowType == typeof(DbExtensionSubstitution) ||
                rowType == typeof(DbStructureComparison) || rowType == typeof(DbElementComparison) ||
                rowType == typeof(DbElementTypeComparison) || rowType == typeof(DbElementOutcome) ||
                rowType == typeof(DbElementOutcomeTarget),
            XVerPreparationKind.Construction =>
                rowType == typeof(DbUnsafeCodePairState) ||
                rowType == typeof(DbElementComparison) || rowType == typeof(DbElementTypeComparison) ||
                rowType == typeof(DbStructureComparison) || rowType == typeof(DbValueSetConcept) ||
                rowType == typeof(DbStructureOutcome) || rowType == typeof(DbElementOutcome) ||
                rowType == typeof(DbElementOutcomeTarget) || rowType == typeof(DbValueSetOutcome) ||
                rowType == typeof(DbUnsafeCodeTrigger) || rowType == typeof(DbUnsafeCodeElementAnnotation) ||
                rowType == typeof(DbProfileMapOwnerFacts) || rowType == typeof(DbUnsafeCodeMapEvidence) ||
                rowType == typeof(DbUnsafeCodeRule),
            _ => false,
        };
    }

    // Generated selectors own column decoding. Supplemental storage values retain
    // exact JSON, enum/date text and integers hidden by mutable/decoded projections.
    private static Dictionary<int, StoredColumns> ReadStoredColumns(
        IDbConnection connection, string tableName, string[] textFields, string[] integerFields)
    {
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT Key, {string.Join(", ", textFields.Concat(integerFields))} FROM {tableName} ORDER BY Key";
        using IDataReader reader = command.ExecuteReader();
        Dictionary<int, StoredColumns> rows = [];
        while (reader.Read())
        {
            string?[] text = new string?[textFields.Length];
            long?[] integers = new long?[integerFields.Length];
            for (int index = 0; index < text.Length; index++)
            {
                text[index] = reader.IsDBNull(index + 1) ? null : reader.GetString(index + 1);
            }
            for (int index = 0; index < integers.Length; index++)
            {
                int ordinal = text.Length + index + 1;
                integers[index] = reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
            }
            rows.Add(reader.GetInt32(0), new(text, integers));
        }
        return rows;
    }

    private sealed record StoredColumns(string?[] Text, long?[] Integers);

    // Readers: inventory validation/catalog; maps/provenance; support/vocabulary;
    // directed/transitive comparison products; independent persisted-result/construction readers.
    internal static ImmutableArray<XVerTableManifest> Manifest { get; } =
    [
        new(typeof(DbDefinitionInventoryState), DbDefinitionInventoryState.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbDefinitionInventoryState.Key), nameof(DbDefinitionInventoryState.FormatVersion), nameof(DbDefinitionInventoryState.PolicyVersion),
                nameof(DbDefinitionInventoryState.StartedRevision), nameof(DbDefinitionInventoryState.CompletedRevision), nameof(DbDefinitionInventoryState.ExpectedPackageCountsJson),
                nameof(DbDefinitionInventoryState.PendingMapInventoryBindingsJson), nameof(DbDefinitionInventoryState.CompletedMapInventoryBindingsJson),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbDefinitionInventoryState> rows = DbDefinitionInventoryState.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbDefinitionInventoryState.Key)]);
                return new TableImage<DbDefinitionInventoryState, DbDefinitionInventoryStateOperand>(rows.Select(row => DbDefinitionInventoryStateOperand.Copy(row)), row => row.ToRow());
            },
        },
        new(typeof(DbFhirPackage), DbFhirPackage.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbFhirPackage.Key), nameof(DbFhirPackage.Name), nameof(DbFhirPackage.PackageId),
                nameof(DbFhirPackage.PackageVersion), nameof(DbFhirPackage.FhirVersionShort), nameof(DbFhirPackage.CanonicalUrl),
                nameof(DbFhirPackage.ShortName), nameof(DbFhirPackage.Dependencies), nameof(DbFhirPackage.DefinitionFhirSequence),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbFhirPackage> rows = DbFhirPackage.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbFhirPackage.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbFhirPackage.DefinitionFhirSequence)],
                    []);
                return new TableImage<DbFhirPackage, DbFhirPackageOperand>(rows.Select(row => DbFhirPackageOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbStructureDefinition), DbStructureDefinition.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbStructureDefinition.Key), nameof(DbStructureDefinition.FhirPackageKey), nameof(DbStructureDefinition.Id),
                nameof(DbStructureDefinition.VersionedUrl), nameof(DbStructureDefinition.UnversionedUrl), nameof(DbStructureDefinition.Name),
                nameof(DbStructureDefinition.Version), nameof(DbStructureDefinition.VersionAlgorithmString), nameof(DbStructureDefinition.VersionAlgorithmCoding),
                nameof(DbStructureDefinition.Status), nameof(DbStructureDefinition.Title), nameof(DbStructureDefinition.Description),
                nameof(DbStructureDefinition.Purpose), nameof(DbStructureDefinition.Narrative), nameof(DbStructureDefinition.StandardStatus),
                nameof(DbStructureDefinition.WorkGroup), nameof(DbStructureDefinition.FhirMaturity), nameof(DbStructureDefinition.IsExperimental),
                nameof(DbStructureDefinition.LastChangedDate), nameof(DbStructureDefinition.Publisher), nameof(DbStructureDefinition.Copyright),
                nameof(DbStructureDefinition.CopyrightLabel), nameof(DbStructureDefinition.ApprovalDate), nameof(DbStructureDefinition.LastReviewDate),
                nameof(DbStructureDefinition.EffectivePeriodStart), nameof(DbStructureDefinition.EffectivePeriodEnd), nameof(DbStructureDefinition.Topic),
                nameof(DbStructureDefinition.RelatedArtifacts), nameof(DbStructureDefinition.Jurisdictions), nameof(DbStructureDefinition.UseContexts),
                nameof(DbStructureDefinition.Contacts), nameof(DbStructureDefinition.Authors), nameof(DbStructureDefinition.Editors),
                nameof(DbStructureDefinition.Reviewers), nameof(DbStructureDefinition.Endorsers), nameof(DbStructureDefinition.RootExtensions),
                nameof(DbStructureDefinition.SourcePackageMoniker), nameof(DbStructureDefinition.Comment), nameof(DbStructureDefinition.Message),
                nameof(DbStructureDefinition.ArtifactClass), nameof(DbStructureDefinition.SnapshotCount), nameof(DbStructureDefinition.DifferentialCount),
                nameof(DbStructureDefinition.Implements),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbStructureDefinition> rows = DbStructureDefinition.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbStructureDefinition.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbStructureDefinition.VersionAlgorithmCoding), nameof(DbStructureDefinition.Status), nameof(DbStructureDefinition.Narrative), nameof(DbStructureDefinition.LastChangedDate), nameof(DbStructureDefinition.EffectivePeriodStart), nameof(DbStructureDefinition.EffectivePeriodEnd), nameof(DbStructureDefinition.Topic), nameof(DbStructureDefinition.RelatedArtifacts), nameof(DbStructureDefinition.Jurisdictions), nameof(DbStructureDefinition.UseContexts), nameof(DbStructureDefinition.Contacts), nameof(DbStructureDefinition.Authors), nameof(DbStructureDefinition.Editors), nameof(DbStructureDefinition.Reviewers), nameof(DbStructureDefinition.Endorsers), nameof(DbStructureDefinition.RootExtensions), nameof(DbStructureDefinition.ArtifactClass)],
                    [nameof(DbStructureDefinition.IsExperimental)]);
                return new TableImage<DbStructureDefinition, DbStructureDefinitionOperand>(rows.Select(row => DbStructureDefinitionOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbStructureDefinitionFacts), DbStructureDefinitionFacts.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbStructureDefinitionFacts.Key), nameof(DbStructureDefinitionFacts.FhirPackageKey), nameof(DbStructureDefinitionFacts.StructureKey),
                nameof(DbStructureDefinitionFacts.Type), nameof(DbStructureDefinitionFacts.Kind), nameof(DbStructureDefinitionFacts.Derivation),
                nameof(DbStructureDefinitionFacts.BaseDefinition), nameof(DbStructureDefinitionFacts.SnapshotAvailability), nameof(DbStructureDefinitionFacts.StructureDefinitionJson),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbStructureDefinitionFacts> rows = DbStructureDefinitionFacts.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbStructureDefinitionFacts.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbStructureDefinitionFacts.SnapshotAvailability)],
                    []);
                return new TableImage<DbStructureDefinitionFacts, DbStructureDefinitionFactsOperand>(rows.Select(row => DbStructureDefinitionFactsOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElement), DbElement.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbElement.Key), nameof(DbElement.FhirPackageKey), nameof(DbElement.StructureKey),
                nameof(DbElement.ParentElementKey), nameof(DbElement.ResourceFieldOrder), nameof(DbElement.ComponentFieldOrder),
                nameof(DbElement.Id), nameof(DbElement.Path), nameof(DbElement.ChildElementCount),
                nameof(DbElement.Name), nameof(DbElement.Short), nameof(DbElement.Definition),
                nameof(DbElement.Comments), nameof(DbElement.Requirements), nameof(DbElement.MinCardinality),
                nameof(DbElement.MaxCardinality), nameof(DbElement.MaxCardinalityString), nameof(DbElement.SliceName),
                nameof(DbElement.FullCollatedTypeLiteral), nameof(DbElement.DistinctTypeCount), nameof(DbElement.DistinctTypeLiterals),
                nameof(DbElement.ValueSetBindingStrength), nameof(DbElement.BindingValueSet), nameof(DbElement.BindingValueSetKey),
                nameof(DbElement.AdditionalBindingCount), nameof(DbElement.BindingDescription), nameof(DbElement.IsInherited),
                nameof(DbElement.BasePath), nameof(DbElement.BaseElementKey), nameof(DbElement.BaseStructureKey),
                nameof(DbElement.DefinedAsContentReference), nameof(DbElement.ContentReferenceSourceKey), nameof(DbElement.ContentReferenceSourceId),
                nameof(DbElement.UsedAsContentReference), nameof(DbElement.IsSimpleType), nameof(DbElement.IsChoiceType),
                nameof(DbElement.IsModifier), nameof(DbElement.IsModifierReason), nameof(DbElement.IsDeprecated),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElement> rows = DbElement.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElement.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElement.ValueSetBindingStrength)],
                    [nameof(DbElement.IsInherited), nameof(DbElement.DefinedAsContentReference), nameof(DbElement.UsedAsContentReference), nameof(DbElement.IsSimpleType), nameof(DbElement.IsChoiceType), nameof(DbElement.IsModifier), nameof(DbElement.IsDeprecated)]);
                return new TableImage<DbElement, DbElementOperand>(rows.Select(row => DbElementOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElementType), DbElementType.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbElementType.Key), nameof(DbElementType.FhirPackageKey), nameof(DbElementType.StructureKey),
                nameof(DbElementType.ElementKey), nameof(DbElementType.TypeName), nameof(DbElementType.TypeStructureKey),
                nameof(DbElementType.TypeProfile), nameof(DbElementType.TypeProfileStructureKey), nameof(DbElementType.TargetProfile),
                nameof(DbElementType.TargetProfileStructureKey),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementType> rows = DbElementType.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementType.Key)]);
                return new TableImage<DbElementType, DbElementTypeOperand>(rows.Select(row => DbElementTypeOperand.Copy(row)), row => row.ToRow());
            },
        },
        new(typeof(DbElementAdditionalBinding), DbElementAdditionalBinding.DefaultTableName, XVerOperandFamily.Inventory,
            [
                nameof(DbElementAdditionalBinding.Key), nameof(DbElementAdditionalBinding.FhirPackageKey), nameof(DbElementAdditionalBinding.StructureKey),
                nameof(DbElementAdditionalBinding.ElementKey), nameof(DbElementAdditionalBinding.FhirKey), nameof(DbElementAdditionalBinding.Purpose),
                nameof(DbElementAdditionalBinding.BindingValueSet), nameof(DbElementAdditionalBinding.BindingValueSetKey), nameof(DbElementAdditionalBinding.Documentation),
                nameof(DbElementAdditionalBinding.ShortDocumentation), nameof(DbElementAdditionalBinding.CollatedUsageContexts), nameof(DbElementAdditionalBinding.SatisfiedBySingleRepetition),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementAdditionalBinding> rows = DbElementAdditionalBinding.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementAdditionalBinding.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElementAdditionalBinding.Purpose)],
                    [nameof(DbElementAdditionalBinding.SatisfiedBySingleRepetition)]);
                return new TableImage<DbElementAdditionalBinding, DbElementAdditionalBindingOperand>(rows.Select(row => DbElementAdditionalBindingOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbUnsafeCodeInputState), DbUnsafeCodeInputState.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbUnsafeCodeInputState.Key), nameof(DbUnsafeCodeInputState.SnapshotId), nameof(DbUnsafeCodeInputState.FormatVersion),
                nameof(DbUnsafeCodeInputState.SourceMode), nameof(DbUnsafeCodeInputState.SourceRoot), nameof(DbUnsafeCodeInputState.CompletedInputRevision),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbUnsafeCodeInputState> rows = DbUnsafeCodeInputState.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbUnsafeCodeInputState.Key)]);
                return new TableImage<DbUnsafeCodeInputState, DbUnsafeCodeInputStateOperand>(rows.Select(row => DbUnsafeCodeInputStateOperand.Copy(row)), row => row.ToRow());
            },
        },
        new(typeof(DbMappingSourceFile), DbMappingSourceFile.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbMappingSourceFile.Key), nameof(DbMappingSourceFile.Filename), nameof(DbMappingSourceFile.SourceRelativeFilename),
                nameof(DbMappingSourceFile.IsConceptMap), nameof(DbMappingSourceFile.IsFml), nameof(DbMappingSourceFile.Url),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbMappingSourceFile> rows = DbMappingSourceFile.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbMappingSourceFile.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [],
                    [nameof(DbMappingSourceFile.IsConceptMap), nameof(DbMappingSourceFile.IsFml)]);
                return new TableImage<DbMappingSourceFile, DbMappingSourceFileOperand>(rows.Select(row => DbMappingSourceFileOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbStructureMapping), DbStructureMapping.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbStructureMapping.Key), nameof(DbStructureMapping.SourceFhirPackageKey), nameof(DbStructureMapping.SourceFhirSequence),
                nameof(DbStructureMapping.TargetFhirPackageKey), nameof(DbStructureMapping.TargetFhirSequence), nameof(DbStructureMapping.ConceptMapSourceKey),
                nameof(DbStructureMapping.ConceptMapFilename), nameof(DbStructureMapping.FmlSourceKey), nameof(DbStructureMapping.FmlFilename),
                nameof(DbStructureMapping.Relationship), nameof(DbStructureMapping.ExplicitNoMap), nameof(DbStructureMapping.Comments),
                nameof(DbStructureMapping.TechnicalNotes), nameof(DbStructureMapping.SourceStructureKey), nameof(DbStructureMapping.SourceStructureId),
                nameof(DbStructureMapping.SourceStructureUrl), nameof(DbStructureMapping.TargetStructureKey), nameof(DbStructureMapping.TargetStructureId),
                nameof(DbStructureMapping.TargetStructureUrl), nameof(DbStructureMapping.IsFallback), nameof(DbStructureMapping.ConceptDomainRelationship),
                nameof(DbStructureMapping.ValueDomainRelationship),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbStructureMapping> rows = DbStructureMapping.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbStructureMapping.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbStructureMapping.SourceFhirSequence), nameof(DbStructureMapping.TargetFhirSequence), nameof(DbStructureMapping.Relationship), nameof(DbStructureMapping.ConceptDomainRelationship), nameof(DbStructureMapping.ValueDomainRelationship)],
                    [nameof(DbStructureMapping.ExplicitNoMap), nameof(DbStructureMapping.IsFallback)]);
                return new TableImage<DbStructureMapping, DbStructureMappingOperand>(rows.Select(row => DbStructureMappingOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElementMapping), DbElementMapping.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbElementMapping.Key), nameof(DbElementMapping.SourceFhirPackageKey), nameof(DbElementMapping.SourceFhirSequence),
                nameof(DbElementMapping.TargetFhirPackageKey), nameof(DbElementMapping.TargetFhirSequence), nameof(DbElementMapping.ConceptMapSourceKey),
                nameof(DbElementMapping.ConceptMapFilename), nameof(DbElementMapping.FmlSourceKey), nameof(DbElementMapping.FmlFilename),
                nameof(DbElementMapping.Relationship), nameof(DbElementMapping.ExplicitNoMap), nameof(DbElementMapping.Comments),
                nameof(DbElementMapping.TechnicalNotes), nameof(DbElementMapping.StructureMappingKey), nameof(DbElementMapping.SourceStructureKey),
                nameof(DbElementMapping.SourceElementKey), nameof(DbElementMapping.SourceElementId), nameof(DbElementMapping.TargetStructureKey),
                nameof(DbElementMapping.TargetElementKey), nameof(DbElementMapping.TargetElementId), nameof(DbElementMapping.FmlIsSimpleCopy),
                nameof(DbElementMapping.ConceptDomainRelationship), nameof(DbElementMapping.ValueDomainRelationship),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementMapping> rows = DbElementMapping.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementMapping.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElementMapping.SourceFhirSequence), nameof(DbElementMapping.TargetFhirSequence), nameof(DbElementMapping.Relationship), nameof(DbElementMapping.ConceptDomainRelationship), nameof(DbElementMapping.ValueDomainRelationship)],
                    [nameof(DbElementMapping.ExplicitNoMap), nameof(DbElementMapping.FmlIsSimpleCopy)]);
                return new TableImage<DbElementMapping, DbElementMappingOperand>(rows.Select(row => DbElementMappingOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetMapping), DbValueSetMapping.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbValueSetMapping.Key), nameof(DbValueSetMapping.SourceFhirPackageKey), nameof(DbValueSetMapping.SourceFhirSequence),
                nameof(DbValueSetMapping.TargetFhirPackageKey), nameof(DbValueSetMapping.TargetFhirSequence), nameof(DbValueSetMapping.ConceptMapSourceKey),
                nameof(DbValueSetMapping.ConceptMapFilename), nameof(DbValueSetMapping.FmlSourceKey), nameof(DbValueSetMapping.FmlFilename),
                nameof(DbValueSetMapping.Relationship), nameof(DbValueSetMapping.ExplicitNoMap), nameof(DbValueSetMapping.Comments),
                nameof(DbValueSetMapping.TechnicalNotes), nameof(DbValueSetMapping.SourceValueSetKey), nameof(DbValueSetMapping.SourceValueSetId),
                nameof(DbValueSetMapping.SourceValueSetUrl), nameof(DbValueSetMapping.SourceValueSetVersion), nameof(DbValueSetMapping.TargetValueSetKey),
                nameof(DbValueSetMapping.TargetValueSetId), nameof(DbValueSetMapping.TargetValueSetUrl), nameof(DbValueSetMapping.TargetValueSetVersion),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetMapping> rows = DbValueSetMapping.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetMapping.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSetMapping.SourceFhirSequence), nameof(DbValueSetMapping.TargetFhirSequence), nameof(DbValueSetMapping.Relationship)],
                    [nameof(DbValueSetMapping.ExplicitNoMap)]);
                return new TableImage<DbValueSetMapping, DbValueSetMappingOperand>(rows.Select(row => DbValueSetMappingOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetConceptMapping), DbValueSetConceptMapping.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbValueSetConceptMapping.Key), nameof(DbValueSetConceptMapping.SourceFhirPackageKey), nameof(DbValueSetConceptMapping.SourceFhirSequence),
                nameof(DbValueSetConceptMapping.TargetFhirPackageKey), nameof(DbValueSetConceptMapping.TargetFhirSequence), nameof(DbValueSetConceptMapping.ConceptMapSourceKey),
                nameof(DbValueSetConceptMapping.ConceptMapFilename), nameof(DbValueSetConceptMapping.FmlSourceKey), nameof(DbValueSetConceptMapping.FmlFilename),
                nameof(DbValueSetConceptMapping.Relationship), nameof(DbValueSetConceptMapping.ExplicitNoMap), nameof(DbValueSetConceptMapping.Comments),
                nameof(DbValueSetConceptMapping.TechnicalNotes), nameof(DbValueSetConceptMapping.ValueSetMappingKey), nameof(DbValueSetConceptMapping.SourceValueSetKey),
                nameof(DbValueSetConceptMapping.SourceValueSetConceptKey), nameof(DbValueSetConceptMapping.SourceSystem), nameof(DbValueSetConceptMapping.SourceCode),
                nameof(DbValueSetConceptMapping.TargetValueSetKey), nameof(DbValueSetConceptMapping.TargetValueSetConceptKey), nameof(DbValueSetConceptMapping.TargetSystem),
                nameof(DbValueSetConceptMapping.TargetCode),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetConceptMapping> rows = DbValueSetConceptMapping.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetConceptMapping.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSetConceptMapping.SourceFhirSequence), nameof(DbValueSetConceptMapping.TargetFhirSequence), nameof(DbValueSetConceptMapping.Relationship)],
                    [nameof(DbValueSetConceptMapping.ExplicitNoMap)]);
                return new TableImage<DbValueSetConceptMapping, DbValueSetConceptMappingOperand>(rows.Select(row => DbValueSetConceptMappingOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbProfileMapOwnerFacts), DbProfileMapOwnerFacts.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbProfileMapOwnerFacts.Key), nameof(DbProfileMapOwnerFacts.AnnotationKey), nameof(DbProfileMapOwnerFacts.InputSnapshotId),
                nameof(DbProfileMapOwnerFacts.SourceFhirPackageKey), nameof(DbProfileMapOwnerFacts.TargetFhirPackageKey), nameof(DbProfileMapOwnerFacts.GroupSourceCanonical),
                nameof(DbProfileMapOwnerFacts.GroupSourceVersion), nameof(DbProfileMapOwnerFacts.GroupTargetCanonical), nameof(DbProfileMapOwnerFacts.GroupTargetVersion),
                nameof(DbProfileMapOwnerFacts.SourceOwnerContext), nameof(DbProfileMapOwnerFacts.TargetOwnerContext), nameof(DbProfileMapOwnerFacts.SourceStructureKey),
                nameof(DbProfileMapOwnerFacts.TargetStructureKey), nameof(DbProfileMapOwnerFacts.ElementMappingKey), nameof(DbProfileMapOwnerFacts.ResolutionState),
                nameof(DbProfileMapOwnerFacts.SourceElementId), nameof(DbProfileMapOwnerFacts.TargetElementId), nameof(DbProfileMapOwnerFacts.MappingSourceFileKey),
                nameof(DbProfileMapOwnerFacts.SourceRelativeFilename), nameof(DbProfileMapOwnerFacts.ConceptMapId), nameof(DbProfileMapOwnerFacts.ConceptMapUrl),
                nameof(DbProfileMapOwnerFacts.GroupIndex), nameof(DbProfileMapOwnerFacts.SourceIndex), nameof(DbProfileMapOwnerFacts.TargetIndex),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbProfileMapOwnerFacts> rows = DbProfileMapOwnerFacts.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbProfileMapOwnerFacts.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbProfileMapOwnerFacts.ResolutionState)],
                    []);
                return new TableImage<DbProfileMapOwnerFacts, DbProfileMapOwnerFactsOperand>(rows.Select(row => DbProfileMapOwnerFactsOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbUnsafeCodeElementAnnotation), DbUnsafeCodeElementAnnotation.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbUnsafeCodeElementAnnotation.Key), nameof(DbUnsafeCodeElementAnnotation.SourceFhirPackageKey), nameof(DbUnsafeCodeElementAnnotation.TargetFhirPackageKey),
                nameof(DbUnsafeCodeElementAnnotation.SourceElementId), nameof(DbUnsafeCodeElementAnnotation.TargetElementId), nameof(DbUnsafeCodeElementAnnotation.RequiresOmittedMandatoryCode),
                nameof(DbUnsafeCodeElementAnnotation.SourceRelativeFilename), nameof(DbUnsafeCodeElementAnnotation.ConceptMapId), nameof(DbUnsafeCodeElementAnnotation.ConceptMapUrl),
                nameof(DbUnsafeCodeElementAnnotation.GroupIndex), nameof(DbUnsafeCodeElementAnnotation.SourceIndex), nameof(DbUnsafeCodeElementAnnotation.TargetIndex),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbUnsafeCodeElementAnnotation> rows = DbUnsafeCodeElementAnnotation.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbUnsafeCodeElementAnnotation.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [],
                    [nameof(DbUnsafeCodeElementAnnotation.RequiresOmittedMandatoryCode)]);
                return new TableImage<DbUnsafeCodeElementAnnotation, DbUnsafeCodeElementAnnotationOperand>(rows.Select(row => DbUnsafeCodeElementAnnotationOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbUnsafeCodeMapEvidence), DbUnsafeCodeMapEvidence.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbUnsafeCodeMapEvidence.Key), nameof(DbUnsafeCodeMapEvidence.SourceFhirPackageKey), nameof(DbUnsafeCodeMapEvidence.TargetFhirPackageKey),
                nameof(DbUnsafeCodeMapEvidence.SourceValueSetUrl), nameof(DbUnsafeCodeMapEvidence.SourceValueSetVersion), nameof(DbUnsafeCodeMapEvidence.TargetValueSetUrl),
                nameof(DbUnsafeCodeMapEvidence.TargetValueSetVersion), nameof(DbUnsafeCodeMapEvidence.SourceSystem), nameof(DbUnsafeCodeMapEvidence.SourceSystemVersion),
                nameof(DbUnsafeCodeMapEvidence.SourceCode), nameof(DbUnsafeCodeMapEvidence.TargetSystem), nameof(DbUnsafeCodeMapEvidence.TargetSystemVersion),
                nameof(DbUnsafeCodeMapEvidence.TargetCode), nameof(DbUnsafeCodeMapEvidence.Relationship), nameof(DbUnsafeCodeMapEvidence.ExplicitNoMap),
                nameof(DbUnsafeCodeMapEvidence.SourceRelativeFilename), nameof(DbUnsafeCodeMapEvidence.ConceptMapId), nameof(DbUnsafeCodeMapEvidence.ConceptMapUrl),
                nameof(DbUnsafeCodeMapEvidence.GroupIndex), nameof(DbUnsafeCodeMapEvidence.SourceIndex), nameof(DbUnsafeCodeMapEvidence.TargetIndex),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbUnsafeCodeMapEvidence> rows = DbUnsafeCodeMapEvidence.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbUnsafeCodeMapEvidence.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbUnsafeCodeMapEvidence.Relationship)],
                    [nameof(DbUnsafeCodeMapEvidence.ExplicitNoMap)]);
                return new TableImage<DbUnsafeCodeMapEvidence, DbUnsafeCodeMapEvidenceOperand>(rows.Select(row => DbUnsafeCodeMapEvidenceOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbUnsafeCodeRule), DbUnsafeCodeRule.DefaultTableName, XVerOperandFamily.Maps,
            [
                nameof(DbUnsafeCodeRule.Key), nameof(DbUnsafeCodeRule.System), nameof(DbUnsafeCodeRule.Version),
                nameof(DbUnsafeCodeRule.Code), nameof(DbUnsafeCodeRule.NoMapOnly), nameof(DbUnsafeCodeRule.ExcludedRelationshipLiteral),
                nameof(DbUnsafeCodeRule.ExcludedVersionLiteral), nameof(DbUnsafeCodeRule.SourceRelativeFilename), nameof(DbUnsafeCodeRule.EntryIndex),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbUnsafeCodeRule> rows = DbUnsafeCodeRule.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbUnsafeCodeRule.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [],
                    [nameof(DbUnsafeCodeRule.NoMapOnly)]);
                return new TableImage<DbUnsafeCodeRule, DbUnsafeCodeRuleOperand>(rows.Select(row => DbUnsafeCodeRuleOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbExtensionSubstitution), DbExtensionSubstitution.DefaultTableName, XVerOperandFamily.Support,
            [
                nameof(DbExtensionSubstitution.Key), nameof(DbExtensionSubstitution.ReplacementUrl), nameof(DbExtensionSubstitution.ReplacementName),
                nameof(DbExtensionSubstitution.ReplacementSourcePackage), nameof(DbExtensionSubstitution.SourceVersion), nameof(DbExtensionSubstitution.SourceFhirSequence),
                nameof(DbExtensionSubstitution.SourceElementId), nameof(DbExtensionSubstitution.SourceTypeReplacement), nameof(DbExtensionSubstitution.SourceFromContextElement),
                nameof(DbExtensionSubstitution.SourceFromContextExpandedLiteral), nameof(DbExtensionSubstitution.IsModifier), nameof(DbExtensionSubstitution.ContextsLiteral),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbExtensionSubstitution> rows = DbExtensionSubstitution.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbExtensionSubstitution.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbExtensionSubstitution.SourceFhirSequence)],
                    [nameof(DbExtensionSubstitution.IsModifier)]);
                return new TableImage<DbExtensionSubstitution, DbExtensionSubstitutionOperand>(rows.Select(row => DbExtensionSubstitutionOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbExternalInclusion), DbExternalInclusion.DefaultTableName, XVerOperandFamily.Support,
            [
                nameof(DbExternalInclusion.Key), nameof(DbExternalInclusion.ResourceType), nameof(DbExternalInclusion.Id),
                nameof(DbExternalInclusion.Name), nameof(DbExternalInclusion.Version), nameof(DbExternalInclusion.OverrideVersion),
                nameof(DbExternalInclusion.UnversionedUrl), nameof(DbExternalInclusion.VersionedUrl), nameof(DbExternalInclusion.Reason),
                nameof(DbExternalInclusion.IncludeInPackages), nameof(DbExternalInclusion.Json),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbExternalInclusion> rows = DbExternalInclusion.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbExternalInclusion.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbExternalInclusion.ResourceType)],
                    [nameof(DbExternalInclusion.OverrideVersion)]);
                return new TableImage<DbExternalInclusion, DbExternalInclusionOperand>(rows.Select(row => DbExternalInclusionOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbFhirTypeValueSet), DbFhirTypeValueSet.DefaultTableName, XVerOperandFamily.Support,
            [
                nameof(DbFhirTypeValueSet.Key), nameof(DbFhirTypeValueSet.UnversionedUrl),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbFhirTypeValueSet> rows = DbFhirTypeValueSet.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbFhirTypeValueSet.Key)]);
                return new TableImage<DbFhirTypeValueSet, DbFhirTypeValueSetOperand>(rows.Select(row => DbFhirTypeValueSetOperand.Copy(row)), row => row.ToRow());
            },
        },
        new(typeof(DbValueSet), DbValueSet.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbValueSet.Key), nameof(DbValueSet.FhirPackageKey), nameof(DbValueSet.Id),
                nameof(DbValueSet.VersionedUrl), nameof(DbValueSet.UnversionedUrl), nameof(DbValueSet.Name),
                nameof(DbValueSet.Version), nameof(DbValueSet.VersionAlgorithmString), nameof(DbValueSet.VersionAlgorithmCoding),
                nameof(DbValueSet.Status), nameof(DbValueSet.Title), nameof(DbValueSet.Description),
                nameof(DbValueSet.Purpose), nameof(DbValueSet.Narrative), nameof(DbValueSet.StandardStatus),
                nameof(DbValueSet.WorkGroup), nameof(DbValueSet.FhirMaturity), nameof(DbValueSet.IsExperimental),
                nameof(DbValueSet.LastChangedDate), nameof(DbValueSet.Publisher), nameof(DbValueSet.Copyright),
                nameof(DbValueSet.CopyrightLabel), nameof(DbValueSet.ApprovalDate), nameof(DbValueSet.LastReviewDate),
                nameof(DbValueSet.EffectivePeriodStart), nameof(DbValueSet.EffectivePeriodEnd), nameof(DbValueSet.Topic),
                nameof(DbValueSet.RelatedArtifacts), nameof(DbValueSet.Jurisdictions), nameof(DbValueSet.UseContexts),
                nameof(DbValueSet.Contacts), nameof(DbValueSet.Authors), nameof(DbValueSet.Editors),
                nameof(DbValueSet.Reviewers), nameof(DbValueSet.Endorsers), nameof(DbValueSet.RootExtensions),
                nameof(DbValueSet.SourcePackageMoniker), nameof(DbValueSet.CanExpand), nameof(DbValueSet.HasEscapeValveCode),
                nameof(DbValueSet.Message), nameof(DbValueSet.IsExcluded), nameof(DbValueSet.ConceptCount),
                nameof(DbValueSet.ActiveConcreteConceptCount), nameof(DbValueSet.ReferencedSystems), nameof(DbValueSet.BindingCountCore),
                nameof(DbValueSet.StrongestBindingCore), nameof(DbValueSet.StrongestBindingCoreCode), nameof(DbValueSet.StrongestBindingCoreCoding),
                nameof(DbValueSet.BindingCountExtended), nameof(DbValueSet.StrongestBindingExtended), nameof(DbValueSet.StrongestBindingExtendedCode),
                nameof(DbValueSet.StrongestBindingExtendedCoding), nameof(DbValueSet.Compose),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSet> rows = DbValueSet.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSet.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSet.VersionAlgorithmCoding), nameof(DbValueSet.Status), nameof(DbValueSet.Narrative), nameof(DbValueSet.LastChangedDate), nameof(DbValueSet.EffectivePeriodStart), nameof(DbValueSet.EffectivePeriodEnd), nameof(DbValueSet.Topic), nameof(DbValueSet.RelatedArtifacts), nameof(DbValueSet.Jurisdictions), nameof(DbValueSet.UseContexts), nameof(DbValueSet.Contacts), nameof(DbValueSet.Authors), nameof(DbValueSet.Editors), nameof(DbValueSet.Reviewers), nameof(DbValueSet.Endorsers), nameof(DbValueSet.RootExtensions), nameof(DbValueSet.StrongestBindingCore), nameof(DbValueSet.StrongestBindingCoreCode), nameof(DbValueSet.StrongestBindingCoreCoding), nameof(DbValueSet.StrongestBindingExtended), nameof(DbValueSet.StrongestBindingExtendedCode), nameof(DbValueSet.StrongestBindingExtendedCoding), nameof(DbValueSet.Compose)],
                    [nameof(DbValueSet.IsExperimental), nameof(DbValueSet.CanExpand), nameof(DbValueSet.HasEscapeValveCode), nameof(DbValueSet.IsExcluded)]);
                return new TableImage<DbValueSet, DbValueSetOperand>(rows.Select(row => DbValueSetOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetConcept), DbValueSetConcept.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbValueSetConcept.Key), nameof(DbValueSetConcept.FhirPackageKey), nameof(DbValueSetConcept.ValueSetKey),
                nameof(DbValueSetConcept.System), nameof(DbValueSetConcept.SystemVersion), nameof(DbValueSetConcept.Code),
                nameof(DbValueSetConcept.Display), nameof(DbValueSetConcept.Inactive), nameof(DbValueSetConcept.Abstract),
                nameof(DbValueSetConcept.Properties), nameof(DbValueSetConcept.CodeSystemConceptKey),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetConcept> rows = DbValueSetConcept.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetConcept.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [],
                    [nameof(DbValueSetConcept.Inactive), nameof(DbValueSetConcept.Abstract)]);
                return new TableImage<DbValueSetConcept, DbValueSetConceptOperand>(rows.Select(row => DbValueSetConceptOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbCodeSystem), DbCodeSystem.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbCodeSystem.Key), nameof(DbCodeSystem.FhirPackageKey), nameof(DbCodeSystem.Id),
                nameof(DbCodeSystem.VersionedUrl), nameof(DbCodeSystem.UnversionedUrl), nameof(DbCodeSystem.Name),
                nameof(DbCodeSystem.Version), nameof(DbCodeSystem.VersionAlgorithmString), nameof(DbCodeSystem.VersionAlgorithmCoding),
                nameof(DbCodeSystem.Status), nameof(DbCodeSystem.Title), nameof(DbCodeSystem.Description),
                nameof(DbCodeSystem.Purpose), nameof(DbCodeSystem.Narrative), nameof(DbCodeSystem.StandardStatus),
                nameof(DbCodeSystem.WorkGroup), nameof(DbCodeSystem.FhirMaturity), nameof(DbCodeSystem.IsExperimental),
                nameof(DbCodeSystem.LastChangedDate), nameof(DbCodeSystem.Publisher), nameof(DbCodeSystem.Copyright),
                nameof(DbCodeSystem.CopyrightLabel), nameof(DbCodeSystem.ApprovalDate), nameof(DbCodeSystem.LastReviewDate),
                nameof(DbCodeSystem.EffectivePeriodStart), nameof(DbCodeSystem.EffectivePeriodEnd), nameof(DbCodeSystem.Topic),
                nameof(DbCodeSystem.RelatedArtifacts), nameof(DbCodeSystem.Jurisdictions), nameof(DbCodeSystem.UseContexts),
                nameof(DbCodeSystem.Contacts), nameof(DbCodeSystem.Authors), nameof(DbCodeSystem.Editors),
                nameof(DbCodeSystem.Reviewers), nameof(DbCodeSystem.Endorsers), nameof(DbCodeSystem.RootExtensions),
                nameof(DbCodeSystem.SourcePackageMoniker), nameof(DbCodeSystem.IsCaseSensitive), nameof(DbCodeSystem.ValueSetVersioned),
                nameof(DbCodeSystem.ValueSetUnversioned), nameof(DbCodeSystem.HierarchyMeaning), nameof(DbCodeSystem.IsCompositional),
                nameof(DbCodeSystem.VersionNeeded), nameof(DbCodeSystem.Content), nameof(DbCodeSystem.SupplementsVersioned),
                nameof(DbCodeSystem.SupplementsUnversioned), nameof(DbCodeSystem.Count),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbCodeSystem> rows = DbCodeSystem.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbCodeSystem.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbCodeSystem.VersionAlgorithmCoding), nameof(DbCodeSystem.Status), nameof(DbCodeSystem.Narrative), nameof(DbCodeSystem.LastChangedDate), nameof(DbCodeSystem.EffectivePeriodStart), nameof(DbCodeSystem.EffectivePeriodEnd), nameof(DbCodeSystem.Topic), nameof(DbCodeSystem.RelatedArtifacts), nameof(DbCodeSystem.Jurisdictions), nameof(DbCodeSystem.UseContexts), nameof(DbCodeSystem.Contacts), nameof(DbCodeSystem.Authors), nameof(DbCodeSystem.Editors), nameof(DbCodeSystem.Reviewers), nameof(DbCodeSystem.Endorsers), nameof(DbCodeSystem.RootExtensions), nameof(DbCodeSystem.HierarchyMeaning), nameof(DbCodeSystem.Content)],
                    [nameof(DbCodeSystem.IsExperimental), nameof(DbCodeSystem.IsCaseSensitive), nameof(DbCodeSystem.IsCompositional), nameof(DbCodeSystem.VersionNeeded)]);
                return new TableImage<DbCodeSystem, DbCodeSystemOperand>(rows.Select(row => DbCodeSystemOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbCodeSystemConcept), DbCodeSystemConcept.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbCodeSystemConcept.Key), nameof(DbCodeSystemConcept.FhirPackageKey), nameof(DbCodeSystemConcept.CodeSystemKey),
                nameof(DbCodeSystemConcept.FlatOrder), nameof(DbCodeSystemConcept.RelativeOrder), nameof(DbCodeSystemConcept.Code),
                nameof(DbCodeSystemConcept.Display), nameof(DbCodeSystemConcept.Definition), nameof(DbCodeSystemConcept.Designations),
                nameof(DbCodeSystemConcept.Properties), nameof(DbCodeSystemConcept.ParentConceptKey), nameof(DbCodeSystemConcept.ChildConceptCount),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbCodeSystemConcept> rows = DbCodeSystemConcept.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbCodeSystemConcept.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbCodeSystemConcept.Designations), nameof(DbCodeSystemConcept.Properties)],
                    []);
                return new TableImage<DbCodeSystemConcept, DbCodeSystemConceptOperand>(rows.Select(row => DbCodeSystemConceptOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbCodeSystemConceptProperty), DbCodeSystemConceptProperty.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbCodeSystemConceptProperty.Key), nameof(DbCodeSystemConceptProperty.FhirPackageKey), nameof(DbCodeSystemConceptProperty.CodeSystemConceptKey),
                nameof(DbCodeSystemConceptProperty.CodeSystemPropertyDefinitionKey), nameof(DbCodeSystemConceptProperty.Code), nameof(DbCodeSystemConceptProperty.Type),
                nameof(DbCodeSystemConceptProperty.Value),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbCodeSystemConceptProperty> rows = DbCodeSystemConceptProperty.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbCodeSystemConceptProperty.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbCodeSystemConceptProperty.Type)],
                    []);
                return new TableImage<DbCodeSystemConceptProperty, DbCodeSystemConceptPropertyOperand>(rows.Select(row => DbCodeSystemConceptPropertyOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbCodeSystemPropertyDefinition), DbCodeSystemPropertyDefinition.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbCodeSystemPropertyDefinition.Key), nameof(DbCodeSystemPropertyDefinition.FhirPackageKey), nameof(DbCodeSystemPropertyDefinition.CodeSystemKey),
                nameof(DbCodeSystemPropertyDefinition.Code), nameof(DbCodeSystemPropertyDefinition.Uri), nameof(DbCodeSystemPropertyDefinition.Description),
                nameof(DbCodeSystemPropertyDefinition.Type),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbCodeSystemPropertyDefinition> rows = DbCodeSystemPropertyDefinition.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbCodeSystemPropertyDefinition.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbCodeSystemPropertyDefinition.Type)],
                    []);
                return new TableImage<DbCodeSystemPropertyDefinition, DbCodeSystemPropertyDefinitionOperand>(rows.Select(row => DbCodeSystemPropertyDefinitionOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbCodeSystemFilter), DbCodeSystemFilter.DefaultTableName, XVerOperandFamily.Vocabulary,
            [
                nameof(DbCodeSystemFilter.Key), nameof(DbCodeSystemFilter.FhirPackageKey), nameof(DbCodeSystemFilter.CodeSystemKey),
                nameof(DbCodeSystemFilter.Code), nameof(DbCodeSystemFilter.Description), nameof(DbCodeSystemFilter.Operators),
                nameof(DbCodeSystemFilter.Value),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbCodeSystemFilter> rows = DbCodeSystemFilter.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbCodeSystemFilter.Key)]);
                return new TableImage<DbCodeSystemFilter, DbCodeSystemFilterOperand>(rows.Select(row => DbCodeSystemFilterOperand.Copy(row)), row => row.ToRow());
            },
        },
        new(typeof(DbStructureComparison), DbStructureComparison.DefaultTableName, XVerOperandFamily.StructureComparisons,
            [
                nameof(DbStructureComparison.Key), nameof(DbStructureComparison.SourceFhirPackageKey), nameof(DbStructureComparison.SourceFhirSequence),
                nameof(DbStructureComparison.TargetFhirPackageKey), nameof(DbStructureComparison.TargetFhirSequence), nameof(DbStructureComparison.Steps),
                nameof(DbStructureComparison.Relationship), nameof(DbStructureComparison.NotMapped), nameof(DbStructureComparison.IsIdentical),
                nameof(DbStructureComparison.TechnicalMessage), nameof(DbStructureComparison.UserMessage), nameof(DbStructureComparison.ContentKeyR2),
                nameof(DbStructureComparison.ContentKeyR3), nameof(DbStructureComparison.ContentKeyR4), nameof(DbStructureComparison.ContentKeyR4B),
                nameof(DbStructureComparison.ContentKeyR5), nameof(DbStructureComparison.ContentKeyR6), nameof(DbStructureComparison.SourceCanonicalVersioned),
                nameof(DbStructureComparison.SourceCanonicalUnversioned), nameof(DbStructureComparison.SourceId), nameof(DbStructureComparison.SourceName),
                nameof(DbStructureComparison.SourceVersion), nameof(DbStructureComparison.TargetCanonicalVersioned), nameof(DbStructureComparison.TargetCanonicalUnversioned),
                nameof(DbStructureComparison.TargetId), nameof(DbStructureComparison.TargetName), nameof(DbStructureComparison.TargetVersion),
                nameof(DbStructureComparison.SourceStructureKey), nameof(DbStructureComparison.TargetStructureKey), nameof(DbStructureComparison.StructureMappingKey),
                nameof(DbStructureComparison.ElementRelativePathsAreIdentical), nameof(DbStructureComparison.IsBasedOnFallbackMapping), nameof(DbStructureComparison.ConceptDomainRelationship),
                nameof(DbStructureComparison.ValueDomainRelationship),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbStructureComparison> rows = DbStructureComparison.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbStructureComparison.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbStructureComparison.SourceFhirSequence), nameof(DbStructureComparison.TargetFhirSequence), nameof(DbStructureComparison.Relationship), nameof(DbStructureComparison.ConceptDomainRelationship), nameof(DbStructureComparison.ValueDomainRelationship)],
                    [nameof(DbStructureComparison.NotMapped), nameof(DbStructureComparison.IsIdentical), nameof(DbStructureComparison.ElementRelativePathsAreIdentical), nameof(DbStructureComparison.IsBasedOnFallbackMapping)]);
                return new TableImage<DbStructureComparison, DbStructureComparisonOperand>(rows.Select(row => DbStructureComparisonOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElementComparison), DbElementComparison.DefaultTableName, XVerOperandFamily.StructureComparisons,
            [
                nameof(DbElementComparison.Key), nameof(DbElementComparison.SourceFhirPackageKey), nameof(DbElementComparison.SourceFhirSequence),
                nameof(DbElementComparison.TargetFhirPackageKey), nameof(DbElementComparison.TargetFhirSequence), nameof(DbElementComparison.Steps),
                nameof(DbElementComparison.Relationship), nameof(DbElementComparison.NotMapped), nameof(DbElementComparison.IsIdentical),
                nameof(DbElementComparison.TechnicalMessage), nameof(DbElementComparison.UserMessage), nameof(DbElementComparison.ContentKeyR2),
                nameof(DbElementComparison.ContentKeyR3), nameof(DbElementComparison.ContentKeyR4), nameof(DbElementComparison.ContentKeyR4B),
                nameof(DbElementComparison.ContentKeyR5), nameof(DbElementComparison.ContentKeyR6), nameof(DbElementComparison.StructureComparisonKey),
                nameof(DbElementComparison.SourceStructureKey), nameof(DbElementComparison.SourceElementKey), nameof(DbElementComparison.SourceElementId),
                nameof(DbElementComparison.TargetStructureKey), nameof(DbElementComparison.TargetElementKey), nameof(DbElementComparison.TargetElementId),
                nameof(DbElementComparison.ElementMappingKey), nameof(DbElementComparison.ConceptDomainRelationship), nameof(DbElementComparison.ValueDomainRelationship),
                nameof(DbElementComparison.RelativePathsAreIdentical), nameof(DbElementComparison.BoundValueSetComparisonKey), nameof(DbElementComparison.BoundValueSetRelationship),
                nameof(DbElementComparison.SourceRequiresMoreValues), nameof(DbElementComparison.TargetRequiresMoreValues), nameof(DbElementComparison.SourceAllowsMoreValues),
                nameof(DbElementComparison.TargetAllowsMoreValues), nameof(DbElementComparison.TypeRelationship), nameof(DbElementComparison.TypeMessage),
                nameof(DbElementComparison.TargetProfileRelationship), nameof(DbElementComparison.TargetProfileMessage), nameof(DbElementComparison.TypeProfileRelationship),
                nameof(DbElementComparison.TypeProfileMessage),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementComparison> rows = DbElementComparison.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementComparison.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElementComparison.SourceFhirSequence), nameof(DbElementComparison.TargetFhirSequence), nameof(DbElementComparison.Relationship), nameof(DbElementComparison.ConceptDomainRelationship), nameof(DbElementComparison.ValueDomainRelationship), nameof(DbElementComparison.BoundValueSetRelationship), nameof(DbElementComparison.TypeRelationship), nameof(DbElementComparison.TargetProfileRelationship), nameof(DbElementComparison.TypeProfileRelationship)],
                    [nameof(DbElementComparison.NotMapped), nameof(DbElementComparison.IsIdentical), nameof(DbElementComparison.RelativePathsAreIdentical), nameof(DbElementComparison.SourceRequiresMoreValues), nameof(DbElementComparison.TargetRequiresMoreValues), nameof(DbElementComparison.SourceAllowsMoreValues), nameof(DbElementComparison.TargetAllowsMoreValues)]);
                return new TableImage<DbElementComparison, DbElementComparisonOperand>(rows.Select(row => DbElementComparisonOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElementTypeComparison), DbElementTypeComparison.DefaultTableName, XVerOperandFamily.StructureComparisons,
            [
                nameof(DbElementTypeComparison.Key), nameof(DbElementTypeComparison.SourceFhirPackageKey), nameof(DbElementTypeComparison.SourceFhirSequence),
                nameof(DbElementTypeComparison.TargetFhirPackageKey), nameof(DbElementTypeComparison.TargetFhirSequence), nameof(DbElementTypeComparison.Steps),
                nameof(DbElementTypeComparison.Relationship), nameof(DbElementTypeComparison.NotMapped), nameof(DbElementTypeComparison.IsIdentical),
                nameof(DbElementTypeComparison.TechnicalMessage), nameof(DbElementTypeComparison.UserMessage), nameof(DbElementTypeComparison.ContentKeyR2),
                nameof(DbElementTypeComparison.ContentKeyR3), nameof(DbElementTypeComparison.ContentKeyR4), nameof(DbElementTypeComparison.ContentKeyR4B),
                nameof(DbElementTypeComparison.ContentKeyR5), nameof(DbElementTypeComparison.ContentKeyR6), nameof(DbElementTypeComparison.ElementComparisonKey),
                nameof(DbElementTypeComparison.SourceElementKey), nameof(DbElementTypeComparison.SourceElementId), nameof(DbElementTypeComparison.SourceElementTypeKey),
                nameof(DbElementTypeComparison.SourceTypeLiteral), nameof(DbElementTypeComparison.TargetElementKey), nameof(DbElementTypeComparison.TargetElementId),
                nameof(DbElementTypeComparison.TargetElementTypeKey), nameof(DbElementTypeComparison.TargetTypeLiteral), nameof(DbElementTypeComparison.TargetProfileRelationship),
                nameof(DbElementTypeComparison.TargetProfileMessage), nameof(DbElementTypeComparison.TypeProfileRelationship), nameof(DbElementTypeComparison.TypeProfileMessage),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementTypeComparison> rows = DbElementTypeComparison.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementTypeComparison.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElementTypeComparison.SourceFhirSequence), nameof(DbElementTypeComparison.TargetFhirSequence), nameof(DbElementTypeComparison.Relationship), nameof(DbElementTypeComparison.TargetProfileRelationship), nameof(DbElementTypeComparison.TypeProfileRelationship)],
                    [nameof(DbElementTypeComparison.NotMapped), nameof(DbElementTypeComparison.IsIdentical)]);
                return new TableImage<DbElementTypeComparison, DbElementTypeComparisonOperand>(rows.Select(row => DbElementTypeComparisonOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetComparison), DbValueSetComparison.DefaultTableName, XVerOperandFamily.VocabularyComparisons,
            [
                nameof(DbValueSetComparison.Key), nameof(DbValueSetComparison.SourceFhirPackageKey), nameof(DbValueSetComparison.SourceFhirSequence),
                nameof(DbValueSetComparison.TargetFhirPackageKey), nameof(DbValueSetComparison.TargetFhirSequence), nameof(DbValueSetComparison.Steps),
                nameof(DbValueSetComparison.Relationship), nameof(DbValueSetComparison.NotMapped), nameof(DbValueSetComparison.IsIdentical),
                nameof(DbValueSetComparison.TechnicalMessage), nameof(DbValueSetComparison.UserMessage), nameof(DbValueSetComparison.ContentKeyR2),
                nameof(DbValueSetComparison.ContentKeyR3), nameof(DbValueSetComparison.ContentKeyR4), nameof(DbValueSetComparison.ContentKeyR4B),
                nameof(DbValueSetComparison.ContentKeyR5), nameof(DbValueSetComparison.ContentKeyR6), nameof(DbValueSetComparison.SourceCanonicalVersioned),
                nameof(DbValueSetComparison.SourceCanonicalUnversioned), nameof(DbValueSetComparison.SourceId), nameof(DbValueSetComparison.SourceName),
                nameof(DbValueSetComparison.SourceVersion), nameof(DbValueSetComparison.TargetCanonicalVersioned), nameof(DbValueSetComparison.TargetCanonicalUnversioned),
                nameof(DbValueSetComparison.TargetId), nameof(DbValueSetComparison.TargetName), nameof(DbValueSetComparison.TargetVersion),
                nameof(DbValueSetComparison.SourceValueSetKey), nameof(DbValueSetComparison.TargetValueSetKey), nameof(DbValueSetComparison.ValueSetMappingKey),
                nameof(DbValueSetComparison.CodeLiteralsAreIdentical),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetComparison> rows = DbValueSetComparison.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetComparison.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSetComparison.SourceFhirSequence), nameof(DbValueSetComparison.TargetFhirSequence), nameof(DbValueSetComparison.Relationship)],
                    [nameof(DbValueSetComparison.NotMapped), nameof(DbValueSetComparison.IsIdentical), nameof(DbValueSetComparison.CodeLiteralsAreIdentical)]);
                return new TableImage<DbValueSetComparison, DbValueSetComparisonOperand>(rows.Select(row => DbValueSetComparisonOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetConceptComparison), DbValueSetConceptComparison.DefaultTableName, XVerOperandFamily.VocabularyComparisons,
            [
                nameof(DbValueSetConceptComparison.Key), nameof(DbValueSetConceptComparison.SourceFhirPackageKey), nameof(DbValueSetConceptComparison.SourceFhirSequence),
                nameof(DbValueSetConceptComparison.TargetFhirPackageKey), nameof(DbValueSetConceptComparison.TargetFhirSequence), nameof(DbValueSetConceptComparison.Steps),
                nameof(DbValueSetConceptComparison.Relationship), nameof(DbValueSetConceptComparison.NotMapped), nameof(DbValueSetConceptComparison.IsIdentical),
                nameof(DbValueSetConceptComparison.TechnicalMessage), nameof(DbValueSetConceptComparison.UserMessage), nameof(DbValueSetConceptComparison.ContentKeyR2),
                nameof(DbValueSetConceptComparison.ContentKeyR3), nameof(DbValueSetConceptComparison.ContentKeyR4), nameof(DbValueSetConceptComparison.ContentKeyR4B),
                nameof(DbValueSetConceptComparison.ContentKeyR5), nameof(DbValueSetConceptComparison.ContentKeyR6), nameof(DbValueSetConceptComparison.ValueSetComparisonKey),
                nameof(DbValueSetConceptComparison.SourceValueSetKey), nameof(DbValueSetConceptComparison.SourceConceptKey), nameof(DbValueSetConceptComparison.SourceSystem),
                nameof(DbValueSetConceptComparison.SourceCode), nameof(DbValueSetConceptComparison.TargetValueSetKey), nameof(DbValueSetConceptComparison.TargetConceptKey),
                nameof(DbValueSetConceptComparison.TargetSystem), nameof(DbValueSetConceptComparison.TargetCode), nameof(DbValueSetConceptComparison.ValueSetConceptMappingKey),
                nameof(DbValueSetConceptComparison.CodeLiteralsAreIdentical), nameof(DbValueSetConceptComparison.SourceCodeTreatedAsEscapeValve), nameof(DbValueSetConceptComparison.TargetCodeTreatedAsEscapeValve),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetConceptComparison> rows = DbValueSetConceptComparison.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetConceptComparison.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSetConceptComparison.SourceFhirSequence), nameof(DbValueSetConceptComparison.TargetFhirSequence), nameof(DbValueSetConceptComparison.Relationship)],
                    [nameof(DbValueSetConceptComparison.NotMapped), nameof(DbValueSetConceptComparison.IsIdentical), nameof(DbValueSetConceptComparison.CodeLiteralsAreIdentical), nameof(DbValueSetConceptComparison.SourceCodeTreatedAsEscapeValve), nameof(DbValueSetConceptComparison.TargetCodeTreatedAsEscapeValve)]);
                return new TableImage<DbValueSetConceptComparison, DbValueSetConceptComparisonOperand>(rows.Select(row => DbValueSetConceptComparisonOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbUnsafeCodePairState), DbUnsafeCodePairState.DefaultTableName, XVerOperandFamily.PairControl,
            [
                nameof(DbUnsafeCodePairState.Key), nameof(DbUnsafeCodePairState.SourceFhirPackageKey), nameof(DbUnsafeCodePairState.TargetFhirPackageKey),
                nameof(DbUnsafeCodePairState.InputRevision), nameof(DbUnsafeCodePairState.ComparisonRevision), nameof(DbUnsafeCodePairState.OutcomeInputRevision),
                nameof(DbUnsafeCodePairState.OutcomeComparisonRevision), nameof(DbUnsafeCodePairState.OutcomeFormatVersion),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbUnsafeCodePairState> rows = DbUnsafeCodePairState.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbUnsafeCodePairState.Key)]);
                return new TableImage<DbUnsafeCodePairState, DbUnsafeCodePairStateOperand>(rows.Select(row => DbUnsafeCodePairStateOperand.Copy(row)), row => row.ToRow());
            },
        },
        new(typeof(DbStructureOutcome), DbStructureOutcome.DefaultTableName, XVerOperandFamily.StructureOutcomes,
            [
                nameof(DbStructureOutcome.Key), nameof(DbStructureOutcome.SourceFhirPackageKey), nameof(DbStructureOutcome.SourceFhirSequence),
                nameof(DbStructureOutcome.TargetFhirPackageKey), nameof(DbStructureOutcome.TargetFhirSequence), nameof(DbStructureOutcome.RequiresXVerDefinition),
                nameof(DbStructureOutcome.TotalTargetCount), nameof(DbStructureOutcome.TotalSourceCount), nameof(DbStructureOutcome.IsRenamed),
                nameof(DbStructureOutcome.IsUnmapped), nameof(DbStructureOutcome.IsIdentical), nameof(DbStructureOutcome.IsEquivalent),
                nameof(DbStructureOutcome.IsBroaderThanTarget), nameof(DbStructureOutcome.IsNarrowerThanTarget), nameof(DbStructureOutcome.FullyMapsToThisTarget),
                nameof(DbStructureOutcome.FullyMapsAcrossAllTargets), nameof(DbStructureOutcome.Comments), nameof(DbStructureOutcome.GenLongId),
                nameof(DbStructureOutcome.GenShortId), nameof(DbStructureOutcome.GenUrl), nameof(DbStructureOutcome.GenName),
                nameof(DbStructureOutcome.GenFileName), nameof(DbStructureOutcome.GenArtifactShort), nameof(DbStructureOutcome.GenArtifactDescription),
                nameof(DbStructureOutcome.GenArtifactComment), nameof(DbStructureOutcome.GenMappingComment), nameof(DbStructureOutcome.SourceCanonicalVersioned),
                nameof(DbStructureOutcome.SourceCanonicalUnversioned), nameof(DbStructureOutcome.SourceId), nameof(DbStructureOutcome.SourceName),
                nameof(DbStructureOutcome.SourceVersion), nameof(DbStructureOutcome.TargetCanonicalVersioned), nameof(DbStructureOutcome.TargetCanonicalUnversioned),
                nameof(DbStructureOutcome.TargetId), nameof(DbStructureOutcome.TargetName), nameof(DbStructureOutcome.TargetVersion),
                nameof(DbStructureOutcome.StructureComparisonKey), nameof(DbStructureOutcome.SourceStructureKey), nameof(DbStructureOutcome.SourceArtifactClass),
                nameof(DbStructureOutcome.TargetStructureKey), nameof(DbStructureOutcome.TargetArtifactClass), nameof(DbStructureOutcome.ElementConceptMapLongId),
                nameof(DbStructureOutcome.ElementConceptMapShortId), nameof(DbStructureOutcome.ElementConceptMapUrl), nameof(DbStructureOutcome.ElementConceptMapName),
                nameof(DbStructureOutcome.ElementConceptMapFileName),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbStructureOutcome> rows = DbStructureOutcome.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbStructureOutcome.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbStructureOutcome.SourceFhirSequence), nameof(DbStructureOutcome.TargetFhirSequence), nameof(DbStructureOutcome.SourceArtifactClass), nameof(DbStructureOutcome.TargetArtifactClass)],
                    [nameof(DbStructureOutcome.RequiresXVerDefinition), nameof(DbStructureOutcome.IsRenamed), nameof(DbStructureOutcome.IsUnmapped), nameof(DbStructureOutcome.IsIdentical), nameof(DbStructureOutcome.IsEquivalent), nameof(DbStructureOutcome.IsBroaderThanTarget), nameof(DbStructureOutcome.IsNarrowerThanTarget), nameof(DbStructureOutcome.FullyMapsToThisTarget), nameof(DbStructureOutcome.FullyMapsAcrossAllTargets)]);
                return new TableImage<DbStructureOutcome, DbStructureOutcomeOperand>(rows.Select(row => DbStructureOutcomeOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElementOutcome), DbElementOutcome.DefaultTableName, XVerOperandFamily.StructureOutcomes,
            [
                nameof(DbElementOutcome.Key), nameof(DbElementOutcome.SourceFhirPackageKey), nameof(DbElementOutcome.SourceFhirSequence),
                nameof(DbElementOutcome.TargetFhirPackageKey), nameof(DbElementOutcome.TargetFhirSequence), nameof(DbElementOutcome.RequiresXVerDefinition),
                nameof(DbElementOutcome.TotalTargetCount), nameof(DbElementOutcome.TotalSourceCount), nameof(DbElementOutcome.IsRenamed),
                nameof(DbElementOutcome.IsUnmapped), nameof(DbElementOutcome.IsIdentical), nameof(DbElementOutcome.IsEquivalent),
                nameof(DbElementOutcome.IsBroaderThanTarget), nameof(DbElementOutcome.IsNarrowerThanTarget), nameof(DbElementOutcome.FullyMapsToThisTarget),
                nameof(DbElementOutcome.FullyMapsAcrossAllTargets), nameof(DbElementOutcome.Comments), nameof(DbElementOutcome.GenLongId),
                nameof(DbElementOutcome.GenShortId), nameof(DbElementOutcome.GenUrl), nameof(DbElementOutcome.GenName),
                nameof(DbElementOutcome.GenFileName), nameof(DbElementOutcome.GenArtifactShort), nameof(DbElementOutcome.GenArtifactDescription),
                nameof(DbElementOutcome.GenArtifactComment), nameof(DbElementOutcome.GenMappingComment), nameof(DbElementOutcome.SourceCanonicalVersioned),
                nameof(DbElementOutcome.SourceCanonicalUnversioned), nameof(DbElementOutcome.SourceId), nameof(DbElementOutcome.SourceName),
                nameof(DbElementOutcome.SourceVersion), nameof(DbElementOutcome.SourceStructureKey), nameof(DbElementOutcome.SourceElementKey),
                nameof(DbElementOutcome.RequiresExtensionDefinition), nameof(DbElementOutcome.RequiresSliceDefinition), nameof(DbElementOutcome.GenSliceName),
                nameof(DbElementOutcome.RequiresCardinalityDefinition), nameof(DbElementOutcome.RequiresCardinalitySlice), nameof(DbElementOutcome.SourceResourceOrder),
                nameof(DbElementOutcome.SourceComponentOrder), nameof(DbElementOutcome.SourceMinCardinality), nameof(DbElementOutcome.SourceMaxCardinalityString),
                nameof(DbElementOutcome.SourceChildElementCount), nameof(DbElementOutcome.SourceUsedAsContentReference), nameof(DbElementOutcome.SourceAncestorUsedAsContentReferenceId),
                nameof(DbElementOutcome.SourceAncestorContentReferenceOutcomeKey), nameof(DbElementOutcome.AlternateCanonicalTargetsLiteral), nameof(DbElementOutcome.AlternateCanonicalContextLiteral),
                nameof(DbElementOutcome.AlternateReferenceTargetsLiteral), nameof(DbElementOutcome.AlternateReferenceContextLiteral), nameof(DbElementOutcome.AncestorElementOutcomeKey),
                nameof(DbElementOutcome.ParentElementOutcomeKey), nameof(DbElementOutcome.ExtensionSubstitutionKey), nameof(DbElementOutcome.ExtensionSubstitutionUrl),
                nameof(DbElementOutcome.BasicElementBaseId), nameof(DbElementOutcome.BasicElementId), nameof(DbElementOutcome.ExtensionElementBaseId),
                nameof(DbElementOutcome.ExtensionElementId), nameof(DbElementOutcome.ContentReferenceOutcomeKey), nameof(DbElementOutcome.ContentReferenceExtensionUrl),
                nameof(DbElementOutcome.ContentReferenceRequiresXVerDefinition), nameof(DbElementOutcome.ContentReferenceAncestorId), nameof(DbElementOutcome.RequiresDefinitionAsContentReference),
                nameof(DbElementOutcome.ExtensionDefinitionIsProhibited), nameof(DbElementOutcome.ExtensionProhibitionReason), nameof(DbElementOutcome.OutcomeTargetCount),
                nameof(DbElementOutcome.SourceIsModifier), nameof(DbElementOutcome.DefineAsModifier), nameof(DbElementOutcome.ModifierReason),
                nameof(DbElementOutcome.SourceIsDeprecated), nameof(DbElementOutcome.ExtensionContextsLiteral), nameof(DbElementOutcome.CardinalityExtensionContextsLiteral),
                nameof(DbElementOutcome.MappedTypeKeysLiteral), nameof(DbElementOutcome.MappedTypeNamesLiteral), nameof(DbElementOutcome.UnmappedTypeKeysLiteral),
                nameof(DbElementOutcome.UnmappedTypeNamesLiteral), nameof(DbElementOutcome.MappedTypeChildElementKeysLiteral), nameof(DbElementOutcome.MappedChildTypeElementNamesLiteral),
                nameof(DbElementOutcome.UnmappedTypeChildElementKeysLiteral), nameof(DbElementOutcome.UnmappedChildTypeElementNamesLiteral),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementOutcome> rows = DbElementOutcome.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementOutcome.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElementOutcome.SourceFhirSequence), nameof(DbElementOutcome.TargetFhirSequence)],
                    [nameof(DbElementOutcome.RequiresXVerDefinition), nameof(DbElementOutcome.IsRenamed), nameof(DbElementOutcome.IsUnmapped), nameof(DbElementOutcome.IsIdentical), nameof(DbElementOutcome.IsEquivalent), nameof(DbElementOutcome.IsBroaderThanTarget), nameof(DbElementOutcome.IsNarrowerThanTarget), nameof(DbElementOutcome.FullyMapsToThisTarget), nameof(DbElementOutcome.FullyMapsAcrossAllTargets), nameof(DbElementOutcome.RequiresExtensionDefinition), nameof(DbElementOutcome.RequiresSliceDefinition), nameof(DbElementOutcome.RequiresCardinalityDefinition), nameof(DbElementOutcome.RequiresCardinalitySlice), nameof(DbElementOutcome.SourceUsedAsContentReference), nameof(DbElementOutcome.ContentReferenceRequiresXVerDefinition), nameof(DbElementOutcome.RequiresDefinitionAsContentReference), nameof(DbElementOutcome.ExtensionDefinitionIsProhibited), nameof(DbElementOutcome.SourceIsModifier), nameof(DbElementOutcome.DefineAsModifier), nameof(DbElementOutcome.SourceIsDeprecated)]);
                return new TableImage<DbElementOutcome, DbElementOutcomeOperand>(rows.Select(row => DbElementOutcomeOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbElementOutcomeTarget), DbElementOutcomeTarget.DefaultTableName, XVerOperandFamily.StructureOutcomes,
            [
                nameof(DbElementOutcomeTarget.Key), nameof(DbElementOutcomeTarget.ElementOutcomeKey), nameof(DbElementOutcomeTarget.StructureOutcomeKey),
                nameof(DbElementOutcomeTarget.ElementComparisonKey), nameof(DbElementOutcomeTarget.SourceFhirPackageKey), nameof(DbElementOutcomeTarget.SourceFhirSequence),
                nameof(DbElementOutcomeTarget.TargetFhirPackageKey), nameof(DbElementOutcomeTarget.TargetFhirSequence), nameof(DbElementOutcomeTarget.FullyMapsToThisTarget),
                nameof(DbElementOutcomeTarget.TargetStructureKey), nameof(DbElementOutcomeTarget.TargetElementKey), nameof(DbElementOutcomeTarget.TargetElementId),
                nameof(DbElementOutcomeTarget.TargetResourceOrder), nameof(DbElementOutcomeTarget.TargetComponentOrder), nameof(DbElementOutcomeTarget.TargetMinCardinality),
                nameof(DbElementOutcomeTarget.TargetMaxCardinalityString), nameof(DbElementOutcomeTarget.TargetIsModifier), nameof(DbElementOutcomeTarget.ContextElementKey),
                nameof(DbElementOutcomeTarget.ContextElementId), nameof(DbElementOutcomeTarget.ContextRootExtensionUrl), nameof(DbElementOutcomeTarget.ContextParentExtensionUrl),
                nameof(DbElementOutcomeTarget.CardinalityContextElementKey), nameof(DbElementOutcomeTarget.CardinalityContextElementId), nameof(DbElementOutcomeTarget.CardinalityContextRootExtensionUrl),
                nameof(DbElementOutcomeTarget.CardinalityContextParentExtensionUrl), nameof(DbElementOutcomeTarget.Comments),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbElementOutcomeTarget> rows = DbElementOutcomeTarget.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbElementOutcomeTarget.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbElementOutcomeTarget.SourceFhirSequence), nameof(DbElementOutcomeTarget.TargetFhirSequence)],
                    [nameof(DbElementOutcomeTarget.FullyMapsToThisTarget), nameof(DbElementOutcomeTarget.TargetIsModifier)]);
                return new TableImage<DbElementOutcomeTarget, DbElementOutcomeTargetOperand>(rows.Select(row => DbElementOutcomeTargetOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbUnsafeCodeTrigger), DbUnsafeCodeTrigger.DefaultTableName, XVerOperandFamily.StructureOutcomes,
            [
                nameof(DbUnsafeCodeTrigger.Key), nameof(DbUnsafeCodeTrigger.StructureOutcomeKey), nameof(DbUnsafeCodeTrigger.SourceFhirPackageKey),
                nameof(DbUnsafeCodeTrigger.TargetFhirPackageKey), nameof(DbUnsafeCodeTrigger.SourceElementId), nameof(DbUnsafeCodeTrigger.TargetElementId),
                nameof(DbUnsafeCodeTrigger.SourcePathJson), nameof(DbUnsafeCodeTrigger.System), nameof(DbUnsafeCodeTrigger.Code),
                nameof(DbUnsafeCodeTrigger.SourceVersion), nameof(DbUnsafeCodeTrigger.IsWildcardVersion), nameof(DbUnsafeCodeTrigger.VersionsJson),
                nameof(DbUnsafeCodeTrigger.EvidenceKeysJson), nameof(DbUnsafeCodeTrigger.RuleKeysJson), nameof(DbUnsafeCodeTrigger.AnnotationKeysJson),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbUnsafeCodeTrigger> rows = DbUnsafeCodeTrigger.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbUnsafeCodeTrigger.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [],
                    [nameof(DbUnsafeCodeTrigger.IsWildcardVersion)]);
                return new TableImage<DbUnsafeCodeTrigger, DbUnsafeCodeTriggerOperand>(rows.Select(row => DbUnsafeCodeTriggerOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetOutcome), DbValueSetOutcome.DefaultTableName, XVerOperandFamily.VocabularyOutcomes,
            [
                nameof(DbValueSetOutcome.Key), nameof(DbValueSetOutcome.SourceFhirPackageKey), nameof(DbValueSetOutcome.SourceFhirSequence),
                nameof(DbValueSetOutcome.TargetFhirPackageKey), nameof(DbValueSetOutcome.TargetFhirSequence), nameof(DbValueSetOutcome.RequiresXVerDefinition),
                nameof(DbValueSetOutcome.TotalTargetCount), nameof(DbValueSetOutcome.TotalSourceCount), nameof(DbValueSetOutcome.IsRenamed),
                nameof(DbValueSetOutcome.IsUnmapped), nameof(DbValueSetOutcome.IsIdentical), nameof(DbValueSetOutcome.IsEquivalent),
                nameof(DbValueSetOutcome.IsBroaderThanTarget), nameof(DbValueSetOutcome.IsNarrowerThanTarget), nameof(DbValueSetOutcome.FullyMapsToThisTarget),
                nameof(DbValueSetOutcome.FullyMapsAcrossAllTargets), nameof(DbValueSetOutcome.Comments), nameof(DbValueSetOutcome.GenLongId),
                nameof(DbValueSetOutcome.GenShortId), nameof(DbValueSetOutcome.GenUrl), nameof(DbValueSetOutcome.GenName),
                nameof(DbValueSetOutcome.GenFileName), nameof(DbValueSetOutcome.GenArtifactShort), nameof(DbValueSetOutcome.GenArtifactDescription),
                nameof(DbValueSetOutcome.GenArtifactComment), nameof(DbValueSetOutcome.GenMappingComment), nameof(DbValueSetOutcome.SourceCanonicalVersioned),
                nameof(DbValueSetOutcome.SourceCanonicalUnversioned), nameof(DbValueSetOutcome.SourceId), nameof(DbValueSetOutcome.SourceName),
                nameof(DbValueSetOutcome.SourceVersion), nameof(DbValueSetOutcome.TargetCanonicalVersioned), nameof(DbValueSetOutcome.TargetCanonicalUnversioned),
                nameof(DbValueSetOutcome.TargetId), nameof(DbValueSetOutcome.TargetName), nameof(DbValueSetOutcome.TargetVersion),
                nameof(DbValueSetOutcome.ValueSetComparisonKey), nameof(DbValueSetOutcome.SourceValueSetKey), nameof(DbValueSetOutcome.TargetValueSetKey),
                nameof(DbValueSetOutcome.ConceptMapLongId), nameof(DbValueSetOutcome.ConceptMapShortId), nameof(DbValueSetOutcome.ConceptMapUrl),
                nameof(DbValueSetOutcome.ConceptMapName), nameof(DbValueSetOutcome.ConceptMapFileName),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetOutcome> rows = DbValueSetOutcome.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetOutcome.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSetOutcome.SourceFhirSequence), nameof(DbValueSetOutcome.TargetFhirSequence)],
                    [nameof(DbValueSetOutcome.RequiresXVerDefinition), nameof(DbValueSetOutcome.IsRenamed), nameof(DbValueSetOutcome.IsUnmapped), nameof(DbValueSetOutcome.IsIdentical), nameof(DbValueSetOutcome.IsEquivalent), nameof(DbValueSetOutcome.IsBroaderThanTarget), nameof(DbValueSetOutcome.IsNarrowerThanTarget), nameof(DbValueSetOutcome.FullyMapsToThisTarget), nameof(DbValueSetOutcome.FullyMapsAcrossAllTargets)]);
                return new TableImage<DbValueSetOutcome, DbValueSetOutcomeOperand>(rows.Select(row => DbValueSetOutcomeOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
        new(typeof(DbValueSetConceptOutcome), DbValueSetConceptOutcome.DefaultTableName, XVerOperandFamily.VocabularyOutcomes,
            [
                nameof(DbValueSetConceptOutcome.Key), nameof(DbValueSetConceptOutcome.SourceFhirPackageKey), nameof(DbValueSetConceptOutcome.SourceFhirSequence),
                nameof(DbValueSetConceptOutcome.TargetFhirPackageKey), nameof(DbValueSetConceptOutcome.TargetFhirSequence), nameof(DbValueSetConceptOutcome.RequiresXVerDefinition),
                nameof(DbValueSetConceptOutcome.TotalTargetCount), nameof(DbValueSetConceptOutcome.TotalSourceCount), nameof(DbValueSetConceptOutcome.IsRenamed),
                nameof(DbValueSetConceptOutcome.IsUnmapped), nameof(DbValueSetConceptOutcome.IsIdentical), nameof(DbValueSetConceptOutcome.IsEquivalent),
                nameof(DbValueSetConceptOutcome.IsBroaderThanTarget), nameof(DbValueSetConceptOutcome.IsNarrowerThanTarget), nameof(DbValueSetConceptOutcome.FullyMapsToThisTarget),
                nameof(DbValueSetConceptOutcome.FullyMapsAcrossAllTargets), nameof(DbValueSetConceptOutcome.Comments), nameof(DbValueSetConceptOutcome.ValueSetOutcomeKey),
                nameof(DbValueSetConceptOutcome.ValueSetConceptComparisonKey), nameof(DbValueSetConceptOutcome.SourceValueSetKey), nameof(DbValueSetConceptOutcome.SourceValueSetConceptKey),
                nameof(DbValueSetConceptOutcome.TargetValueSetKey), nameof(DbValueSetConceptOutcome.TargetValueSetConceptKey), nameof(DbValueSetConceptOutcome.SourceSystem),
                nameof(DbValueSetConceptOutcome.SourceSystemVersion), nameof(DbValueSetConceptOutcome.SourceCode), nameof(DbValueSetConceptOutcome.SourceDisplay),
                nameof(DbValueSetConceptOutcome.TargetSystem), nameof(DbValueSetConceptOutcome.TargetSystemVersion), nameof(DbValueSetConceptOutcome.TargetCode),
                nameof(DbValueSetConceptOutcome.TargetDisplay),
            ])
        {
            Reader = (db, table) =>
            {
                List<DbValueSetConceptOutcome> rows = DbValueSetConceptOutcome.SelectList(db, dbTableName: table, orderByProperties: [nameof(DbValueSetConceptOutcome.Key)]);
                Dictionary<int, StoredColumns> stored = ReadStoredColumns(db, table,
                    [nameof(DbValueSetConceptOutcome.SourceFhirSequence), nameof(DbValueSetConceptOutcome.TargetFhirSequence)],
                    [nameof(DbValueSetConceptOutcome.RequiresXVerDefinition), nameof(DbValueSetConceptOutcome.IsRenamed), nameof(DbValueSetConceptOutcome.IsUnmapped), nameof(DbValueSetConceptOutcome.IsIdentical), nameof(DbValueSetConceptOutcome.IsEquivalent), nameof(DbValueSetConceptOutcome.IsBroaderThanTarget), nameof(DbValueSetConceptOutcome.IsNarrowerThanTarget), nameof(DbValueSetConceptOutcome.FullyMapsToThisTarget), nameof(DbValueSetConceptOutcome.FullyMapsAcrossAllTargets)]);
                return new TableImage<DbValueSetConceptOutcome, DbValueSetConceptOutcomeOperand>(rows.Select(row => DbValueSetConceptOutcomeOperand.Copy(row, stored[row.Key])), row => row.ToRow());
            },
        },
    ];

    private sealed record DbDefinitionInventoryStateOperand(
        int Key,
        int FormatVersion,
        int PolicyVersion,
        string StartedRevision,
        string? CompletedRevision,
        string ExpectedPackageCountsJson,
        string PendingMapInventoryBindingsJson,
        string CompletedMapInventoryBindingsJson)
    {
        internal static DbDefinitionInventoryStateOperand Copy(DbDefinitionInventoryState row, StoredColumns? stored = null) => new(
            row.Key,
            row.FormatVersion,
            row.PolicyVersion,
            row.StartedRevision,
            row.CompletedRevision,
            row.ExpectedPackageCountsJson,
            row.PendingMapInventoryBindingsJson,
            row.CompletedMapInventoryBindingsJson);

        internal DbDefinitionInventoryState ToRow() => new()
        {
            Key = Key,
            FormatVersion = FormatVersion,
            PolicyVersion = PolicyVersion,
            StartedRevision = StartedRevision,
            CompletedRevision = CompletedRevision,
            ExpectedPackageCountsJson = ExpectedPackageCountsJson,
            PendingMapInventoryBindingsJson = PendingMapInventoryBindingsJson,
            CompletedMapInventoryBindingsJson = CompletedMapInventoryBindingsJson,
        };
    }

    private sealed record DbFhirPackageOperand(
        int Key,
        string Name,
        string PackageId,
        string PackageVersion,
        string FhirVersionShort,
        string CanonicalUrl,
        string ShortName,
        string? Dependencies,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes DefinitionFhirSequence,
        string? DefinitionFhirSequenceStoredText)
    {
        internal static DbFhirPackageOperand Copy(DbFhirPackage row, StoredColumns? stored = null) => new(
            row.Key,
            row.Name,
            row.PackageId,
            row.PackageVersion,
            row.FhirVersionShort,
            row.CanonicalUrl,
            row.ShortName,
            row.Dependencies,
            row.DefinitionFhirSequence,
            stored!.Text[0]);

        internal DbFhirPackage ToRow() => new()
        {
            Key = Key,
            Name = Name,
            PackageId = PackageId,
            PackageVersion = PackageVersion,
            FhirVersionShort = FhirVersionShort,
            CanonicalUrl = CanonicalUrl,
            ShortName = ShortName,
            Dependencies = Dependencies,
            DefinitionFhirSequence = DefinitionFhirSequence,
        };
    }

    private sealed record DbStructureDefinitionOperand(
        int Key,
        int FhirPackageKey,
        string Id,
        string VersionedUrl,
        string UnversionedUrl,
        string Name,
        string Version,
        string? VersionAlgorithmString,
        string? VersionAlgorithmCoding,
        Hl7.Fhir.Model.PublicationStatus? Status,
        string? Title,
        string? Description,
        string? Purpose,
        string? Narrative,
        string? StandardStatus,
        string? WorkGroup,
        int? FhirMaturity,
        bool? IsExperimental,
        DateTimeOffset? LastChangedDate,
        string? Publisher,
        string? Copyright,
        string? CopyrightLabel,
        string? ApprovalDate,
        string? LastReviewDate,
        DateTimeOffset? EffectivePeriodStart,
        DateTimeOffset? EffectivePeriodEnd,
        string? Topic,
        string? RelatedArtifacts,
        string? Jurisdictions,
        string? UseContexts,
        string? Contacts,
        string? Authors,
        string? Editors,
        string? Reviewers,
        string? Endorsers,
        string? RootExtensions,
        string? SourcePackageMoniker,
        string? Comment,
        string? Message,
        Fhir.CodeGen.Common.Models.FhirArtifactClassEnum ArtifactClass,
        int SnapshotCount,
        int DifferentialCount,
        string? Implements,
        string? StatusStoredText,
        string? LastChangedDateStoredText,
        string? EffectivePeriodStartStoredText,
        string? EffectivePeriodEndStoredText,
        string? ArtifactClassStoredText,
        long? IsExperimentalStoredInteger)
    {
        internal static DbStructureDefinitionOperand Copy(DbStructureDefinition row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.Id,
            row.VersionedUrl,
            row.UnversionedUrl,
            row.Name,
            row.Version,
            row.VersionAlgorithmString,
            stored!.Text[0],
            row.Status,
            row.Title,
            row.Description,
            row.Purpose,
            stored!.Text[2],
            row.StandardStatus,
            row.WorkGroup,
            row.FhirMaturity,
            row.IsExperimental,
            row.LastChangedDate,
            row.Publisher,
            row.Copyright,
            row.CopyrightLabel,
            row.ApprovalDate,
            row.LastReviewDate,
            row.EffectivePeriodStart,
            row.EffectivePeriodEnd,
            stored!.Text[6],
            stored!.Text[7],
            stored!.Text[8],
            stored!.Text[9],
            stored!.Text[10],
            stored!.Text[11],
            stored!.Text[12],
            stored!.Text[13],
            stored!.Text[14],
            stored!.Text[15],
            row.SourcePackageMoniker,
            row.Comment,
            row.Message,
            row.ArtifactClass,
            row.SnapshotCount,
            row.DifferentialCount,
            row.Implements,
            stored!.Text[1],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Text[5],
            stored!.Text[16],
            stored!.Integers[0]);

        internal DbStructureDefinition ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            Id = Id,
            VersionedUrl = VersionedUrl,
            UnversionedUrl = UnversionedUrl,
            Name = Name,
            Version = Version,
            VersionAlgorithmString = VersionAlgorithmString,
            VersionAlgorithmCoding = VersionAlgorithmCoding is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.Coding>(VersionAlgorithmCoding),
            Status = Status,
            Title = Title,
            Description = Description,
            Purpose = Purpose,
            Narrative = Narrative is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.Narrative>(Narrative),
            StandardStatus = StandardStatus,
            WorkGroup = WorkGroup,
            FhirMaturity = FhirMaturity,
            IsExperimental = IsExperimental,
            LastChangedDate = LastChangedDate,
            Publisher = Publisher,
            Copyright = Copyright,
            CopyrightLabel = CopyrightLabel,
            ApprovalDate = ApprovalDate,
            LastReviewDate = LastReviewDate,
            EffectivePeriodStart = EffectivePeriodStart,
            EffectivePeriodEnd = EffectivePeriodEnd,
            Topic = Topic is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeableConcept>(Topic),
            RelatedArtifacts = RelatedArtifacts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.RelatedArtifact>(RelatedArtifacts),
            Jurisdictions = Jurisdictions is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeableConcept>(Jurisdictions),
            UseContexts = UseContexts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.UsageContext>(UseContexts),
            Contacts = Contacts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Contacts),
            Authors = Authors is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Authors),
            Editors = Editors is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Editors),
            Reviewers = Reviewers is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Reviewers),
            Endorsers = Endorsers is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Endorsers),
            RootExtensions = RootExtensions is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.Extension>(RootExtensions),
            SourcePackageMoniker = SourcePackageMoniker,
            Comment = Comment,
            Message = Message,
            ArtifactClass = ArtifactClass,
            SnapshotCount = SnapshotCount,
            DifferentialCount = DifferentialCount,
            Implements = Implements,
        };
    }

    private sealed record DbStructureDefinitionFactsOperand(
        int Key,
        int FhirPackageKey,
        int StructureKey,
        string? Type,
        string? Kind,
        string? Derivation,
        string? BaseDefinition,
        DefinitionSnapshotAvailability SnapshotAvailability,
        string StructureDefinitionJson,
        string? SnapshotAvailabilityStoredText)
    {
        internal static DbStructureDefinitionFactsOperand Copy(DbStructureDefinitionFacts row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.StructureKey,
            row.Type,
            row.Kind,
            row.Derivation,
            row.BaseDefinition,
            row.SnapshotAvailability,
            row.StructureDefinitionJson,
            stored!.Text[0]);

        internal DbStructureDefinitionFacts ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            StructureKey = StructureKey,
            Type = Type,
            Kind = Kind,
            Derivation = Derivation,
            BaseDefinition = BaseDefinition,
            SnapshotAvailability = SnapshotAvailability,
            StructureDefinitionJson = StructureDefinitionJson,
        };
    }

    private sealed record DbElementOperand(
        int Key,
        int FhirPackageKey,
        int StructureKey,
        int? ParentElementKey,
        int ResourceFieldOrder,
        int ComponentFieldOrder,
        string Id,
        string Path,
        int ChildElementCount,
        string Name,
        string? Short,
        string? Definition,
        string? Comments,
        string? Requirements,
        int MinCardinality,
        int MaxCardinality,
        string MaxCardinalityString,
        string? SliceName,
        string FullCollatedTypeLiteral,
        int DistinctTypeCount,
        string DistinctTypeLiterals,
        Hl7.Fhir.Model.BindingStrength? ValueSetBindingStrength,
        string? BindingValueSet,
        int? BindingValueSetKey,
        int AdditionalBindingCount,
        string? BindingDescription,
        bool IsInherited,
        string? BasePath,
        int? BaseElementKey,
        int? BaseStructureKey,
        bool DefinedAsContentReference,
        int? ContentReferenceSourceKey,
        string? ContentReferenceSourceId,
        bool? UsedAsContentReference,
        bool IsSimpleType,
        bool IsChoiceType,
        bool IsModifier,
        string? IsModifierReason,
        bool IsDeprecated,
        string? ValueSetBindingStrengthStoredText,
        long? IsInheritedStoredInteger,
        long? DefinedAsContentReferenceStoredInteger,
        long? UsedAsContentReferenceStoredInteger,
        long? IsSimpleTypeStoredInteger,
        long? IsChoiceTypeStoredInteger,
        long? IsModifierStoredInteger,
        long? IsDeprecatedStoredInteger)
    {
        internal static DbElementOperand Copy(DbElement row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.StructureKey,
            row.ParentElementKey,
            row.ResourceFieldOrder,
            row.ComponentFieldOrder,
            row.Id,
            row.Path,
            row.ChildElementCount,
            row.Name,
            row.Short,
            row.Definition,
            row.Comments,
            row.Requirements,
            row.MinCardinality,
            row.MaxCardinality,
            row.MaxCardinalityString,
            row.SliceName,
            row.FullCollatedTypeLiteral,
            row.DistinctTypeCount,
            row.DistinctTypeLiterals,
            row.ValueSetBindingStrength,
            row.BindingValueSet,
            row.BindingValueSetKey,
            row.AdditionalBindingCount,
            row.BindingDescription,
            row.IsInherited,
            row.BasePath,
            row.BaseElementKey,
            row.BaseStructureKey,
            row.DefinedAsContentReference,
            row.ContentReferenceSourceKey,
            row.ContentReferenceSourceId,
            row.UsedAsContentReference,
            row.IsSimpleType,
            row.IsChoiceType,
            row.IsModifier,
            row.IsModifierReason,
            row.IsDeprecated,
            stored!.Text[0],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4],
            stored!.Integers[5],
            stored!.Integers[6]);

        internal DbElement ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            StructureKey = StructureKey,
            ParentElementKey = ParentElementKey,
            ResourceFieldOrder = ResourceFieldOrder,
            ComponentFieldOrder = ComponentFieldOrder,
            Id = Id,
            Path = Path,
            ChildElementCount = ChildElementCount,
            Name = Name,
            Short = Short,
            Definition = Definition,
            Comments = Comments,
            Requirements = Requirements,
            MinCardinality = MinCardinality,
            MaxCardinality = MaxCardinality,
            MaxCardinalityString = MaxCardinalityString,
            SliceName = SliceName,
            FullCollatedTypeLiteral = FullCollatedTypeLiteral,
            DistinctTypeCount = DistinctTypeCount,
            DistinctTypeLiterals = DistinctTypeLiterals,
            ValueSetBindingStrength = ValueSetBindingStrength,
            BindingValueSet = BindingValueSet,
            BindingValueSetKey = BindingValueSetKey,
            AdditionalBindingCount = AdditionalBindingCount,
            BindingDescription = BindingDescription,
            IsInherited = IsInherited,
            BasePath = BasePath,
            BaseElementKey = BaseElementKey,
            BaseStructureKey = BaseStructureKey,
            DefinedAsContentReference = DefinedAsContentReference,
            ContentReferenceSourceKey = ContentReferenceSourceKey,
            ContentReferenceSourceId = ContentReferenceSourceId,
            UsedAsContentReference = UsedAsContentReference,
            IsSimpleType = IsSimpleType,
            IsChoiceType = IsChoiceType,
            IsModifier = IsModifier,
            IsModifierReason = IsModifierReason,
            IsDeprecated = IsDeprecated,
        };
    }

    private sealed record DbElementTypeOperand(
        int Key,
        int FhirPackageKey,
        int StructureKey,
        int ElementKey,
        string? TypeName,
        int? TypeStructureKey,
        string? TypeProfile,
        int? TypeProfileStructureKey,
        string? TargetProfile,
        int? TargetProfileStructureKey)
    {
        internal static DbElementTypeOperand Copy(DbElementType row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.StructureKey,
            row.ElementKey,
            row.TypeName,
            row.TypeStructureKey,
            row.TypeProfile,
            row.TypeProfileStructureKey,
            row.TargetProfile,
            row.TargetProfileStructureKey);

        internal DbElementType ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            StructureKey = StructureKey,
            ElementKey = ElementKey,
            TypeName = TypeName,
            TypeStructureKey = TypeStructureKey,
            TypeProfile = TypeProfile,
            TypeProfileStructureKey = TypeProfileStructureKey,
            TargetProfile = TargetProfile,
            TargetProfileStructureKey = TargetProfileStructureKey,
        };
    }

    private sealed record DbElementAdditionalBindingOperand(
        int Key,
        int FhirPackageKey,
        int StructureKey,
        int ElementKey,
        string? FhirKey,
        Hl7.Fhir.Model.ElementDefinition.AdditionalBindingPurposeVS? Purpose,
        string? BindingValueSet,
        int? BindingValueSetKey,
        string? Documentation,
        string? ShortDocumentation,
        string? CollatedUsageContexts,
        bool? SatisfiedBySingleRepetition,
        string? PurposeStoredText,
        long? SatisfiedBySingleRepetitionStoredInteger)
    {
        internal static DbElementAdditionalBindingOperand Copy(DbElementAdditionalBinding row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.StructureKey,
            row.ElementKey,
            row.FhirKey,
            row.Purpose,
            row.BindingValueSet,
            row.BindingValueSetKey,
            row.Documentation,
            row.ShortDocumentation,
            row.CollatedUsageContexts,
            row.SatisfiedBySingleRepetition,
            stored!.Text[0],
            stored!.Integers[0]);

        internal DbElementAdditionalBinding ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            StructureKey = StructureKey,
            ElementKey = ElementKey,
            FhirKey = FhirKey,
            Purpose = Purpose,
            BindingValueSet = BindingValueSet,
            BindingValueSetKey = BindingValueSetKey,
            Documentation = Documentation,
            ShortDocumentation = ShortDocumentation,
            CollatedUsageContexts = CollatedUsageContexts,
            SatisfiedBySingleRepetition = SatisfiedBySingleRepetition,
        };
    }

    private sealed record DbUnsafeCodeInputStateOperand(
        int Key,
        string SnapshotId,
        int FormatVersion,
        string SourceMode,
        string? SourceRoot,
        string? CompletedInputRevision)
    {
        internal static DbUnsafeCodeInputStateOperand Copy(DbUnsafeCodeInputState row, StoredColumns? stored = null) => new(
            row.Key,
            row.SnapshotId,
            row.FormatVersion,
            row.SourceMode,
            row.SourceRoot,
            row.CompletedInputRevision);

        internal DbUnsafeCodeInputState ToRow() => new()
        {
            Key = Key,
            SnapshotId = SnapshotId,
            FormatVersion = FormatVersion,
            SourceMode = SourceMode,
            SourceRoot = SourceRoot,
            CompletedInputRevision = CompletedInputRevision,
        };
    }

    private sealed record DbMappingSourceFileOperand(
        int Key,
        string Filename,
        string SourceRelativeFilename,
        bool IsConceptMap,
        bool IsFml,
        string Url,
        long? IsConceptMapStoredInteger,
        long? IsFmlStoredInteger)
    {
        internal static DbMappingSourceFileOperand Copy(DbMappingSourceFile row, StoredColumns? stored = null) => new(
            row.Key,
            row.Filename,
            row.SourceRelativeFilename,
            row.IsConceptMap,
            row.IsFml,
            row.Url,
            stored!.Integers[0],
            stored!.Integers[1]);

        internal DbMappingSourceFile ToRow() => new()
        {
            Key = Key,
            Filename = Filename,
            SourceRelativeFilename = SourceRelativeFilename,
            IsConceptMap = IsConceptMap,
            IsFml = IsFml,
            Url = Url,
        };
    }

    private sealed record DbStructureMappingOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int? ConceptMapSourceKey,
        string? ConceptMapFilename,
        int? FmlSourceKey,
        string? FmlFilename,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool ExplicitNoMap,
        string? Comments,
        string? TechnicalNotes,
        int SourceStructureKey,
        string SourceStructureId,
        string SourceStructureUrl,
        int? TargetStructureKey,
        string? TargetStructureId,
        string? TargetStructureUrl,
        bool IsFallback,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        string? ConceptDomainRelationshipStoredText,
        string? ValueDomainRelationshipStoredText,
        long? ExplicitNoMapStoredInteger,
        long? IsFallbackStoredInteger)
    {
        internal static DbStructureMappingOperand Copy(DbStructureMapping row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.ConceptMapSourceKey,
            row.ConceptMapFilename,
            row.FmlSourceKey,
            row.FmlFilename,
            row.Relationship,
            row.ExplicitNoMap,
            row.Comments,
            row.TechnicalNotes,
            row.SourceStructureKey,
            row.SourceStructureId,
            row.SourceStructureUrl,
            row.TargetStructureKey,
            row.TargetStructureId,
            row.TargetStructureUrl,
            row.IsFallback,
            row.ConceptDomainRelationship,
            row.ValueDomainRelationship,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Integers[0],
            stored!.Integers[1]);

        internal DbStructureMapping ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            ConceptMapSourceKey = ConceptMapSourceKey,
            ConceptMapFilename = ConceptMapFilename,
            FmlSourceKey = FmlSourceKey,
            FmlFilename = FmlFilename,
            Relationship = Relationship,
            ExplicitNoMap = ExplicitNoMap,
            Comments = Comments,
            TechnicalNotes = TechnicalNotes,
            SourceStructureKey = SourceStructureKey,
            SourceStructureId = SourceStructureId,
            SourceStructureUrl = SourceStructureUrl,
            TargetStructureKey = TargetStructureKey,
            TargetStructureId = TargetStructureId,
            TargetStructureUrl = TargetStructureUrl,
            IsFallback = IsFallback,
            ConceptDomainRelationship = ConceptDomainRelationship,
            ValueDomainRelationship = ValueDomainRelationship,
        };
    }

    private sealed record DbElementMappingOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int? ConceptMapSourceKey,
        string? ConceptMapFilename,
        int? FmlSourceKey,
        string? FmlFilename,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool ExplicitNoMap,
        string? Comments,
        string? TechnicalNotes,
        int? StructureMappingKey,
        int SourceStructureKey,
        int? SourceElementKey,
        string SourceElementId,
        int? TargetStructureKey,
        int? TargetElementKey,
        string? TargetElementId,
        bool? FmlIsSimpleCopy,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        string? ConceptDomainRelationshipStoredText,
        string? ValueDomainRelationshipStoredText,
        long? ExplicitNoMapStoredInteger,
        long? FmlIsSimpleCopyStoredInteger)
    {
        internal static DbElementMappingOperand Copy(DbElementMapping row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.ConceptMapSourceKey,
            row.ConceptMapFilename,
            row.FmlSourceKey,
            row.FmlFilename,
            row.Relationship,
            row.ExplicitNoMap,
            row.Comments,
            row.TechnicalNotes,
            row.StructureMappingKey,
            row.SourceStructureKey,
            row.SourceElementKey,
            row.SourceElementId,
            row.TargetStructureKey,
            row.TargetElementKey,
            row.TargetElementId,
            row.FmlIsSimpleCopy,
            row.ConceptDomainRelationship,
            row.ValueDomainRelationship,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Integers[0],
            stored!.Integers[1]);

        internal DbElementMapping ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            ConceptMapSourceKey = ConceptMapSourceKey,
            ConceptMapFilename = ConceptMapFilename,
            FmlSourceKey = FmlSourceKey,
            FmlFilename = FmlFilename,
            Relationship = Relationship,
            ExplicitNoMap = ExplicitNoMap,
            Comments = Comments,
            TechnicalNotes = TechnicalNotes,
            StructureMappingKey = StructureMappingKey,
            SourceStructureKey = SourceStructureKey,
            SourceElementKey = SourceElementKey,
            SourceElementId = SourceElementId,
            TargetStructureKey = TargetStructureKey,
            TargetElementKey = TargetElementKey,
            TargetElementId = TargetElementId,
            FmlIsSimpleCopy = FmlIsSimpleCopy,
            ConceptDomainRelationship = ConceptDomainRelationship,
            ValueDomainRelationship = ValueDomainRelationship,
        };
    }

    private sealed record DbValueSetMappingOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int? ConceptMapSourceKey,
        string? ConceptMapFilename,
        int? FmlSourceKey,
        string? FmlFilename,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool ExplicitNoMap,
        string? Comments,
        string? TechnicalNotes,
        int SourceValueSetKey,
        string SourceValueSetId,
        string SourceValueSetUrl,
        string SourceValueSetVersion,
        int? TargetValueSetKey,
        string? TargetValueSetId,
        string? TargetValueSetUrl,
        string? TargetValueSetVersion,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        long? ExplicitNoMapStoredInteger)
    {
        internal static DbValueSetMappingOperand Copy(DbValueSetMapping row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.ConceptMapSourceKey,
            row.ConceptMapFilename,
            row.FmlSourceKey,
            row.FmlFilename,
            row.Relationship,
            row.ExplicitNoMap,
            row.Comments,
            row.TechnicalNotes,
            row.SourceValueSetKey,
            row.SourceValueSetId,
            row.SourceValueSetUrl,
            row.SourceValueSetVersion,
            row.TargetValueSetKey,
            row.TargetValueSetId,
            row.TargetValueSetUrl,
            row.TargetValueSetVersion,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Integers[0]);

        internal DbValueSetMapping ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            ConceptMapSourceKey = ConceptMapSourceKey,
            ConceptMapFilename = ConceptMapFilename,
            FmlSourceKey = FmlSourceKey,
            FmlFilename = FmlFilename,
            Relationship = Relationship,
            ExplicitNoMap = ExplicitNoMap,
            Comments = Comments,
            TechnicalNotes = TechnicalNotes,
            SourceValueSetKey = SourceValueSetKey,
            SourceValueSetId = SourceValueSetId,
            SourceValueSetUrl = SourceValueSetUrl,
            SourceValueSetVersion = SourceValueSetVersion,
            TargetValueSetKey = TargetValueSetKey,
            TargetValueSetId = TargetValueSetId,
            TargetValueSetUrl = TargetValueSetUrl,
            TargetValueSetVersion = TargetValueSetVersion,
        };
    }

    private sealed record DbValueSetConceptMappingOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int? ConceptMapSourceKey,
        string? ConceptMapFilename,
        int? FmlSourceKey,
        string? FmlFilename,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool ExplicitNoMap,
        string? Comments,
        string? TechnicalNotes,
        int ValueSetMappingKey,
        int SourceValueSetKey,
        int SourceValueSetConceptKey,
        string SourceSystem,
        string SourceCode,
        int? TargetValueSetKey,
        int? TargetValueSetConceptKey,
        string? TargetSystem,
        string? TargetCode,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        long? ExplicitNoMapStoredInteger)
    {
        internal static DbValueSetConceptMappingOperand Copy(DbValueSetConceptMapping row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.ConceptMapSourceKey,
            row.ConceptMapFilename,
            row.FmlSourceKey,
            row.FmlFilename,
            row.Relationship,
            row.ExplicitNoMap,
            row.Comments,
            row.TechnicalNotes,
            row.ValueSetMappingKey,
            row.SourceValueSetKey,
            row.SourceValueSetConceptKey,
            row.SourceSystem,
            row.SourceCode,
            row.TargetValueSetKey,
            row.TargetValueSetConceptKey,
            row.TargetSystem,
            row.TargetCode,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Integers[0]);

        internal DbValueSetConceptMapping ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            ConceptMapSourceKey = ConceptMapSourceKey,
            ConceptMapFilename = ConceptMapFilename,
            FmlSourceKey = FmlSourceKey,
            FmlFilename = FmlFilename,
            Relationship = Relationship,
            ExplicitNoMap = ExplicitNoMap,
            Comments = Comments,
            TechnicalNotes = TechnicalNotes,
            ValueSetMappingKey = ValueSetMappingKey,
            SourceValueSetKey = SourceValueSetKey,
            SourceValueSetConceptKey = SourceValueSetConceptKey,
            SourceSystem = SourceSystem,
            SourceCode = SourceCode,
            TargetValueSetKey = TargetValueSetKey,
            TargetValueSetConceptKey = TargetValueSetConceptKey,
            TargetSystem = TargetSystem,
            TargetCode = TargetCode,
        };
    }

    private sealed record DbProfileMapOwnerFactsOperand(
        int Key,
        int AnnotationKey,
        string InputSnapshotId,
        int SourceFhirPackageKey,
        int TargetFhirPackageKey,
        string? GroupSourceCanonical,
        string? GroupSourceVersion,
        string? GroupTargetCanonical,
        string? GroupTargetVersion,
        string? SourceOwnerContext,
        string? TargetOwnerContext,
        int? SourceStructureKey,
        int? TargetStructureKey,
        int? ElementMappingKey,
        ProfileMapOwnerResolution ResolutionState,
        string SourceElementId,
        string TargetElementId,
        int? MappingSourceFileKey,
        string SourceRelativeFilename,
        string? ConceptMapId,
        string? ConceptMapUrl,
        int GroupIndex,
        int SourceIndex,
        int TargetIndex,
        string? ResolutionStateStoredText)
    {
        internal static DbProfileMapOwnerFactsOperand Copy(DbProfileMapOwnerFacts row, StoredColumns? stored = null) => new(
            row.Key,
            row.AnnotationKey,
            row.InputSnapshotId,
            row.SourceFhirPackageKey,
            row.TargetFhirPackageKey,
            row.GroupSourceCanonical,
            row.GroupSourceVersion,
            row.GroupTargetCanonical,
            row.GroupTargetVersion,
            row.SourceOwnerContext,
            row.TargetOwnerContext,
            row.SourceStructureKey,
            row.TargetStructureKey,
            row.ElementMappingKey,
            row.ResolutionState,
            row.SourceElementId,
            row.TargetElementId,
            row.MappingSourceFileKey,
            row.SourceRelativeFilename,
            row.ConceptMapId,
            row.ConceptMapUrl,
            row.GroupIndex,
            row.SourceIndex,
            row.TargetIndex,
            stored!.Text[0]);

        internal DbProfileMapOwnerFacts ToRow() => new()
        {
            Key = Key,
            AnnotationKey = AnnotationKey,
            InputSnapshotId = InputSnapshotId,
            SourceFhirPackageKey = SourceFhirPackageKey,
            TargetFhirPackageKey = TargetFhirPackageKey,
            GroupSourceCanonical = GroupSourceCanonical,
            GroupSourceVersion = GroupSourceVersion,
            GroupTargetCanonical = GroupTargetCanonical,
            GroupTargetVersion = GroupTargetVersion,
            SourceOwnerContext = SourceOwnerContext,
            TargetOwnerContext = TargetOwnerContext,
            SourceStructureKey = SourceStructureKey,
            TargetStructureKey = TargetStructureKey,
            ElementMappingKey = ElementMappingKey,
            ResolutionState = ResolutionState,
            SourceElementId = SourceElementId,
            TargetElementId = TargetElementId,
            MappingSourceFileKey = MappingSourceFileKey,
            SourceRelativeFilename = SourceRelativeFilename,
            ConceptMapId = ConceptMapId,
            ConceptMapUrl = ConceptMapUrl,
            GroupIndex = GroupIndex,
            SourceIndex = SourceIndex,
            TargetIndex = TargetIndex,
        };
    }

    private sealed record DbUnsafeCodeElementAnnotationOperand(
        int Key,
        int SourceFhirPackageKey,
        int TargetFhirPackageKey,
        string SourceElementId,
        string TargetElementId,
        bool RequiresOmittedMandatoryCode,
        string SourceRelativeFilename,
        string? ConceptMapId,
        string? ConceptMapUrl,
        int GroupIndex,
        int SourceIndex,
        int TargetIndex,
        long? RequiresOmittedMandatoryCodeStoredInteger)
    {
        internal static DbUnsafeCodeElementAnnotationOperand Copy(DbUnsafeCodeElementAnnotation row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.TargetFhirPackageKey,
            row.SourceElementId,
            row.TargetElementId,
            row.RequiresOmittedMandatoryCode,
            row.SourceRelativeFilename,
            row.ConceptMapId,
            row.ConceptMapUrl,
            row.GroupIndex,
            row.SourceIndex,
            row.TargetIndex,
            stored!.Integers[0]);

        internal DbUnsafeCodeElementAnnotation ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            TargetFhirPackageKey = TargetFhirPackageKey,
            SourceElementId = SourceElementId,
            TargetElementId = TargetElementId,
            RequiresOmittedMandatoryCode = RequiresOmittedMandatoryCode,
            SourceRelativeFilename = SourceRelativeFilename,
            ConceptMapId = ConceptMapId,
            ConceptMapUrl = ConceptMapUrl,
            GroupIndex = GroupIndex,
            SourceIndex = SourceIndex,
            TargetIndex = TargetIndex,
        };
    }

    private sealed record DbUnsafeCodeMapEvidenceOperand(
        int Key,
        int SourceFhirPackageKey,
        int TargetFhirPackageKey,
        string? SourceValueSetUrl,
        string? SourceValueSetVersion,
        string? TargetValueSetUrl,
        string? TargetValueSetVersion,
        string? SourceSystem,
        string? SourceSystemVersion,
        string? SourceCode,
        string? TargetSystem,
        string? TargetSystemVersion,
        string? TargetCode,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool ExplicitNoMap,
        string SourceRelativeFilename,
        string? ConceptMapId,
        string? ConceptMapUrl,
        int GroupIndex,
        int SourceIndex,
        int? TargetIndex,
        string? RelationshipStoredText,
        long? ExplicitNoMapStoredInteger)
    {
        internal static DbUnsafeCodeMapEvidenceOperand Copy(DbUnsafeCodeMapEvidence row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.TargetFhirPackageKey,
            row.SourceValueSetUrl,
            row.SourceValueSetVersion,
            row.TargetValueSetUrl,
            row.TargetValueSetVersion,
            row.SourceSystem,
            row.SourceSystemVersion,
            row.SourceCode,
            row.TargetSystem,
            row.TargetSystemVersion,
            row.TargetCode,
            row.Relationship,
            row.ExplicitNoMap,
            row.SourceRelativeFilename,
            row.ConceptMapId,
            row.ConceptMapUrl,
            row.GroupIndex,
            row.SourceIndex,
            row.TargetIndex,
            stored!.Text[0],
            stored!.Integers[0]);

        internal DbUnsafeCodeMapEvidence ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            TargetFhirPackageKey = TargetFhirPackageKey,
            SourceValueSetUrl = SourceValueSetUrl,
            SourceValueSetVersion = SourceValueSetVersion,
            TargetValueSetUrl = TargetValueSetUrl,
            TargetValueSetVersion = TargetValueSetVersion,
            SourceSystem = SourceSystem,
            SourceSystemVersion = SourceSystemVersion,
            SourceCode = SourceCode,
            TargetSystem = TargetSystem,
            TargetSystemVersion = TargetSystemVersion,
            TargetCode = TargetCode,
            Relationship = Relationship,
            ExplicitNoMap = ExplicitNoMap,
            SourceRelativeFilename = SourceRelativeFilename,
            ConceptMapId = ConceptMapId,
            ConceptMapUrl = ConceptMapUrl,
            GroupIndex = GroupIndex,
            SourceIndex = SourceIndex,
            TargetIndex = TargetIndex,
        };
    }

    private sealed record DbUnsafeCodeRuleOperand(
        int Key,
        string System,
        string? Version,
        string Code,
        bool NoMapOnly,
        string ExcludedRelationshipLiteral,
        string ExcludedVersionLiteral,
        string SourceRelativeFilename,
        int EntryIndex,
        long? NoMapOnlyStoredInteger)
    {
        internal static DbUnsafeCodeRuleOperand Copy(DbUnsafeCodeRule row, StoredColumns? stored = null) => new(
            row.Key,
            row.System,
            row.Version,
            row.Code,
            row.NoMapOnly,
            row.ExcludedRelationshipLiteral,
            row.ExcludedVersionLiteral,
            row.SourceRelativeFilename,
            row.EntryIndex,
            stored!.Integers[0]);

        internal DbUnsafeCodeRule ToRow() => new()
        {
            Key = Key,
            System = System,
            Version = Version,
            Code = Code,
            NoMapOnly = NoMapOnly,
            ExcludedRelationshipLiteral = ExcludedRelationshipLiteral,
            ExcludedVersionLiteral = ExcludedVersionLiteral,
            SourceRelativeFilename = SourceRelativeFilename,
            EntryIndex = EntryIndex,
        };
    }

    private sealed record DbExtensionSubstitutionOperand(
        int Key,
        string ReplacementUrl,
        string? ReplacementName,
        string? ReplacementSourcePackage,
        string? SourceVersion,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes? SourceFhirSequence,
        string? SourceElementId,
        string? SourceTypeReplacement,
        string? SourceFromContextElement,
        string? SourceFromContextExpandedLiteral,
        bool? IsModifier,
        string? ContextsLiteral,
        string? SourceFhirSequenceStoredText,
        long? IsModifierStoredInteger)
    {
        internal static DbExtensionSubstitutionOperand Copy(DbExtensionSubstitution row, StoredColumns? stored = null) => new(
            row.Key,
            row.ReplacementUrl,
            row.ReplacementName,
            row.ReplacementSourcePackage,
            row.SourceVersion,
            row.SourceFhirSequence,
            row.SourceElementId,
            row.SourceTypeReplacement,
            row.SourceFromContextElement,
            row.SourceFromContextExpandedLiteral,
            row.IsModifier,
            row.ContextsLiteral,
            stored!.Text[0],
            stored!.Integers[0]);

        internal DbExtensionSubstitution ToRow() => new()
        {
            Key = Key,
            ReplacementUrl = ReplacementUrl,
            ReplacementName = ReplacementName,
            ReplacementSourcePackage = ReplacementSourcePackage,
            SourceVersion = SourceVersion,
            SourceFhirSequence = SourceFhirSequence,
            SourceElementId = SourceElementId,
            SourceTypeReplacement = SourceTypeReplacement,
            SourceFromContextElement = SourceFromContextElement,
            SourceFromContextExpandedLiteral = SourceFromContextExpandedLiteral,
            IsModifier = IsModifier,
            ContextsLiteral = ContextsLiteral,
        };
    }

    private sealed record DbExternalInclusionOperand(
        int Key,
        Hl7.Fhir.Model.FHIRAllTypes ResourceType,
        string Id,
        string Name,
        string Version,
        bool OverrideVersion,
        string UnversionedUrl,
        string VersionedUrl,
        string Reason,
        string? IncludeInPackages,
        string Json,
        string? ResourceTypeStoredText,
        long? OverrideVersionStoredInteger)
    {
        internal static DbExternalInclusionOperand Copy(DbExternalInclusion row, StoredColumns? stored = null) => new(
            row.Key,
            row.ResourceType,
            row.Id,
            row.Name,
            row.Version,
            row.OverrideVersion,
            row.UnversionedUrl,
            row.VersionedUrl,
            row.Reason,
            row.IncludeInPackages,
            row.Json,
            stored!.Text[0],
            stored!.Integers[0]);

        internal DbExternalInclusion ToRow() => new()
        {
            Key = Key,
            ResourceType = ResourceType,
            Id = Id,
            Name = Name,
            Version = Version,
            OverrideVersion = OverrideVersion,
            UnversionedUrl = UnversionedUrl,
            VersionedUrl = VersionedUrl,
            Reason = Reason,
            IncludeInPackages = IncludeInPackages,
            Json = Json,
        };
    }

    private sealed record DbFhirTypeValueSetOperand(
        int Key,
        string UnversionedUrl)
    {
        internal static DbFhirTypeValueSetOperand Copy(DbFhirTypeValueSet row, StoredColumns? stored = null) => new(
            row.Key,
            row.UnversionedUrl);

        internal DbFhirTypeValueSet ToRow() => new()
        {
            Key = Key,
            UnversionedUrl = UnversionedUrl,
        };
    }

    private sealed record DbValueSetOperand(
        int Key,
        int FhirPackageKey,
        string Id,
        string VersionedUrl,
        string UnversionedUrl,
        string Name,
        string Version,
        string? VersionAlgorithmString,
        string? VersionAlgorithmCoding,
        Hl7.Fhir.Model.PublicationStatus? Status,
        string? Title,
        string? Description,
        string? Purpose,
        string? Narrative,
        string? StandardStatus,
        string? WorkGroup,
        int? FhirMaturity,
        bool? IsExperimental,
        DateTimeOffset? LastChangedDate,
        string? Publisher,
        string? Copyright,
        string? CopyrightLabel,
        string? ApprovalDate,
        string? LastReviewDate,
        DateTimeOffset? EffectivePeriodStart,
        DateTimeOffset? EffectivePeriodEnd,
        string? Topic,
        string? RelatedArtifacts,
        string? Jurisdictions,
        string? UseContexts,
        string? Contacts,
        string? Authors,
        string? Editors,
        string? Reviewers,
        string? Endorsers,
        string? RootExtensions,
        string? SourcePackageMoniker,
        bool CanExpand,
        bool? HasEscapeValveCode,
        string? Message,
        bool IsExcluded,
        int ConceptCount,
        int ActiveConcreteConceptCount,
        string? ReferencedSystems,
        int BindingCountCore,
        Hl7.Fhir.Model.BindingStrength? StrongestBindingCore,
        Hl7.Fhir.Model.BindingStrength? StrongestBindingCoreCode,
        Hl7.Fhir.Model.BindingStrength? StrongestBindingCoreCoding,
        int BindingCountExtended,
        Hl7.Fhir.Model.BindingStrength? StrongestBindingExtended,
        Hl7.Fhir.Model.BindingStrength? StrongestBindingExtendedCode,
        Hl7.Fhir.Model.BindingStrength? StrongestBindingExtendedCoding,
        string? Compose,
        string? StatusStoredText,
        string? LastChangedDateStoredText,
        string? EffectivePeriodStartStoredText,
        string? EffectivePeriodEndStoredText,
        string? StrongestBindingCoreStoredText,
        string? StrongestBindingCoreCodeStoredText,
        string? StrongestBindingCoreCodingStoredText,
        string? StrongestBindingExtendedStoredText,
        string? StrongestBindingExtendedCodeStoredText,
        string? StrongestBindingExtendedCodingStoredText,
        long? IsExperimentalStoredInteger,
        long? CanExpandStoredInteger,
        long? HasEscapeValveCodeStoredInteger,
        long? IsExcludedStoredInteger)
    {
        internal static DbValueSetOperand Copy(DbValueSet row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.Id,
            row.VersionedUrl,
            row.UnversionedUrl,
            row.Name,
            row.Version,
            row.VersionAlgorithmString,
            stored!.Text[0],
            row.Status,
            row.Title,
            row.Description,
            row.Purpose,
            stored!.Text[2],
            row.StandardStatus,
            row.WorkGroup,
            row.FhirMaturity,
            row.IsExperimental,
            row.LastChangedDate,
            row.Publisher,
            row.Copyright,
            row.CopyrightLabel,
            row.ApprovalDate,
            row.LastReviewDate,
            row.EffectivePeriodStart,
            row.EffectivePeriodEnd,
            stored!.Text[6],
            stored!.Text[7],
            stored!.Text[8],
            stored!.Text[9],
            stored!.Text[10],
            stored!.Text[11],
            stored!.Text[12],
            stored!.Text[13],
            stored!.Text[14],
            stored!.Text[15],
            row.SourcePackageMoniker,
            row.CanExpand,
            row.HasEscapeValveCode,
            row.Message,
            row.IsExcluded,
            row.ConceptCount,
            row.ActiveConcreteConceptCount,
            row.ReferencedSystems,
            row.BindingCountCore,
            row.StrongestBindingCore,
            row.StrongestBindingCoreCode,
            row.StrongestBindingCoreCoding,
            row.BindingCountExtended,
            row.StrongestBindingExtended,
            row.StrongestBindingExtendedCode,
            row.StrongestBindingExtendedCoding,
            stored!.Text[22],
            stored!.Text[1],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Text[5],
            stored!.Text[16],
            stored!.Text[17],
            stored!.Text[18],
            stored!.Text[19],
            stored!.Text[20],
            stored!.Text[21],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3]);

        internal DbValueSet ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            Id = Id,
            VersionedUrl = VersionedUrl,
            UnversionedUrl = UnversionedUrl,
            Name = Name,
            Version = Version,
            VersionAlgorithmString = VersionAlgorithmString,
            VersionAlgorithmCoding = VersionAlgorithmCoding is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.Coding>(VersionAlgorithmCoding),
            Status = Status,
            Title = Title,
            Description = Description,
            Purpose = Purpose,
            Narrative = Narrative is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.Narrative>(Narrative),
            StandardStatus = StandardStatus,
            WorkGroup = WorkGroup,
            FhirMaturity = FhirMaturity,
            IsExperimental = IsExperimental,
            LastChangedDate = LastChangedDate,
            Publisher = Publisher,
            Copyright = Copyright,
            CopyrightLabel = CopyrightLabel,
            ApprovalDate = ApprovalDate,
            LastReviewDate = LastReviewDate,
            EffectivePeriodStart = EffectivePeriodStart,
            EffectivePeriodEnd = EffectivePeriodEnd,
            Topic = Topic is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeableConcept>(Topic),
            RelatedArtifacts = RelatedArtifacts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.RelatedArtifact>(RelatedArtifacts),
            Jurisdictions = Jurisdictions is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeableConcept>(Jurisdictions),
            UseContexts = UseContexts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.UsageContext>(UseContexts),
            Contacts = Contacts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Contacts),
            Authors = Authors is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Authors),
            Editors = Editors is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Editors),
            Reviewers = Reviewers is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Reviewers),
            Endorsers = Endorsers is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Endorsers),
            RootExtensions = RootExtensions is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.Extension>(RootExtensions),
            SourcePackageMoniker = SourcePackageMoniker,
            CanExpand = CanExpand,
            HasEscapeValveCode = HasEscapeValveCode,
            Message = Message,
            IsExcluded = IsExcluded,
            ConceptCount = ConceptCount,
            ActiveConcreteConceptCount = ActiveConcreteConceptCount,
            ReferencedSystems = ReferencedSystems,
            BindingCountCore = BindingCountCore,
            StrongestBindingCore = StrongestBindingCore,
            StrongestBindingCoreCode = StrongestBindingCoreCode,
            StrongestBindingCoreCoding = StrongestBindingCoreCoding,
            BindingCountExtended = BindingCountExtended,
            StrongestBindingExtended = StrongestBindingExtended,
            StrongestBindingExtendedCode = StrongestBindingExtendedCode,
            StrongestBindingExtendedCoding = StrongestBindingExtendedCoding,
            Compose = Compose is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.ValueSet.ComposeComponent>(Compose),
        };
    }

    private sealed record DbValueSetConceptOperand(
        int Key,
        int FhirPackageKey,
        int ValueSetKey,
        string System,
        string SystemVersion,
        string Code,
        string? Display,
        bool Inactive,
        bool Abstract,
        string? Properties,
        int? CodeSystemConceptKey,
        long? InactiveStoredInteger,
        long? AbstractStoredInteger)
    {
        internal static DbValueSetConceptOperand Copy(DbValueSetConcept row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.ValueSetKey,
            row.System,
            row.SystemVersion,
            row.Code,
            row.Display,
            row.Inactive,
            row.Abstract,
            row.Properties,
            row.CodeSystemConceptKey,
            stored!.Integers[0],
            stored!.Integers[1]);

        internal DbValueSetConcept ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            ValueSetKey = ValueSetKey,
            System = System,
            SystemVersion = SystemVersion,
            Code = Code,
            Display = Display,
            Inactive = Inactive,
            Abstract = Abstract,
            Properties = Properties,
            CodeSystemConceptKey = CodeSystemConceptKey,
        };
    }

    private sealed record DbCodeSystemOperand(
        int Key,
        int FhirPackageKey,
        string Id,
        string VersionedUrl,
        string UnversionedUrl,
        string Name,
        string Version,
        string? VersionAlgorithmString,
        string? VersionAlgorithmCoding,
        Hl7.Fhir.Model.PublicationStatus? Status,
        string? Title,
        string? Description,
        string? Purpose,
        string? Narrative,
        string? StandardStatus,
        string? WorkGroup,
        int? FhirMaturity,
        bool? IsExperimental,
        DateTimeOffset? LastChangedDate,
        string? Publisher,
        string? Copyright,
        string? CopyrightLabel,
        string? ApprovalDate,
        string? LastReviewDate,
        DateTimeOffset? EffectivePeriodStart,
        DateTimeOffset? EffectivePeriodEnd,
        string? Topic,
        string? RelatedArtifacts,
        string? Jurisdictions,
        string? UseContexts,
        string? Contacts,
        string? Authors,
        string? Editors,
        string? Reviewers,
        string? Endorsers,
        string? RootExtensions,
        string? SourcePackageMoniker,
        bool? IsCaseSensitive,
        string? ValueSetVersioned,
        string? ValueSetUnversioned,
        Hl7.Fhir.Model.CodeSystem.CodeSystemHierarchyMeaning? HierarchyMeaning,
        bool? IsCompositional,
        bool? VersionNeeded,
        Hl7.Fhir.Model.CodeSystemContentMode? Content,
        string? SupplementsVersioned,
        string? SupplementsUnversioned,
        int? Count,
        string? StatusStoredText,
        string? LastChangedDateStoredText,
        string? EffectivePeriodStartStoredText,
        string? EffectivePeriodEndStoredText,
        string? HierarchyMeaningStoredText,
        string? ContentStoredText,
        long? IsExperimentalStoredInteger,
        long? IsCaseSensitiveStoredInteger,
        long? IsCompositionalStoredInteger,
        long? VersionNeededStoredInteger)
    {
        internal static DbCodeSystemOperand Copy(DbCodeSystem row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.Id,
            row.VersionedUrl,
            row.UnversionedUrl,
            row.Name,
            row.Version,
            row.VersionAlgorithmString,
            stored!.Text[0],
            row.Status,
            row.Title,
            row.Description,
            row.Purpose,
            stored!.Text[2],
            row.StandardStatus,
            row.WorkGroup,
            row.FhirMaturity,
            row.IsExperimental,
            row.LastChangedDate,
            row.Publisher,
            row.Copyright,
            row.CopyrightLabel,
            row.ApprovalDate,
            row.LastReviewDate,
            row.EffectivePeriodStart,
            row.EffectivePeriodEnd,
            stored!.Text[6],
            stored!.Text[7],
            stored!.Text[8],
            stored!.Text[9],
            stored!.Text[10],
            stored!.Text[11],
            stored!.Text[12],
            stored!.Text[13],
            stored!.Text[14],
            stored!.Text[15],
            row.SourcePackageMoniker,
            row.IsCaseSensitive,
            row.ValueSetVersioned,
            row.ValueSetUnversioned,
            row.HierarchyMeaning,
            row.IsCompositional,
            row.VersionNeeded,
            row.Content,
            row.SupplementsVersioned,
            row.SupplementsUnversioned,
            row.Count,
            stored!.Text[1],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Text[5],
            stored!.Text[16],
            stored!.Text[17],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3]);

        internal DbCodeSystem ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            Id = Id,
            VersionedUrl = VersionedUrl,
            UnversionedUrl = UnversionedUrl,
            Name = Name,
            Version = Version,
            VersionAlgorithmString = VersionAlgorithmString,
            VersionAlgorithmCoding = VersionAlgorithmCoding is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.Coding>(VersionAlgorithmCoding),
            Status = Status,
            Title = Title,
            Description = Description,
            Purpose = Purpose,
            Narrative = Narrative is null ? null : CgFhirUtils.ParseFromDb<Hl7.Fhir.Model.Narrative>(Narrative),
            StandardStatus = StandardStatus,
            WorkGroup = WorkGroup,
            FhirMaturity = FhirMaturity,
            IsExperimental = IsExperimental,
            LastChangedDate = LastChangedDate,
            Publisher = Publisher,
            Copyright = Copyright,
            CopyrightLabel = CopyrightLabel,
            ApprovalDate = ApprovalDate,
            LastReviewDate = LastReviewDate,
            EffectivePeriodStart = EffectivePeriodStart,
            EffectivePeriodEnd = EffectivePeriodEnd,
            Topic = Topic is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeableConcept>(Topic),
            RelatedArtifacts = RelatedArtifacts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.RelatedArtifact>(RelatedArtifacts),
            Jurisdictions = Jurisdictions is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeableConcept>(Jurisdictions),
            UseContexts = UseContexts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.UsageContext>(UseContexts),
            Contacts = Contacts is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Contacts),
            Authors = Authors is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Authors),
            Editors = Editors is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Editors),
            Reviewers = Reviewers is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Reviewers),
            Endorsers = Endorsers is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.ContactDetail>(Endorsers),
            RootExtensions = RootExtensions is null ? null : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.Extension>(RootExtensions),
            SourcePackageMoniker = SourcePackageMoniker,
            IsCaseSensitive = IsCaseSensitive,
            ValueSetVersioned = ValueSetVersioned,
            ValueSetUnversioned = ValueSetUnversioned,
            HierarchyMeaning = HierarchyMeaning,
            IsCompositional = IsCompositional,
            VersionNeeded = VersionNeeded,
            Content = Content,
            SupplementsVersioned = SupplementsVersioned,
            SupplementsUnversioned = SupplementsUnversioned,
            Count = Count,
        };
    }

    private sealed record DbCodeSystemConceptOperand(
        int Key,
        int FhirPackageKey,
        int CodeSystemKey,
        int FlatOrder,
        int RelativeOrder,
        string Code,
        string? Display,
        string? Definition,
        string? Designations,
        string? Properties,
        int? ParentConceptKey,
        int ChildConceptCount)
    {
        internal static DbCodeSystemConceptOperand Copy(DbCodeSystemConcept row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.CodeSystemKey,
            row.FlatOrder,
            row.RelativeOrder,
            row.Code,
            row.Display,
            row.Definition,
            stored!.Text[0],
            stored!.Text[1],
            row.ParentConceptKey,
            row.ChildConceptCount);

        internal DbCodeSystemConcept ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            CodeSystemKey = CodeSystemKey,
            FlatOrder = FlatOrder,
            RelativeOrder = RelativeOrder,
            Code = Code,
            Display = Display,
            Definition = Definition,
            Designations = Designations is null ? [] : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeSystem.DesignationComponent>(Designations),
            Properties = Properties is null ? [] : CgFhirUtils.ParseArrayFromDb<Hl7.Fhir.Model.CodeSystem.ConceptPropertyComponent>(Properties),
            ParentConceptKey = ParentConceptKey,
            ChildConceptCount = ChildConceptCount,
        };
    }

    private sealed record DbCodeSystemConceptPropertyOperand(
        int Key,
        int FhirPackageKey,
        int CodeSystemConceptKey,
        int CodeSystemPropertyDefinitionKey,
        string Code,
        Hl7.Fhir.Model.CodeSystem.PropertyType Type,
        string Value,
        string? TypeStoredText)
    {
        internal static DbCodeSystemConceptPropertyOperand Copy(DbCodeSystemConceptProperty row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.CodeSystemConceptKey,
            row.CodeSystemPropertyDefinitionKey,
            row.Code,
            row.Type,
            row.Value,
            stored!.Text[0]);

        internal DbCodeSystemConceptProperty ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            CodeSystemConceptKey = CodeSystemConceptKey,
            CodeSystemPropertyDefinitionKey = CodeSystemPropertyDefinitionKey,
            Code = Code,
            Type = Type,
            Value = Value,
        };
    }

    private sealed record DbCodeSystemPropertyDefinitionOperand(
        int Key,
        int FhirPackageKey,
        int CodeSystemKey,
        string Code,
        string? Uri,
        string? Description,
        Hl7.Fhir.Model.CodeSystem.PropertyType Type,
        string? TypeStoredText)
    {
        internal static DbCodeSystemPropertyDefinitionOperand Copy(DbCodeSystemPropertyDefinition row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.CodeSystemKey,
            row.Code,
            row.Uri,
            row.Description,
            row.Type,
            stored!.Text[0]);

        internal DbCodeSystemPropertyDefinition ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            CodeSystemKey = CodeSystemKey,
            Code = Code,
            Uri = Uri,
            Description = Description,
            Type = Type,
        };
    }

    private sealed record DbCodeSystemFilterOperand(
        int Key,
        int FhirPackageKey,
        int CodeSystemKey,
        string Code,
        string? Description,
        string Operators,
        string Value)
    {
        internal static DbCodeSystemFilterOperand Copy(DbCodeSystemFilter row, StoredColumns? stored = null) => new(
            row.Key,
            row.FhirPackageKey,
            row.CodeSystemKey,
            row.Code,
            row.Description,
            row.Operators,
            row.Value);

        internal DbCodeSystemFilter ToRow() => new()
        {
            Key = Key,
            FhirPackageKey = FhirPackageKey,
            CodeSystemKey = CodeSystemKey,
            Code = Code,
            Description = Description,
            Operators = Operators,
            Value = Value,
        };
    }

    private sealed record DbStructureComparisonOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int Steps,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool NotMapped,
        bool? IsIdentical,
        string? TechnicalMessage,
        string? UserMessage,
        int? ContentKeyR2,
        int? ContentKeyR3,
        int? ContentKeyR4,
        int? ContentKeyR4B,
        int? ContentKeyR5,
        int? ContentKeyR6,
        string SourceCanonicalVersioned,
        string SourceCanonicalUnversioned,
        string SourceId,
        string SourceName,
        string SourceVersion,
        string? TargetCanonicalVersioned,
        string? TargetCanonicalUnversioned,
        string? TargetId,
        string? TargetName,
        string? TargetVersion,
        int SourceStructureKey,
        int? TargetStructureKey,
        int? StructureMappingKey,
        bool? ElementRelativePathsAreIdentical,
        bool IsBasedOnFallbackMapping,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        string? ConceptDomainRelationshipStoredText,
        string? ValueDomainRelationshipStoredText,
        long? NotMappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? ElementRelativePathsAreIdenticalStoredInteger,
        long? IsBasedOnFallbackMappingStoredInteger)
    {
        internal static DbStructureComparisonOperand Copy(DbStructureComparison row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.Steps,
            row.Relationship,
            row.NotMapped,
            row.IsIdentical,
            row.TechnicalMessage,
            row.UserMessage,
            row.ContentKeyR2,
            row.ContentKeyR3,
            row.ContentKeyR4,
            row.ContentKeyR4B,
            row.ContentKeyR5,
            row.ContentKeyR6,
            row.SourceCanonicalVersioned,
            row.SourceCanonicalUnversioned,
            row.SourceId,
            row.SourceName,
            row.SourceVersion,
            row.TargetCanonicalVersioned,
            row.TargetCanonicalUnversioned,
            row.TargetId,
            row.TargetName,
            row.TargetVersion,
            row.SourceStructureKey,
            row.TargetStructureKey,
            row.StructureMappingKey,
            row.ElementRelativePathsAreIdentical,
            row.IsBasedOnFallbackMapping,
            row.ConceptDomainRelationship,
            row.ValueDomainRelationship,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3]);

        internal DbStructureComparison ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            Steps = Steps,
            Relationship = Relationship,
            NotMapped = NotMapped,
            IsIdentical = IsIdentical,
            TechnicalMessage = TechnicalMessage,
            UserMessage = UserMessage,
            ContentKeyR2 = ContentKeyR2,
            ContentKeyR3 = ContentKeyR3,
            ContentKeyR4 = ContentKeyR4,
            ContentKeyR4B = ContentKeyR4B,
            ContentKeyR5 = ContentKeyR5,
            ContentKeyR6 = ContentKeyR6,
            SourceCanonicalVersioned = SourceCanonicalVersioned,
            SourceCanonicalUnversioned = SourceCanonicalUnversioned,
            SourceId = SourceId,
            SourceName = SourceName,
            SourceVersion = SourceVersion,
            TargetCanonicalVersioned = TargetCanonicalVersioned,
            TargetCanonicalUnversioned = TargetCanonicalUnversioned,
            TargetId = TargetId,
            TargetName = TargetName,
            TargetVersion = TargetVersion,
            SourceStructureKey = SourceStructureKey,
            TargetStructureKey = TargetStructureKey,
            StructureMappingKey = StructureMappingKey,
            ElementRelativePathsAreIdentical = ElementRelativePathsAreIdentical,
            IsBasedOnFallbackMapping = IsBasedOnFallbackMapping,
            ConceptDomainRelationship = ConceptDomainRelationship,
            ValueDomainRelationship = ValueDomainRelationship,
        };
    }

    private sealed record DbElementComparisonOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int Steps,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool NotMapped,
        bool? IsIdentical,
        string? TechnicalMessage,
        string? UserMessage,
        int? ContentKeyR2,
        int? ContentKeyR3,
        int? ContentKeyR4,
        int? ContentKeyR4B,
        int? ContentKeyR5,
        int? ContentKeyR6,
        int StructureComparisonKey,
        int SourceStructureKey,
        int SourceElementKey,
        string SourceElementId,
        int? TargetStructureKey,
        int? TargetElementKey,
        string? TargetElementId,
        int? ElementMappingKey,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship,
        bool? RelativePathsAreIdentical,
        int? BoundValueSetComparisonKey,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? BoundValueSetRelationship,
        bool? SourceRequiresMoreValues,
        bool? TargetRequiresMoreValues,
        bool? SourceAllowsMoreValues,
        bool? TargetAllowsMoreValues,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeRelationship,
        string? TypeMessage,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TargetProfileRelationship,
        string? TargetProfileMessage,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeProfileRelationship,
        string? TypeProfileMessage,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        string? ConceptDomainRelationshipStoredText,
        string? ValueDomainRelationshipStoredText,
        string? BoundValueSetRelationshipStoredText,
        string? TypeRelationshipStoredText,
        string? TargetProfileRelationshipStoredText,
        string? TypeProfileRelationshipStoredText,
        long? NotMappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? RelativePathsAreIdenticalStoredInteger,
        long? SourceRequiresMoreValuesStoredInteger,
        long? TargetRequiresMoreValuesStoredInteger,
        long? SourceAllowsMoreValuesStoredInteger,
        long? TargetAllowsMoreValuesStoredInteger)
    {
        internal static DbElementComparisonOperand Copy(DbElementComparison row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.Steps,
            row.Relationship,
            row.NotMapped,
            row.IsIdentical,
            row.TechnicalMessage,
            row.UserMessage,
            row.ContentKeyR2,
            row.ContentKeyR3,
            row.ContentKeyR4,
            row.ContentKeyR4B,
            row.ContentKeyR5,
            row.ContentKeyR6,
            row.StructureComparisonKey,
            row.SourceStructureKey,
            row.SourceElementKey,
            row.SourceElementId,
            row.TargetStructureKey,
            row.TargetElementKey,
            row.TargetElementId,
            row.ElementMappingKey,
            row.ConceptDomainRelationship,
            row.ValueDomainRelationship,
            row.RelativePathsAreIdentical,
            row.BoundValueSetComparisonKey,
            row.BoundValueSetRelationship,
            row.SourceRequiresMoreValues,
            row.TargetRequiresMoreValues,
            row.SourceAllowsMoreValues,
            row.TargetAllowsMoreValues,
            row.TypeRelationship,
            row.TypeMessage,
            row.TargetProfileRelationship,
            row.TargetProfileMessage,
            row.TypeProfileRelationship,
            row.TypeProfileMessage,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Text[5],
            stored!.Text[6],
            stored!.Text[7],
            stored!.Text[8],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4],
            stored!.Integers[5],
            stored!.Integers[6]);

        internal DbElementComparison ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            Steps = Steps,
            Relationship = Relationship,
            NotMapped = NotMapped,
            IsIdentical = IsIdentical,
            TechnicalMessage = TechnicalMessage,
            UserMessage = UserMessage,
            ContentKeyR2 = ContentKeyR2,
            ContentKeyR3 = ContentKeyR3,
            ContentKeyR4 = ContentKeyR4,
            ContentKeyR4B = ContentKeyR4B,
            ContentKeyR5 = ContentKeyR5,
            ContentKeyR6 = ContentKeyR6,
            StructureComparisonKey = StructureComparisonKey,
            SourceStructureKey = SourceStructureKey,
            SourceElementKey = SourceElementKey,
            SourceElementId = SourceElementId,
            TargetStructureKey = TargetStructureKey,
            TargetElementKey = TargetElementKey,
            TargetElementId = TargetElementId,
            ElementMappingKey = ElementMappingKey,
            ConceptDomainRelationship = ConceptDomainRelationship,
            ValueDomainRelationship = ValueDomainRelationship,
            RelativePathsAreIdentical = RelativePathsAreIdentical,
            BoundValueSetComparisonKey = BoundValueSetComparisonKey,
            BoundValueSetRelationship = BoundValueSetRelationship,
            SourceRequiresMoreValues = SourceRequiresMoreValues,
            TargetRequiresMoreValues = TargetRequiresMoreValues,
            SourceAllowsMoreValues = SourceAllowsMoreValues,
            TargetAllowsMoreValues = TargetAllowsMoreValues,
            TypeRelationship = TypeRelationship,
            TypeMessage = TypeMessage,
            TargetProfileRelationship = TargetProfileRelationship,
            TargetProfileMessage = TargetProfileMessage,
            TypeProfileRelationship = TypeProfileRelationship,
            TypeProfileMessage = TypeProfileMessage,
        };
    }

    private sealed record DbElementTypeComparisonOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int Steps,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool NotMapped,
        bool? IsIdentical,
        string? TechnicalMessage,
        string? UserMessage,
        int? ContentKeyR2,
        int? ContentKeyR3,
        int? ContentKeyR4,
        int? ContentKeyR4B,
        int? ContentKeyR5,
        int? ContentKeyR6,
        int ElementComparisonKey,
        int SourceElementKey,
        string SourceElementId,
        int SourceElementTypeKey,
        string SourceTypeLiteral,
        int? TargetElementKey,
        string? TargetElementId,
        int? TargetElementTypeKey,
        string? TargetTypeLiteral,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TargetProfileRelationship,
        string? TargetProfileMessage,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeProfileRelationship,
        string? TypeProfileMessage,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        string? TargetProfileRelationshipStoredText,
        string? TypeProfileRelationshipStoredText,
        long? NotMappedStoredInteger,
        long? IsIdenticalStoredInteger)
    {
        internal static DbElementTypeComparisonOperand Copy(DbElementTypeComparison row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.Steps,
            row.Relationship,
            row.NotMapped,
            row.IsIdentical,
            row.TechnicalMessage,
            row.UserMessage,
            row.ContentKeyR2,
            row.ContentKeyR3,
            row.ContentKeyR4,
            row.ContentKeyR4B,
            row.ContentKeyR5,
            row.ContentKeyR6,
            row.ElementComparisonKey,
            row.SourceElementKey,
            row.SourceElementId,
            row.SourceElementTypeKey,
            row.SourceTypeLiteral,
            row.TargetElementKey,
            row.TargetElementId,
            row.TargetElementTypeKey,
            row.TargetTypeLiteral,
            row.TargetProfileRelationship,
            row.TargetProfileMessage,
            row.TypeProfileRelationship,
            row.TypeProfileMessage,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Text[3],
            stored!.Text[4],
            stored!.Integers[0],
            stored!.Integers[1]);

        internal DbElementTypeComparison ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            Steps = Steps,
            Relationship = Relationship,
            NotMapped = NotMapped,
            IsIdentical = IsIdentical,
            TechnicalMessage = TechnicalMessage,
            UserMessage = UserMessage,
            ContentKeyR2 = ContentKeyR2,
            ContentKeyR3 = ContentKeyR3,
            ContentKeyR4 = ContentKeyR4,
            ContentKeyR4B = ContentKeyR4B,
            ContentKeyR5 = ContentKeyR5,
            ContentKeyR6 = ContentKeyR6,
            ElementComparisonKey = ElementComparisonKey,
            SourceElementKey = SourceElementKey,
            SourceElementId = SourceElementId,
            SourceElementTypeKey = SourceElementTypeKey,
            SourceTypeLiteral = SourceTypeLiteral,
            TargetElementKey = TargetElementKey,
            TargetElementId = TargetElementId,
            TargetElementTypeKey = TargetElementTypeKey,
            TargetTypeLiteral = TargetTypeLiteral,
            TargetProfileRelationship = TargetProfileRelationship,
            TargetProfileMessage = TargetProfileMessage,
            TypeProfileRelationship = TypeProfileRelationship,
            TypeProfileMessage = TypeProfileMessage,
        };
    }

    private sealed record DbValueSetComparisonOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int Steps,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool NotMapped,
        bool? IsIdentical,
        string? TechnicalMessage,
        string? UserMessage,
        int? ContentKeyR2,
        int? ContentKeyR3,
        int? ContentKeyR4,
        int? ContentKeyR4B,
        int? ContentKeyR5,
        int? ContentKeyR6,
        string SourceCanonicalVersioned,
        string SourceCanonicalUnversioned,
        string SourceId,
        string SourceName,
        string SourceVersion,
        string? TargetCanonicalVersioned,
        string? TargetCanonicalUnversioned,
        string? TargetId,
        string? TargetName,
        string? TargetVersion,
        int SourceValueSetKey,
        int? TargetValueSetKey,
        int? ValueSetMappingKey,
        bool? CodeLiteralsAreIdentical,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        long? NotMappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? CodeLiteralsAreIdenticalStoredInteger)
    {
        internal static DbValueSetComparisonOperand Copy(DbValueSetComparison row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.Steps,
            row.Relationship,
            row.NotMapped,
            row.IsIdentical,
            row.TechnicalMessage,
            row.UserMessage,
            row.ContentKeyR2,
            row.ContentKeyR3,
            row.ContentKeyR4,
            row.ContentKeyR4B,
            row.ContentKeyR5,
            row.ContentKeyR6,
            row.SourceCanonicalVersioned,
            row.SourceCanonicalUnversioned,
            row.SourceId,
            row.SourceName,
            row.SourceVersion,
            row.TargetCanonicalVersioned,
            row.TargetCanonicalUnversioned,
            row.TargetId,
            row.TargetName,
            row.TargetVersion,
            row.SourceValueSetKey,
            row.TargetValueSetKey,
            row.ValueSetMappingKey,
            row.CodeLiteralsAreIdentical,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2]);

        internal DbValueSetComparison ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            Steps = Steps,
            Relationship = Relationship,
            NotMapped = NotMapped,
            IsIdentical = IsIdentical,
            TechnicalMessage = TechnicalMessage,
            UserMessage = UserMessage,
            ContentKeyR2 = ContentKeyR2,
            ContentKeyR3 = ContentKeyR3,
            ContentKeyR4 = ContentKeyR4,
            ContentKeyR4B = ContentKeyR4B,
            ContentKeyR5 = ContentKeyR5,
            ContentKeyR6 = ContentKeyR6,
            SourceCanonicalVersioned = SourceCanonicalVersioned,
            SourceCanonicalUnversioned = SourceCanonicalUnversioned,
            SourceId = SourceId,
            SourceName = SourceName,
            SourceVersion = SourceVersion,
            TargetCanonicalVersioned = TargetCanonicalVersioned,
            TargetCanonicalUnversioned = TargetCanonicalUnversioned,
            TargetId = TargetId,
            TargetName = TargetName,
            TargetVersion = TargetVersion,
            SourceValueSetKey = SourceValueSetKey,
            TargetValueSetKey = TargetValueSetKey,
            ValueSetMappingKey = ValueSetMappingKey,
            CodeLiteralsAreIdentical = CodeLiteralsAreIdentical,
        };
    }

    private sealed record DbValueSetConceptComparisonOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        int Steps,
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship,
        bool NotMapped,
        bool? IsIdentical,
        string? TechnicalMessage,
        string? UserMessage,
        int? ContentKeyR2,
        int? ContentKeyR3,
        int? ContentKeyR4,
        int? ContentKeyR4B,
        int? ContentKeyR5,
        int? ContentKeyR6,
        int ValueSetComparisonKey,
        int SourceValueSetKey,
        int SourceConceptKey,
        string SourceSystem,
        string SourceCode,
        int? TargetValueSetKey,
        int? TargetConceptKey,
        string? TargetSystem,
        string? TargetCode,
        int? ValueSetConceptMappingKey,
        bool? CodeLiteralsAreIdentical,
        bool SourceCodeTreatedAsEscapeValve,
        bool? TargetCodeTreatedAsEscapeValve,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? RelationshipStoredText,
        long? NotMappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? CodeLiteralsAreIdenticalStoredInteger,
        long? SourceCodeTreatedAsEscapeValveStoredInteger,
        long? TargetCodeTreatedAsEscapeValveStoredInteger)
    {
        internal static DbValueSetConceptComparisonOperand Copy(DbValueSetConceptComparison row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.Steps,
            row.Relationship,
            row.NotMapped,
            row.IsIdentical,
            row.TechnicalMessage,
            row.UserMessage,
            row.ContentKeyR2,
            row.ContentKeyR3,
            row.ContentKeyR4,
            row.ContentKeyR4B,
            row.ContentKeyR5,
            row.ContentKeyR6,
            row.ValueSetComparisonKey,
            row.SourceValueSetKey,
            row.SourceConceptKey,
            row.SourceSystem,
            row.SourceCode,
            row.TargetValueSetKey,
            row.TargetConceptKey,
            row.TargetSystem,
            row.TargetCode,
            row.ValueSetConceptMappingKey,
            row.CodeLiteralsAreIdentical,
            row.SourceCodeTreatedAsEscapeValve,
            row.TargetCodeTreatedAsEscapeValve,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4]);

        internal DbValueSetConceptComparison ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            Steps = Steps,
            Relationship = Relationship,
            NotMapped = NotMapped,
            IsIdentical = IsIdentical,
            TechnicalMessage = TechnicalMessage,
            UserMessage = UserMessage,
            ContentKeyR2 = ContentKeyR2,
            ContentKeyR3 = ContentKeyR3,
            ContentKeyR4 = ContentKeyR4,
            ContentKeyR4B = ContentKeyR4B,
            ContentKeyR5 = ContentKeyR5,
            ContentKeyR6 = ContentKeyR6,
            ValueSetComparisonKey = ValueSetComparisonKey,
            SourceValueSetKey = SourceValueSetKey,
            SourceConceptKey = SourceConceptKey,
            SourceSystem = SourceSystem,
            SourceCode = SourceCode,
            TargetValueSetKey = TargetValueSetKey,
            TargetConceptKey = TargetConceptKey,
            TargetSystem = TargetSystem,
            TargetCode = TargetCode,
            ValueSetConceptMappingKey = ValueSetConceptMappingKey,
            CodeLiteralsAreIdentical = CodeLiteralsAreIdentical,
            SourceCodeTreatedAsEscapeValve = SourceCodeTreatedAsEscapeValve,
            TargetCodeTreatedAsEscapeValve = TargetCodeTreatedAsEscapeValve,
        };
    }

    private sealed record DbUnsafeCodePairStateOperand(
        int Key,
        int SourceFhirPackageKey,
        int TargetFhirPackageKey,
        string? InputRevision,
        string? ComparisonRevision,
        string? OutcomeInputRevision,
        string? OutcomeComparisonRevision,
        int? OutcomeFormatVersion)
    {
        internal static DbUnsafeCodePairStateOperand Copy(DbUnsafeCodePairState row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.TargetFhirPackageKey,
            row.InputRevision,
            row.ComparisonRevision,
            row.OutcomeInputRevision,
            row.OutcomeComparisonRevision,
            row.OutcomeFormatVersion);

        internal DbUnsafeCodePairState ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            TargetFhirPackageKey = TargetFhirPackageKey,
            InputRevision = InputRevision,
            ComparisonRevision = ComparisonRevision,
            OutcomeInputRevision = OutcomeInputRevision,
            OutcomeComparisonRevision = OutcomeComparisonRevision,
            OutcomeFormatVersion = OutcomeFormatVersion,
        };
    }

    private sealed record DbStructureOutcomeOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        bool RequiresXVerDefinition,
        int TotalTargetCount,
        int TotalSourceCount,
        bool IsRenamed,
        bool IsUnmapped,
        bool IsIdentical,
        bool IsEquivalent,
        bool IsBroaderThanTarget,
        bool IsNarrowerThanTarget,
        bool FullyMapsToThisTarget,
        bool FullyMapsAcrossAllTargets,
        string Comments,
        string? GenLongId,
        string? GenShortId,
        string? GenUrl,
        string? GenName,
        string? GenFileName,
        string? GenArtifactShort,
        string? GenArtifactDescription,
        string? GenArtifactComment,
        string? GenMappingComment,
        string SourceCanonicalVersioned,
        string SourceCanonicalUnversioned,
        string SourceId,
        string SourceName,
        string SourceVersion,
        string? TargetCanonicalVersioned,
        string? TargetCanonicalUnversioned,
        string? TargetId,
        string? TargetName,
        string? TargetVersion,
        int? StructureComparisonKey,
        int SourceStructureKey,
        Fhir.CodeGen.Common.Models.FhirArtifactClassEnum SourceArtifactClass,
        int? TargetStructureKey,
        Fhir.CodeGen.Common.Models.FhirArtifactClassEnum? TargetArtifactClass,
        string? ElementConceptMapLongId,
        string? ElementConceptMapShortId,
        string? ElementConceptMapUrl,
        string? ElementConceptMapName,
        string? ElementConceptMapFileName,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        string? SourceArtifactClassStoredText,
        string? TargetArtifactClassStoredText,
        long? RequiresXVerDefinitionStoredInteger,
        long? IsRenamedStoredInteger,
        long? IsUnmappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? IsEquivalentStoredInteger,
        long? IsBroaderThanTargetStoredInteger,
        long? IsNarrowerThanTargetStoredInteger,
        long? FullyMapsToThisTargetStoredInteger,
        long? FullyMapsAcrossAllTargetsStoredInteger)
    {
        internal static DbStructureOutcomeOperand Copy(DbStructureOutcome row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.RequiresXVerDefinition,
            row.TotalTargetCount,
            row.TotalSourceCount,
            row.IsRenamed,
            row.IsUnmapped,
            row.IsIdentical,
            row.IsEquivalent,
            row.IsBroaderThanTarget,
            row.IsNarrowerThanTarget,
            row.FullyMapsToThisTarget,
            row.FullyMapsAcrossAllTargets,
            row.Comments,
            row.GenLongId,
            row.GenShortId,
            row.GenUrl,
            row.GenName,
            row.GenFileName,
            row.GenArtifactShort,
            row.GenArtifactDescription,
            row.GenArtifactComment,
            row.GenMappingComment,
            row.SourceCanonicalVersioned,
            row.SourceCanonicalUnversioned,
            row.SourceId,
            row.SourceName,
            row.SourceVersion,
            row.TargetCanonicalVersioned,
            row.TargetCanonicalUnversioned,
            row.TargetId,
            row.TargetName,
            row.TargetVersion,
            row.StructureComparisonKey,
            row.SourceStructureKey,
            row.SourceArtifactClass,
            row.TargetStructureKey,
            row.TargetArtifactClass,
            row.ElementConceptMapLongId,
            row.ElementConceptMapShortId,
            row.ElementConceptMapUrl,
            row.ElementConceptMapName,
            row.ElementConceptMapFileName,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Text[2],
            stored!.Text[3],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4],
            stored!.Integers[5],
            stored!.Integers[6],
            stored!.Integers[7],
            stored!.Integers[8]);

        internal DbStructureOutcome ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            RequiresXVerDefinition = RequiresXVerDefinition,
            TotalTargetCount = TotalTargetCount,
            TotalSourceCount = TotalSourceCount,
            IsRenamed = IsRenamed,
            IsUnmapped = IsUnmapped,
            IsIdentical = IsIdentical,
            IsEquivalent = IsEquivalent,
            IsBroaderThanTarget = IsBroaderThanTarget,
            IsNarrowerThanTarget = IsNarrowerThanTarget,
            FullyMapsToThisTarget = FullyMapsToThisTarget,
            FullyMapsAcrossAllTargets = FullyMapsAcrossAllTargets,
            Comments = Comments,
            GenLongId = GenLongId,
            GenShortId = GenShortId,
            GenUrl = GenUrl,
            GenName = GenName,
            GenFileName = GenFileName,
            GenArtifactShort = GenArtifactShort,
            GenArtifactDescription = GenArtifactDescription,
            GenArtifactComment = GenArtifactComment,
            GenMappingComment = GenMappingComment,
            SourceCanonicalVersioned = SourceCanonicalVersioned,
            SourceCanonicalUnversioned = SourceCanonicalUnversioned,
            SourceId = SourceId,
            SourceName = SourceName,
            SourceVersion = SourceVersion,
            TargetCanonicalVersioned = TargetCanonicalVersioned,
            TargetCanonicalUnversioned = TargetCanonicalUnversioned,
            TargetId = TargetId,
            TargetName = TargetName,
            TargetVersion = TargetVersion,
            StructureComparisonKey = StructureComparisonKey,
            SourceStructureKey = SourceStructureKey,
            SourceArtifactClass = SourceArtifactClass,
            TargetStructureKey = TargetStructureKey,
            TargetArtifactClass = TargetArtifactClass,
            ElementConceptMapLongId = ElementConceptMapLongId,
            ElementConceptMapShortId = ElementConceptMapShortId,
            ElementConceptMapUrl = ElementConceptMapUrl,
            ElementConceptMapName = ElementConceptMapName,
            ElementConceptMapFileName = ElementConceptMapFileName,
        };
    }

    private sealed record DbElementOutcomeOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        bool RequiresXVerDefinition,
        int TotalTargetCount,
        int TotalSourceCount,
        bool IsRenamed,
        bool IsUnmapped,
        bool IsIdentical,
        bool IsEquivalent,
        bool IsBroaderThanTarget,
        bool IsNarrowerThanTarget,
        bool FullyMapsToThisTarget,
        bool FullyMapsAcrossAllTargets,
        string Comments,
        string? GenLongId,
        string? GenShortId,
        string? GenUrl,
        string? GenName,
        string? GenFileName,
        string? GenArtifactShort,
        string? GenArtifactDescription,
        string? GenArtifactComment,
        string? GenMappingComment,
        string SourceCanonicalVersioned,
        string SourceCanonicalUnversioned,
        string SourceId,
        string SourceName,
        string SourceVersion,
        int SourceStructureKey,
        int SourceElementKey,
        bool RequiresExtensionDefinition,
        bool RequiresSliceDefinition,
        string? GenSliceName,
        bool RequiresCardinalityDefinition,
        bool RequiresCardinalitySlice,
        int SourceResourceOrder,
        int SourceComponentOrder,
        int SourceMinCardinality,
        string SourceMaxCardinalityString,
        int SourceChildElementCount,
        bool SourceUsedAsContentReference,
        string? SourceAncestorUsedAsContentReferenceId,
        int? SourceAncestorContentReferenceOutcomeKey,
        string? AlternateCanonicalTargetsLiteral,
        string? AlternateCanonicalContextLiteral,
        string? AlternateReferenceTargetsLiteral,
        string? AlternateReferenceContextLiteral,
        int? AncestorElementOutcomeKey,
        int? ParentElementOutcomeKey,
        int? ExtensionSubstitutionKey,
        string? ExtensionSubstitutionUrl,
        string? BasicElementBaseId,
        string? BasicElementId,
        string? ExtensionElementBaseId,
        string? ExtensionElementId,
        int? ContentReferenceOutcomeKey,
        string? ContentReferenceExtensionUrl,
        bool? ContentReferenceRequiresXVerDefinition,
        string? ContentReferenceAncestorId,
        bool? RequiresDefinitionAsContentReference,
        bool ExtensionDefinitionIsProhibited,
        string? ExtensionProhibitionReason,
        int? OutcomeTargetCount,
        bool SourceIsModifier,
        bool DefineAsModifier,
        string? ModifierReason,
        bool SourceIsDeprecated,
        string? ExtensionContextsLiteral,
        string? CardinalityExtensionContextsLiteral,
        string? MappedTypeKeysLiteral,
        string? MappedTypeNamesLiteral,
        string? UnmappedTypeKeysLiteral,
        string? UnmappedTypeNamesLiteral,
        string? MappedTypeChildElementKeysLiteral,
        string? MappedChildTypeElementNamesLiteral,
        string? UnmappedTypeChildElementKeysLiteral,
        string? UnmappedChildTypeElementNamesLiteral,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        long? RequiresXVerDefinitionStoredInteger,
        long? IsRenamedStoredInteger,
        long? IsUnmappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? IsEquivalentStoredInteger,
        long? IsBroaderThanTargetStoredInteger,
        long? IsNarrowerThanTargetStoredInteger,
        long? FullyMapsToThisTargetStoredInteger,
        long? FullyMapsAcrossAllTargetsStoredInteger,
        long? RequiresExtensionDefinitionStoredInteger,
        long? RequiresSliceDefinitionStoredInteger,
        long? RequiresCardinalityDefinitionStoredInteger,
        long? RequiresCardinalitySliceStoredInteger,
        long? SourceUsedAsContentReferenceStoredInteger,
        long? ContentReferenceRequiresXVerDefinitionStoredInteger,
        long? RequiresDefinitionAsContentReferenceStoredInteger,
        long? ExtensionDefinitionIsProhibitedStoredInteger,
        long? SourceIsModifierStoredInteger,
        long? DefineAsModifierStoredInteger,
        long? SourceIsDeprecatedStoredInteger)
    {
        internal static DbElementOutcomeOperand Copy(DbElementOutcome row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.RequiresXVerDefinition,
            row.TotalTargetCount,
            row.TotalSourceCount,
            row.IsRenamed,
            row.IsUnmapped,
            row.IsIdentical,
            row.IsEquivalent,
            row.IsBroaderThanTarget,
            row.IsNarrowerThanTarget,
            row.FullyMapsToThisTarget,
            row.FullyMapsAcrossAllTargets,
            row.Comments,
            row.GenLongId,
            row.GenShortId,
            row.GenUrl,
            row.GenName,
            row.GenFileName,
            row.GenArtifactShort,
            row.GenArtifactDescription,
            row.GenArtifactComment,
            row.GenMappingComment,
            row.SourceCanonicalVersioned,
            row.SourceCanonicalUnversioned,
            row.SourceId,
            row.SourceName,
            row.SourceVersion,
            row.SourceStructureKey,
            row.SourceElementKey,
            row.RequiresExtensionDefinition,
            row.RequiresSliceDefinition,
            row.GenSliceName,
            row.RequiresCardinalityDefinition,
            row.RequiresCardinalitySlice,
            row.SourceResourceOrder,
            row.SourceComponentOrder,
            row.SourceMinCardinality,
            row.SourceMaxCardinalityString,
            row.SourceChildElementCount,
            row.SourceUsedAsContentReference,
            row.SourceAncestorUsedAsContentReferenceId,
            row.SourceAncestorContentReferenceOutcomeKey,
            row.AlternateCanonicalTargetsLiteral,
            row.AlternateCanonicalContextLiteral,
            row.AlternateReferenceTargetsLiteral,
            row.AlternateReferenceContextLiteral,
            row.AncestorElementOutcomeKey,
            row.ParentElementOutcomeKey,
            row.ExtensionSubstitutionKey,
            row.ExtensionSubstitutionUrl,
            row.BasicElementBaseId,
            row.BasicElementId,
            row.ExtensionElementBaseId,
            row.ExtensionElementId,
            row.ContentReferenceOutcomeKey,
            row.ContentReferenceExtensionUrl,
            row.ContentReferenceRequiresXVerDefinition,
            row.ContentReferenceAncestorId,
            row.RequiresDefinitionAsContentReference,
            row.ExtensionDefinitionIsProhibited,
            row.ExtensionProhibitionReason,
            row.OutcomeTargetCount,
            row.SourceIsModifier,
            row.DefineAsModifier,
            row.ModifierReason,
            row.SourceIsDeprecated,
            row.ExtensionContextsLiteral,
            row.CardinalityExtensionContextsLiteral,
            row.MappedTypeKeysLiteral,
            row.MappedTypeNamesLiteral,
            row.UnmappedTypeKeysLiteral,
            row.UnmappedTypeNamesLiteral,
            row.MappedTypeChildElementKeysLiteral,
            row.MappedChildTypeElementNamesLiteral,
            row.UnmappedTypeChildElementKeysLiteral,
            row.UnmappedChildTypeElementNamesLiteral,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4],
            stored!.Integers[5],
            stored!.Integers[6],
            stored!.Integers[7],
            stored!.Integers[8],
            stored!.Integers[9],
            stored!.Integers[10],
            stored!.Integers[11],
            stored!.Integers[12],
            stored!.Integers[13],
            stored!.Integers[14],
            stored!.Integers[15],
            stored!.Integers[16],
            stored!.Integers[17],
            stored!.Integers[18],
            stored!.Integers[19]);

        internal DbElementOutcome ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            RequiresXVerDefinition = RequiresXVerDefinition,
            TotalTargetCount = TotalTargetCount,
            TotalSourceCount = TotalSourceCount,
            IsRenamed = IsRenamed,
            IsUnmapped = IsUnmapped,
            IsIdentical = IsIdentical,
            IsEquivalent = IsEquivalent,
            IsBroaderThanTarget = IsBroaderThanTarget,
            IsNarrowerThanTarget = IsNarrowerThanTarget,
            FullyMapsToThisTarget = FullyMapsToThisTarget,
            FullyMapsAcrossAllTargets = FullyMapsAcrossAllTargets,
            Comments = Comments,
            GenLongId = GenLongId,
            GenShortId = GenShortId,
            GenUrl = GenUrl,
            GenName = GenName,
            GenFileName = GenFileName,
            GenArtifactShort = GenArtifactShort,
            GenArtifactDescription = GenArtifactDescription,
            GenArtifactComment = GenArtifactComment,
            GenMappingComment = GenMappingComment,
            SourceCanonicalVersioned = SourceCanonicalVersioned,
            SourceCanonicalUnversioned = SourceCanonicalUnversioned,
            SourceId = SourceId,
            SourceName = SourceName,
            SourceVersion = SourceVersion,
            SourceStructureKey = SourceStructureKey,
            SourceElementKey = SourceElementKey,
            RequiresExtensionDefinition = RequiresExtensionDefinition,
            RequiresSliceDefinition = RequiresSliceDefinition,
            GenSliceName = GenSliceName,
            RequiresCardinalityDefinition = RequiresCardinalityDefinition,
            RequiresCardinalitySlice = RequiresCardinalitySlice,
            SourceResourceOrder = SourceResourceOrder,
            SourceComponentOrder = SourceComponentOrder,
            SourceMinCardinality = SourceMinCardinality,
            SourceMaxCardinalityString = SourceMaxCardinalityString,
            SourceChildElementCount = SourceChildElementCount,
            SourceUsedAsContentReference = SourceUsedAsContentReference,
            SourceAncestorUsedAsContentReferenceId = SourceAncestorUsedAsContentReferenceId,
            SourceAncestorContentReferenceOutcomeKey = SourceAncestorContentReferenceOutcomeKey,
            AlternateCanonicalTargetsLiteral = AlternateCanonicalTargetsLiteral,
            AlternateCanonicalContextLiteral = AlternateCanonicalContextLiteral,
            AlternateReferenceTargetsLiteral = AlternateReferenceTargetsLiteral,
            AlternateReferenceContextLiteral = AlternateReferenceContextLiteral,
            AncestorElementOutcomeKey = AncestorElementOutcomeKey,
            ParentElementOutcomeKey = ParentElementOutcomeKey,
            ExtensionSubstitutionKey = ExtensionSubstitutionKey,
            ExtensionSubstitutionUrl = ExtensionSubstitutionUrl,
            BasicElementBaseId = BasicElementBaseId,
            BasicElementId = BasicElementId,
            ExtensionElementBaseId = ExtensionElementBaseId,
            ExtensionElementId = ExtensionElementId,
            ContentReferenceOutcomeKey = ContentReferenceOutcomeKey,
            ContentReferenceExtensionUrl = ContentReferenceExtensionUrl,
            ContentReferenceRequiresXVerDefinition = ContentReferenceRequiresXVerDefinition,
            ContentReferenceAncestorId = ContentReferenceAncestorId,
            RequiresDefinitionAsContentReference = RequiresDefinitionAsContentReference,
            ExtensionDefinitionIsProhibited = ExtensionDefinitionIsProhibited,
            ExtensionProhibitionReason = ExtensionProhibitionReason,
            OutcomeTargetCount = OutcomeTargetCount,
            SourceIsModifier = SourceIsModifier,
            DefineAsModifier = DefineAsModifier,
            ModifierReason = ModifierReason,
            SourceIsDeprecated = SourceIsDeprecated,
            ExtensionContextsLiteral = ExtensionContextsLiteral,
            CardinalityExtensionContextsLiteral = CardinalityExtensionContextsLiteral,
            MappedTypeKeysLiteral = MappedTypeKeysLiteral,
            MappedTypeNamesLiteral = MappedTypeNamesLiteral,
            UnmappedTypeKeysLiteral = UnmappedTypeKeysLiteral,
            UnmappedTypeNamesLiteral = UnmappedTypeNamesLiteral,
            MappedTypeChildElementKeysLiteral = MappedTypeChildElementKeysLiteral,
            MappedChildTypeElementNamesLiteral = MappedChildTypeElementNamesLiteral,
            UnmappedTypeChildElementKeysLiteral = UnmappedTypeChildElementKeysLiteral,
            UnmappedChildTypeElementNamesLiteral = UnmappedChildTypeElementNamesLiteral,
        };
    }

    private sealed record DbElementOutcomeTargetOperand(
        int Key,
        int ElementOutcomeKey,
        int StructureOutcomeKey,
        int? ElementComparisonKey,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        bool FullyMapsToThisTarget,
        int? TargetStructureKey,
        int? TargetElementKey,
        string? TargetElementId,
        int? TargetResourceOrder,
        int? TargetComponentOrder,
        int? TargetMinCardinality,
        string? TargetMaxCardinalityString,
        bool? TargetIsModifier,
        int? ContextElementKey,
        string? ContextElementId,
        string? ContextRootExtensionUrl,
        string? ContextParentExtensionUrl,
        int? CardinalityContextElementKey,
        string? CardinalityContextElementId,
        string? CardinalityContextRootExtensionUrl,
        string? CardinalityContextParentExtensionUrl,
        string? Comments,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        long? FullyMapsToThisTargetStoredInteger,
        long? TargetIsModifierStoredInteger)
    {
        internal static DbElementOutcomeTargetOperand Copy(DbElementOutcomeTarget row, StoredColumns? stored = null) => new(
            row.Key,
            row.ElementOutcomeKey,
            row.StructureOutcomeKey,
            row.ElementComparisonKey,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.FullyMapsToThisTarget,
            row.TargetStructureKey,
            row.TargetElementKey,
            row.TargetElementId,
            row.TargetResourceOrder,
            row.TargetComponentOrder,
            row.TargetMinCardinality,
            row.TargetMaxCardinalityString,
            row.TargetIsModifier,
            row.ContextElementKey,
            row.ContextElementId,
            row.ContextRootExtensionUrl,
            row.ContextParentExtensionUrl,
            row.CardinalityContextElementKey,
            row.CardinalityContextElementId,
            row.CardinalityContextRootExtensionUrl,
            row.CardinalityContextParentExtensionUrl,
            row.Comments,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Integers[0],
            stored!.Integers[1]);

        internal DbElementOutcomeTarget ToRow() => new()
        {
            Key = Key,
            ElementOutcomeKey = ElementOutcomeKey,
            StructureOutcomeKey = StructureOutcomeKey,
            ElementComparisonKey = ElementComparisonKey,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            FullyMapsToThisTarget = FullyMapsToThisTarget,
            TargetStructureKey = TargetStructureKey,
            TargetElementKey = TargetElementKey,
            TargetElementId = TargetElementId,
            TargetResourceOrder = TargetResourceOrder,
            TargetComponentOrder = TargetComponentOrder,
            TargetMinCardinality = TargetMinCardinality,
            TargetMaxCardinalityString = TargetMaxCardinalityString,
            TargetIsModifier = TargetIsModifier,
            ContextElementKey = ContextElementKey,
            ContextElementId = ContextElementId,
            ContextRootExtensionUrl = ContextRootExtensionUrl,
            ContextParentExtensionUrl = ContextParentExtensionUrl,
            CardinalityContextElementKey = CardinalityContextElementKey,
            CardinalityContextElementId = CardinalityContextElementId,
            CardinalityContextRootExtensionUrl = CardinalityContextRootExtensionUrl,
            CardinalityContextParentExtensionUrl = CardinalityContextParentExtensionUrl,
            Comments = Comments,
        };
    }

    private sealed record DbUnsafeCodeTriggerOperand(
        int Key,
        int StructureOutcomeKey,
        int SourceFhirPackageKey,
        int TargetFhirPackageKey,
        string SourceElementId,
        string TargetElementId,
        string SourcePathJson,
        string System,
        string Code,
        string? SourceVersion,
        bool IsWildcardVersion,
        string VersionsJson,
        string EvidenceKeysJson,
        string RuleKeysJson,
        string AnnotationKeysJson,
        long? IsWildcardVersionStoredInteger)
    {
        internal static DbUnsafeCodeTriggerOperand Copy(DbUnsafeCodeTrigger row, StoredColumns? stored = null) => new(
            row.Key,
            row.StructureOutcomeKey,
            row.SourceFhirPackageKey,
            row.TargetFhirPackageKey,
            row.SourceElementId,
            row.TargetElementId,
            row.SourcePathJson,
            row.System,
            row.Code,
            row.SourceVersion,
            row.IsWildcardVersion,
            row.VersionsJson,
            row.EvidenceKeysJson,
            row.RuleKeysJson,
            row.AnnotationKeysJson,
            stored!.Integers[0]);

        internal DbUnsafeCodeTrigger ToRow() => new()
        {
            Key = Key,
            StructureOutcomeKey = StructureOutcomeKey,
            SourceFhirPackageKey = SourceFhirPackageKey,
            TargetFhirPackageKey = TargetFhirPackageKey,
            SourceElementId = SourceElementId,
            TargetElementId = TargetElementId,
            SourcePathJson = SourcePathJson,
            System = System,
            Code = Code,
            SourceVersion = SourceVersion,
            IsWildcardVersion = IsWildcardVersion,
            VersionsJson = VersionsJson,
            EvidenceKeysJson = EvidenceKeysJson,
            RuleKeysJson = RuleKeysJson,
            AnnotationKeysJson = AnnotationKeysJson,
        };
    }

    private sealed record DbValueSetOutcomeOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        bool RequiresXVerDefinition,
        int TotalTargetCount,
        int TotalSourceCount,
        bool IsRenamed,
        bool IsUnmapped,
        bool IsIdentical,
        bool IsEquivalent,
        bool IsBroaderThanTarget,
        bool IsNarrowerThanTarget,
        bool FullyMapsToThisTarget,
        bool FullyMapsAcrossAllTargets,
        string Comments,
        string? GenLongId,
        string? GenShortId,
        string? GenUrl,
        string? GenName,
        string? GenFileName,
        string? GenArtifactShort,
        string? GenArtifactDescription,
        string? GenArtifactComment,
        string? GenMappingComment,
        string SourceCanonicalVersioned,
        string SourceCanonicalUnversioned,
        string SourceId,
        string SourceName,
        string SourceVersion,
        string? TargetCanonicalVersioned,
        string? TargetCanonicalUnversioned,
        string? TargetId,
        string? TargetName,
        string? TargetVersion,
        int? ValueSetComparisonKey,
        int SourceValueSetKey,
        int? TargetValueSetKey,
        string? ConceptMapLongId,
        string? ConceptMapShortId,
        string? ConceptMapUrl,
        string? ConceptMapName,
        string? ConceptMapFileName,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        long? RequiresXVerDefinitionStoredInteger,
        long? IsRenamedStoredInteger,
        long? IsUnmappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? IsEquivalentStoredInteger,
        long? IsBroaderThanTargetStoredInteger,
        long? IsNarrowerThanTargetStoredInteger,
        long? FullyMapsToThisTargetStoredInteger,
        long? FullyMapsAcrossAllTargetsStoredInteger)
    {
        internal static DbValueSetOutcomeOperand Copy(DbValueSetOutcome row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.RequiresXVerDefinition,
            row.TotalTargetCount,
            row.TotalSourceCount,
            row.IsRenamed,
            row.IsUnmapped,
            row.IsIdentical,
            row.IsEquivalent,
            row.IsBroaderThanTarget,
            row.IsNarrowerThanTarget,
            row.FullyMapsToThisTarget,
            row.FullyMapsAcrossAllTargets,
            row.Comments,
            row.GenLongId,
            row.GenShortId,
            row.GenUrl,
            row.GenName,
            row.GenFileName,
            row.GenArtifactShort,
            row.GenArtifactDescription,
            row.GenArtifactComment,
            row.GenMappingComment,
            row.SourceCanonicalVersioned,
            row.SourceCanonicalUnversioned,
            row.SourceId,
            row.SourceName,
            row.SourceVersion,
            row.TargetCanonicalVersioned,
            row.TargetCanonicalUnversioned,
            row.TargetId,
            row.TargetName,
            row.TargetVersion,
            row.ValueSetComparisonKey,
            row.SourceValueSetKey,
            row.TargetValueSetKey,
            row.ConceptMapLongId,
            row.ConceptMapShortId,
            row.ConceptMapUrl,
            row.ConceptMapName,
            row.ConceptMapFileName,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4],
            stored!.Integers[5],
            stored!.Integers[6],
            stored!.Integers[7],
            stored!.Integers[8]);

        internal DbValueSetOutcome ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            RequiresXVerDefinition = RequiresXVerDefinition,
            TotalTargetCount = TotalTargetCount,
            TotalSourceCount = TotalSourceCount,
            IsRenamed = IsRenamed,
            IsUnmapped = IsUnmapped,
            IsIdentical = IsIdentical,
            IsEquivalent = IsEquivalent,
            IsBroaderThanTarget = IsBroaderThanTarget,
            IsNarrowerThanTarget = IsNarrowerThanTarget,
            FullyMapsToThisTarget = FullyMapsToThisTarget,
            FullyMapsAcrossAllTargets = FullyMapsAcrossAllTargets,
            Comments = Comments,
            GenLongId = GenLongId,
            GenShortId = GenShortId,
            GenUrl = GenUrl,
            GenName = GenName,
            GenFileName = GenFileName,
            GenArtifactShort = GenArtifactShort,
            GenArtifactDescription = GenArtifactDescription,
            GenArtifactComment = GenArtifactComment,
            GenMappingComment = GenMappingComment,
            SourceCanonicalVersioned = SourceCanonicalVersioned,
            SourceCanonicalUnversioned = SourceCanonicalUnversioned,
            SourceId = SourceId,
            SourceName = SourceName,
            SourceVersion = SourceVersion,
            TargetCanonicalVersioned = TargetCanonicalVersioned,
            TargetCanonicalUnversioned = TargetCanonicalUnversioned,
            TargetId = TargetId,
            TargetName = TargetName,
            TargetVersion = TargetVersion,
            ValueSetComparisonKey = ValueSetComparisonKey,
            SourceValueSetKey = SourceValueSetKey,
            TargetValueSetKey = TargetValueSetKey,
            ConceptMapLongId = ConceptMapLongId,
            ConceptMapShortId = ConceptMapShortId,
            ConceptMapUrl = ConceptMapUrl,
            ConceptMapName = ConceptMapName,
            ConceptMapFileName = ConceptMapFileName,
        };
    }

    private sealed record DbValueSetConceptOutcomeOperand(
        int Key,
        int SourceFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence,
        int TargetFhirPackageKey,
        Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence,
        bool RequiresXVerDefinition,
        int TotalTargetCount,
        int TotalSourceCount,
        bool IsRenamed,
        bool IsUnmapped,
        bool IsIdentical,
        bool IsEquivalent,
        bool IsBroaderThanTarget,
        bool IsNarrowerThanTarget,
        bool FullyMapsToThisTarget,
        bool FullyMapsAcrossAllTargets,
        string Comments,
        int ValueSetOutcomeKey,
        int? ValueSetConceptComparisonKey,
        int SourceValueSetKey,
        int SourceValueSetConceptKey,
        int? TargetValueSetKey,
        int? TargetValueSetConceptKey,
        string SourceSystem,
        string SourceSystemVersion,
        string SourceCode,
        string? SourceDisplay,
        string? TargetSystem,
        string? TargetSystemVersion,
        string? TargetCode,
        string? TargetDisplay,
        string? SourceFhirSequenceStoredText,
        string? TargetFhirSequenceStoredText,
        long? RequiresXVerDefinitionStoredInteger,
        long? IsRenamedStoredInteger,
        long? IsUnmappedStoredInteger,
        long? IsIdenticalStoredInteger,
        long? IsEquivalentStoredInteger,
        long? IsBroaderThanTargetStoredInteger,
        long? IsNarrowerThanTargetStoredInteger,
        long? FullyMapsToThisTargetStoredInteger,
        long? FullyMapsAcrossAllTargetsStoredInteger)
    {
        internal static DbValueSetConceptOutcomeOperand Copy(DbValueSetConceptOutcome row, StoredColumns? stored = null) => new(
            row.Key,
            row.SourceFhirPackageKey,
            row.SourceFhirSequence,
            row.TargetFhirPackageKey,
            row.TargetFhirSequence,
            row.RequiresXVerDefinition,
            row.TotalTargetCount,
            row.TotalSourceCount,
            row.IsRenamed,
            row.IsUnmapped,
            row.IsIdentical,
            row.IsEquivalent,
            row.IsBroaderThanTarget,
            row.IsNarrowerThanTarget,
            row.FullyMapsToThisTarget,
            row.FullyMapsAcrossAllTargets,
            row.Comments,
            row.ValueSetOutcomeKey,
            row.ValueSetConceptComparisonKey,
            row.SourceValueSetKey,
            row.SourceValueSetConceptKey,
            row.TargetValueSetKey,
            row.TargetValueSetConceptKey,
            row.SourceSystem,
            row.SourceSystemVersion,
            row.SourceCode,
            row.SourceDisplay,
            row.TargetSystem,
            row.TargetSystemVersion,
            row.TargetCode,
            row.TargetDisplay,
            stored!.Text[0],
            stored!.Text[1],
            stored!.Integers[0],
            stored!.Integers[1],
            stored!.Integers[2],
            stored!.Integers[3],
            stored!.Integers[4],
            stored!.Integers[5],
            stored!.Integers[6],
            stored!.Integers[7],
            stored!.Integers[8]);

        internal DbValueSetConceptOutcome ToRow() => new()
        {
            Key = Key,
            SourceFhirPackageKey = SourceFhirPackageKey,
            SourceFhirSequence = SourceFhirSequence,
            TargetFhirPackageKey = TargetFhirPackageKey,
            TargetFhirSequence = TargetFhirSequence,
            RequiresXVerDefinition = RequiresXVerDefinition,
            TotalTargetCount = TotalTargetCount,
            TotalSourceCount = TotalSourceCount,
            IsRenamed = IsRenamed,
            IsUnmapped = IsUnmapped,
            IsIdentical = IsIdentical,
            IsEquivalent = IsEquivalent,
            IsBroaderThanTarget = IsBroaderThanTarget,
            IsNarrowerThanTarget = IsNarrowerThanTarget,
            FullyMapsToThisTarget = FullyMapsToThisTarget,
            FullyMapsAcrossAllTargets = FullyMapsAcrossAllTargets,
            Comments = Comments,
            ValueSetOutcomeKey = ValueSetOutcomeKey,
            ValueSetConceptComparisonKey = ValueSetConceptComparisonKey,
            SourceValueSetKey = SourceValueSetKey,
            SourceValueSetConceptKey = SourceValueSetConceptKey,
            TargetValueSetKey = TargetValueSetKey,
            TargetValueSetConceptKey = TargetValueSetConceptKey,
            SourceSystem = SourceSystem,
            SourceSystemVersion = SourceSystemVersion,
            SourceCode = SourceCode,
            SourceDisplay = SourceDisplay,
            TargetSystem = TargetSystem,
            TargetSystemVersion = TargetSystemVersion,
            TargetCode = TargetCode,
            TargetDisplay = TargetDisplay,
        };
    }
}
