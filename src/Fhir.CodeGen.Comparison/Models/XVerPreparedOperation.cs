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
        EscapeValveCodes = XVer.XVerProcessor._escapeValveCodes.ToImmutableHashSet(StringComparer.Ordinal);
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
    internal ImmutableHashSet<string> EscapeValveCodes { get; }
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

internal enum XVerProfileInterpretation
{
    Comparison,
    Classification,
    EmittedOwner,
}

internal sealed class XVerProfileContext : IEquatable<XVerProfileContext>
{
    private XVerProfileContext(XVerProfileInterpretation interpretation, int? ownerKey = null,
        int? outcomeKey = null, IEnumerable<int>? emittedOwnerKeys = null)
    {
        Interpretation = interpretation;
        OwnerKey = ownerKey;
        OutcomeKey = outcomeKey;
        EmittedOwnerKeys = [.. (emittedOwnerKeys ?? []).Distinct().Order()];
        if (interpretation == XVerProfileInterpretation.EmittedOwner &&
            (ownerKey is null || outcomeKey is null || !EmittedOwnerKeys.Contains(ownerKey.Value)))
        {
            throw new ArgumentException("An emitted-owner context requires its actual owner, outcome and owner schedule.");
        }
    }

    internal static XVerProfileContext Comparison { get; } = new(XVerProfileInterpretation.Comparison);
    internal static XVerProfileContext Classification { get; } = new(XVerProfileInterpretation.Classification);
    internal static XVerProfileContext ForOwner(int ownerKey, int outcomeKey, IEnumerable<int> emittedOwnerKeys) =>
        new(XVerProfileInterpretation.EmittedOwner, ownerKey, outcomeKey, emittedOwnerKeys);

    internal XVerProfileInterpretation Interpretation { get; }
    internal int? OwnerKey { get; }
    internal int? OutcomeKey { get; }
    internal ImmutableArray<int> EmittedOwnerKeys { get; }

    public bool Equals(XVerProfileContext? other) => other is not null &&
        Interpretation == other.Interpretation && OwnerKey == other.OwnerKey && OutcomeKey == other.OutcomeKey &&
        EmittedOwnerKeys.SequenceEqual(other.EmittedOwnerKeys);

    public override bool Equals(object? obj) => obj is XVerProfileContext other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Interpretation);
        hash.Add(OwnerKey);
        hash.Add(OutcomeKey);
        foreach (int key in EmittedOwnerKeys)
        {
            hash.Add(key);
        }
        return hash.ToHashCode();
    }
}

// This image covers the directional classifier's comparison reader, not a readiness
// guard. Scheduled writers must still audit/declare their transitions at the guard.
internal sealed class XVerDirectionalOperands
{
    private readonly ImmutableArray<ImmutableArray<object?>> _storedRows;
    private readonly List<DbElementComparison> _comparisons;

    private XVerDirectionalOperands(XVerOperandEvidence evidence, int sourcePackageKey, int targetPackageKey,
        ImmutableArray<ImmutableArray<object?>> storedRows, List<DbElementComparison> comparisons, bool hasCorrespondence = true)
    {
        Evidence = evidence;
        SourcePackageKey = sourcePackageKey;
        TargetPackageKey = targetPackageKey;
        _storedRows = storedRows;
        _comparisons = comparisons;
        HasCorrespondence = hasCorrespondence;
    }

    internal XVerOperandEvidence Evidence { get; }
    internal int SourcePackageKey { get; }
    internal int TargetPackageKey { get; }
    internal bool HasCorrespondence { get; }

    internal static XVerDirectionalOperands WithoutCorrespondence(XVerOperandEvidence evidence, int sourcePackageKey, int targetPackageKey)
    {
        evidence.RequireActive();
        return new(evidence, sourcePackageKey, targetPackageKey, [], [], hasCorrespondence: false);
    }

    internal static XVerDirectionalOperands Capture(IDbConnection db, XVerOperandEvidence evidence,
        int sourcePackageKey, int targetPackageKey)
    {
        try
        {
            return CaptureCore(db, evidence, sourcePackageKey, targetPackageKey);
        }
        catch
        {
            evidence.Dispose();
            throw;
        }
    }

    private static XVerDirectionalOperands CaptureCore(IDbConnection db, XVerOperandEvidence evidence,
        int sourcePackageKey, int targetPackageKey)
    {
        evidence.RequireActive();
        (long, long, long) before = Observe(db);
        XVerOperandEvidence.XVerTableManifest manifest = XVerOperandEvidence.Manifest.Single(table => table.RowType == typeof(DbElementComparison));
        List<ImmutableArray<object?>> stored = [];
        using (IDbCommand command = db.CreateCommand())
        {
            command.CommandText = $"SELECT {string.Join(", ", manifest.PersistedFields)} FROM {manifest.TableName} " +
                "WHERE SourceFhirPackageKey = @source AND TargetFhirPackageKey = @target ORDER BY Key ASC";
            IDbDataParameter source = command.CreateParameter();
            source.ParameterName = "@source";
            source.Value = sourcePackageKey;
            command.Parameters.Add(source);
            IDbDataParameter target = command.CreateParameter();
            target.ParameterName = "@target";
            target.Value = targetPackageKey;
            command.Parameters.Add(target);
            using IDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                List<object?> fields = [];
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    object? value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                    if (value is not (null or string or long or double))
                    {
                        throw new InvalidOperationException($"Unsupported persisted comparison operand {manifest.PersistedFields[index]}.");
                    }
                    fields.Add(value);
                }
                stored.Add([.. fields]);
            }
        }
        List<DbElementComparison> comparisons = DbElementComparison.SelectList(db,
            SourceFhirPackageKey: sourcePackageKey, TargetFhirPackageKey: targetPackageKey,
            orderByProperties: [nameof(DbElementComparison.Key)]);
        (long, long, long) after = Observe(db);
        evidence.RequireActive();
        if (before != after || stored.Count != comparisons.Count)
        {
            throw new InvalidOperationException("Directional comparison operands changed during a read pass.");
        }
        return new(evidence, sourcePackageKey, targetPackageKey, [.. stored], comparisons);
    }

    internal ProfileUseFacts CreateFacts(XVerStructureCatalog catalog, XVerProfileContext context)
    {
        if (HasCorrespondence == (context.Interpretation == XVerProfileInterpretation.Comparison))
        {
            throw new InvalidOperationException("Comparison-free and correspondence-aware fact operands are distinct.");
        }
        return new(catalog, SourcePackageKey, TargetPackageKey, HasCorrespondence ? _comparisons : null);
    }

    internal bool ExactlyEquals(XVerDirectionalOperands other) =>
        ReferenceEquals(Evidence, other.Evidence) &&
        SourcePackageKey == other.SourcePackageKey && TargetPackageKey == other.TargetPackageKey &&
        HasCorrespondence == other.HasCorrespondence &&
        _storedRows.Length == other._storedRows.Length &&
        _storedRows.Zip(other._storedRows).All(pair => pair.First.SequenceEqual(pair.Second));

    private static (long, long, long) Observe(IDbConnection db)
    {
        long Read(string sql)
        {
            using IDbCommand command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return (Read("PRAGMA data_version"), Read("SELECT total_changes()"), Read("PRAGMA schema_version"));
    }
}

internal sealed class XVerDirectionalProduct
{
    internal XVerDirectionalProduct(XVerDirectionalOperands operands, XVerProfileContext context,
        ImmutableArray<ProfileRepresentationProof> decisions)
    {
        Operands = operands;
        Context = context;
        Decisions = decisions;
    }

    internal XVerDirectionalOperands Operands { get; }
    internal XVerProfileContext Context { get; }
    internal ImmutableArray<ProfileRepresentationProof> Decisions { get; }
}

internal sealed class XVerProfileWorkPacket : IDisposable
{
    private ProfileUseFacts? _facts;
    private XVerDirectionalProduct? _product;
    private bool _disposed;

    internal XVerProfileWorkPacket(XVerStructureCatalog catalog, XVerDirectionalOperands operands, XVerProfileContext context)
    {
        Operands = operands;
        Context = context;
        _facts = operands.CreateFacts(catalog, context);
    }

    internal XVerDirectionalOperands Operands { get; }
    internal XVerProfileContext Context { get; }
    internal ProfileUseFacts Facts
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Operands.Evidence.RequireActive();
            return _facts ?? throw new InvalidOperationException("The packet's working facts have been released.");
        }
    }

    internal XVerDirectionalProduct Classify()
    {
        if (!Context.Equals(XVerProfileContext.Classification))
        {
            throw new InvalidOperationException("Only a final-classification packet produces directional classification products.");
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        Operands.Evidence.RequireActive();
        return _product ??= new(Operands, Context, [.. Facts.ClassifyRequiredUses()]);
    }

    internal void ReleaseWorkingFacts()
    {
        _facts?.Dispose();
        _facts = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _product = null;
        ReleaseWorkingFacts();
    }
}

internal sealed class XVerPreparedOperation : IDisposable
{
    private readonly XVerOperandEvidence _evidence;
    private readonly IDbConnection _connection;
    private readonly Dictionary<(int Source, int Target, XVerProfileContext Context), XVerProfileWorkPacket> _packets = [];
    private readonly Dictionary<(int Source, int Target), XVerDirectionalOperands> _directionalOperands = [];
    private XVerStructureCatalog? _catalog;
    private string? _inventoryRevision;
    private bool _disposed;

    private XVerPreparedOperation(XVerOperandEvidence evidence, IDbConnection connection)
    {
        _evidence = evidence;
        _connection = connection;
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
        XVerPreparedOperation operation = new(XVerOperandEvidence.Capture(connection, options), connection);
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

    internal void RequireConnection(IDbConnection connection)
    {
        RequireActive();
        if (!ReferenceEquals(connection, _connection))
        {
            Dispose();
            throw new InvalidOperationException("Prepared work belongs to its exact connection and invocation.");
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

    internal XVerDirectionalOperands CaptureDirection(int sourcePackageKey, int targetPackageKey)
    {
        try
        {
            RequireActive();
            return XVerDirectionalOperands.Capture(_connection, _evidence, sourcePackageKey, targetPackageKey);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal XVerProfileWorkPacket GetPacket(XVerDirectionalOperands operands, XVerProfileContext context)
    {
        RequireActive();
        if (!ReferenceEquals(operands.Evidence, _evidence))
        {
            throw new InvalidOperationException("A profile packet cannot borrow another operation's operands.");
        }
        if (operands.HasCorrespondence == (context.Interpretation == XVerProfileInterpretation.Comparison))
        {
            throw new InvalidOperationException("A profile packet requires the exact comparison interpretation operands.");
        }
        (int, int) direction = (operands.SourcePackageKey, operands.TargetPackageKey);
        if (operands.HasCorrespondence)
        {
            if (_directionalOperands.TryGetValue(direction, out XVerDirectionalOperands? current) && !current.ExactlyEquals(operands))
            {
                Dispose();
                throw new InvalidOperationException("Directional comparison operands changed without a scheduled replacement.");
            }
            _directionalOperands[direction] = operands;
        }
        (int, int, XVerProfileContext) key = (operands.SourcePackageKey, operands.TargetPackageKey, context);
        if (_packets.TryGetValue(key, out XVerProfileWorkPacket? packet))
        {
            return packet;
        }
        packet = new(Catalog, operands, context);
        _packets.Add(key, packet);
        return packet;
    }

    internal XVerProfileWorkPacket GetComparisonPacket(int sourcePackageKey, int targetPackageKey) =>
        GetPacket(XVerDirectionalOperands.WithoutCorrespondence(_evidence, sourcePackageKey, targetPackageKey), XVerProfileContext.Comparison);

    internal void ComparisonFlushed(int sourcePackageKey, int targetPackageKey)
    {
        Audit();
        ReplaceScheduledDirection(CaptureDirection(sourcePackageKey, targetPackageKey),
            new(XVerOperandFamily.StructureComparisons, XVerOutputTransitionKind.PairFlush, sourcePackageKey, targetPackageKey));
    }

    internal ImmutableArray<ProfileRepresentationProof> ClassifyComparisonPair(int sourcePackageKey, int targetPackageKey)
    {
        Audit();
        XVerDirectionalProduct forward = GetPacket(CaptureDirection(sourcePackageKey, targetPackageKey),
            XVerProfileContext.Classification).Classify();
        XVerDirectionalProduct reverse = GetPacket(CaptureDirection(targetPackageKey, sourcePackageKey),
            XVerProfileContext.Classification).Classify();
        ImmutableArray<ProfileRepresentationProof> decisions = ComposePair(forward, reverse);
        Audit();
        return decisions;
    }

    // This replaces semantic work only. It does not rebase operation evidence or
    // authorize writes; the comparison guard owns the audited flush transition.
    internal void ReplaceScheduledDirection(XVerDirectionalOperands operands, XVerOutputTransition transition)
    {
        RequireActive();
        transition.RequireOutputFamily();
        if (!ReferenceEquals(operands.Evidence, _evidence) || !operands.HasCorrespondence ||
            transition.Family != XVerOperandFamily.StructureComparisons || transition.Kind != XVerOutputTransitionKind.PairFlush ||
            transition.SourcePackageKey != operands.SourcePackageKey || transition.TargetPackageKey != operands.TargetPackageKey)
        {
            throw new InvalidOperationException("Directional replacement requires its exact scheduled comparison flush.");
        }
        (int, int) direction = (operands.SourcePackageKey, operands.TargetPackageKey);
        if (_directionalOperands.TryGetValue(direction, out XVerDirectionalOperands? current) && current.ExactlyEquals(operands))
        {
            return;
        }
        foreach ((int Source, int Target, XVerProfileContext Context) key in _packets.Keys
            .Where(key => key.Source == direction.Item1 && key.Target == direction.Item2 &&
                key.Context.Interpretation != XVerProfileInterpretation.Comparison).ToArray())
        {
            _packets[key].Dispose();
            _packets.Remove(key);
        }
        _directionalOperands[direction] = operands;
    }

    internal ImmutableArray<ProfileRepresentationProof> ComposePair(XVerDirectionalProduct forward, XVerDirectionalProduct reverse)
    {
        RequireActive();
        RequireCurrent(forward);
        RequireCurrent(reverse);
        if (forward.Operands.SourcePackageKey != reverse.Operands.TargetPackageKey ||
            forward.Operands.TargetPackageKey != reverse.Operands.SourcePackageKey)
        {
            throw new InvalidOperationException("Bidirectional safety requires the exact reverse direction.");
        }
        return forward.Decisions;
    }

    internal void ReleasePairWorkingFacts(int sourcePackageKey, int targetPackageKey)
    {
        foreach (XVerProfileWorkPacket packet in _packets.Values.Where(packet =>
            packet.Operands.SourcePackageKey == sourcePackageKey && packet.Operands.TargetPackageKey == targetPackageKey ||
            packet.Operands.SourcePackageKey == targetPackageKey && packet.Operands.TargetPackageKey == sourcePackageKey))
        {
            packet.ReleaseWorkingFacts();
        }
        _catalog?.ReleasePayloads();
    }

    private void RequireCurrent(XVerDirectionalProduct product)
    {
        if (!ReferenceEquals(product.Operands.Evidence, _evidence) ||
            !product.Context.Equals(XVerProfileContext.Classification) ||
            !_packets.TryGetValue((product.Operands.SourcePackageKey, product.Operands.TargetPackageKey, product.Context),
                out XVerProfileWorkPacket? packet) || !ReferenceEquals(packet.Classify(), product))
        {
            throw new InvalidOperationException("A directional product is not current for this operation, direction and interpretation context.");
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
        foreach (XVerProfileWorkPacket packet in _packets.Values)
        {
            packet.Dispose();
        }
        _packets.Clear();
        _directionalOperands.Clear();
        _catalog?.Dispose();
        _catalog = null;
        _inventoryRevision = null;
        _evidence.Dispose();
    }
}
