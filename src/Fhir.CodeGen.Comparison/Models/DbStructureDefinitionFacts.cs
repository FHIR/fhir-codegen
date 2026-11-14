// <copyright file="DbStructureDefinitionFacts.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Text.Json;
using Fhir.CodeGen.SQLiteGenerator;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using StructureDefinition = Hl7.Fhir.Model.StructureDefinition;

namespace Fhir.CodeGen.Comparison.Models;

internal enum DefinitionSnapshotAvailability
{
    Missing,
    Empty,
    Available,
}

[CgSQLiteTable(tableName: "StructureDefinitionFacts")]
[CgSQLiteIndex(nameof(FhirPackageKey), nameof(StructureKey))]
[CgSQLiteIndex(nameof(StructureKey))]
internal partial class DbStructureDefinitionFacts : DbRecordBase
{
    [CgSQLiteForeignKey(ReferenceTable = "FhirPackages", ReferenceColumn = "Key")]
    public required int FhirPackageKey { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "Structures", ReferenceColumn = "Key")]
    public required int StructureKey { get; set; }
    public string? Type { get; set; }
    public string? Kind { get; set; }
    public string? Derivation { get; set; }
    public string? BaseDefinition { get; set; }
    public required DefinitionSnapshotAvailability SnapshotAvailability { get; set; }
    public required string StructureDefinitionJson { get; set; }
}

[CgSQLiteTable(tableName: "DefinitionInventoryState")]
internal partial class DbDefinitionInventoryState : DbRecordBase
{
    public required int FormatVersion { get; set; }
    public required int PolicyVersion { get; set; }
    public required string StartedRevision { get; set; }
    public string? CompletedRevision { get; set; }
    public required string ExpectedPackageCountsJson { get; set; }
    public string PendingMapInventoryBindingsJson { get; set; } = "{}";
    public string CompletedMapInventoryBindingsJson { get; set; } = "{}";
}

internal enum ProfileMapOwnerResolution
{
    Unnormalized,
    Unresolved,
    Passive,
    Resolved,
    Ambiguous,
}

[CgSQLiteTable(tableName: "ProfileMapOwnerFacts")]
[CgSQLiteIndex(nameof(AnnotationKey), nameof(ElementMappingKey))]
[CgSQLiteIndex(nameof(InputSnapshotId))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(TargetFhirPackageKey), nameof(TargetStructureKey))]
internal partial class DbProfileMapOwnerFacts : DbRecordBase
{
    [CgSQLiteForeignKey(ReferenceTable = "UnsafeCodeElementAnnotations", ReferenceColumn = "Key")]
    public required int AnnotationKey { get; set; }
    public required string InputSnapshotId { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "FhirPackages", ReferenceColumn = "Key")]
    public required int SourceFhirPackageKey { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "FhirPackages", ReferenceColumn = "Key")]
    public required int TargetFhirPackageKey { get; set; }
    public string? GroupSourceCanonical { get; set; }
    public string? GroupSourceVersion { get; set; }
    public string? GroupTargetCanonical { get; set; }
    public string? GroupTargetVersion { get; set; }
    public string? SourceOwnerContext { get; set; }
    public string? TargetOwnerContext { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "Structures", ReferenceColumn = "Key")]
    public int? SourceStructureKey { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "Structures", ReferenceColumn = "Key")]
    public int? TargetStructureKey { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "ElementMappings", ReferenceColumn = "Key")]
    public int? ElementMappingKey { get; set; }
    public required ProfileMapOwnerResolution ResolutionState { get; set; }
    public required string SourceElementId { get; set; }
    public required string TargetElementId { get; set; }
    [CgSQLiteForeignKey(ReferenceTable = "MappingSourceFiles", ReferenceColumn = "Key")]
    public int? MappingSourceFileKey { get; set; }
    public required string SourceRelativeFilename { get; set; }
    public string? ConceptMapId { get; set; }
    public string? ConceptMapUrl { get; set; }
    public required int GroupIndex { get; set; }
    public required int SourceIndex { get; set; }
    public required int TargetIndex { get; set; }
}

internal sealed record DefinitionInventoryPackageCounts(
    int FhirPackageKey,
    int PrimitiveTypeCount,
    int ComplexTypeCount,
    int ResourceCount,
    int ProfileCount,
    int ElementCount,
    int ElementTypeCount,
    int AdditionalBindingCount);

internal static class StructureDefinitionFactsStore
{
    internal static DbStructureDefinitionFacts CreateFacts(
        int key,
        int fhirPackageKey,
        int structureKey,
        StructureDefinition definition) => new()
        {
            Key = key,
            FhirPackageKey = fhirPackageKey,
            StructureKey = structureKey,
            Type = definition.Type,
            Kind = definition.Kind?.GetLiteral(),
            Derivation = definition.Derivation?.GetLiteral(),
            BaseDefinition = definition.BaseDefinition,
            SnapshotAvailability = definition.Snapshot is null ? DefinitionSnapshotAvailability.Missing
                : definition.Snapshot.Element.Count == 0 ? DefinitionSnapshotAvailability.Empty : DefinitionSnapshotAvailability.Available,
            StructureDefinitionJson = new FhirJsonSerializer().SerializeToString(definition),
        };

    internal static StructureDefinition ReadDefinition(DbStructureDefinitionFacts facts) =>
        new FhirJsonParser().Parse<StructureDefinition>(facts.StructureDefinitionJson);

    internal static void LoadContentIndices(IDbConnection db)
    {
        if (DefinitionInventoryStore.TableExists(db, DbStructureDefinitionFacts.DefaultTableName))
        {
            DbStructureDefinitionFacts._indexValue = DbStructureDefinitionFacts.SelectMaxKey(db) ?? 0;
        }

        if (DefinitionInventoryStore.TableExists(db, DbDefinitionInventoryState.DefaultTableName))
        {
            DbDefinitionInventoryState._indexValue = DbDefinitionInventoryState.SelectMaxKey(db) ?? 0;
        }
    }

    internal static void LoadMappingIndices(IDbConnection db)
    {
        if (DefinitionInventoryStore.TableExists(db, DbProfileMapOwnerFacts.DefaultTableName))
        {
            DbProfileMapOwnerFacts._indexValue = DbProfileMapOwnerFacts.SelectMaxKey(db) ?? 0;
        }
    }
}

internal static class DefinitionInventoryStore
{
    internal const int FormatVersion = 1;
    internal const int PolicyVersion = 1;

    internal static DbDefinitionInventoryState? ReadState(IDbConnection db)
    {
        List<DbDefinitionInventoryState> rows = DbDefinitionInventoryState.SelectList(db);
        if (rows.Count == 0)
        {
            return null;
        }

        if (rows.Count != 1 || rows[0].Key != 1)
        {
            throw new InvalidOperationException("Definition inventory state must have exactly one row with Key=1.");
        }

        return rows[0];
    }

    internal static string BeginInventory(IDbConnection db, IReadOnlyList<DefinitionInventoryPackageCounts> expectedCounts)
    {
        DefinitionInventoryPackageCounts[] counts = [.. expectedCounts.OrderBy(count => count.FhirPackageKey)];
        if (counts.Any(count => count.FhirPackageKey <= 0 || count.PrimitiveTypeCount < 0 || count.ComplexTypeCount < 0 ||
            count.ResourceCount < 0 || count.ProfileCount < 0 || count.ElementCount < 0 ||
            count.ElementTypeCount < 0 || count.AdditionalBindingCount < 0) ||
            counts.Select(count => count.FhirPackageKey).Distinct().Count() != counts.Length)
        {
            throw new ArgumentException("Expected definition inventory counts must be nonnegative and package-qualified.", nameof(expectedCounts));
        }

        DbDefinitionInventoryState? state = ReadState(db);
        InvalidateCompletion(db);
        UnsafeCodeInputStore.InvalidateInputs(db);
        string revision = Guid.NewGuid().ToString("N");
        DbDefinitionInventoryState replacement = new()
        {
            Key = 1,
            FormatVersion = FormatVersion,
            PolicyVersion = PolicyVersion,
            StartedRevision = revision,
            ExpectedPackageCountsJson = JsonSerializer.Serialize(counts),
        };
        if (state is null)
        {
            DbDefinitionInventoryState.Insert(db, replacement, insertPrimaryKey: true);
        }
        else
        {
            DbDefinitionInventoryState.Update(db, replacement);
        }

        return revision;
    }

    internal static void CompleteInventory(IDbConnection db, string startedRevision)
    {
        DbDefinitionInventoryState state = RequireStartedState(db, startedRevision);
        state.CompletedRevision = startedRevision;
        DbDefinitionInventoryState.Update(db, state);
    }

    internal static void BeginMapInputs(IDbConnection db, string inputSnapshotId, string inventoryRevision)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputSnapshotId);
        DbDefinitionInventoryState state = RequireCompletedState(db, inventoryRevision);
        Dictionary<string, string> pending = ReadBindings(state.PendingMapInventoryBindingsJson);
        Dictionary<string, string> completed = ReadBindings(state.CompletedMapInventoryBindingsJson);
        pending[inputSnapshotId] = inventoryRevision;
        completed.Remove(inputSnapshotId);
        state.PendingMapInventoryBindingsJson = JsonSerializer.Serialize(pending);
        state.CompletedMapInventoryBindingsJson = JsonSerializer.Serialize(completed);
        DbDefinitionInventoryState.Update(db, state);
    }

    internal static void CompleteMapInputs(IDbConnection db, string inputSnapshotId, string inventoryRevision)
    {
        DbDefinitionInventoryState state = RequireCompletedState(db, inventoryRevision);
        Dictionary<string, string> pending = ReadBindings(state.PendingMapInventoryBindingsJson);
        if (!pending.Remove(inputSnapshotId, out string? pendingRevision) || pendingRevision != inventoryRevision)
        {
            throw new InvalidOperationException($"Map input snapshot '{inputSnapshotId}' was not started against inventory '{inventoryRevision}'.");
        }

        Dictionary<string, string> completed = ReadBindings(state.CompletedMapInventoryBindingsJson);
        completed[inputSnapshotId] = inventoryRevision;
        state.PendingMapInventoryBindingsJson = JsonSerializer.Serialize(pending);
        state.CompletedMapInventoryBindingsJson = JsonSerializer.Serialize(completed);
        DbDefinitionInventoryState.Update(db, state);
    }

    internal static void InvalidateCompletion(IDbConnection db)
    {
        if (!TableExists(db, DbDefinitionInventoryState.DefaultTableName))
        {
            return;
        }

        DbDefinitionInventoryState? state = ReadState(db);
        if (state is not null)
        {
            state.CompletedRevision = null;
            ClearMapBindings(state);
            DbDefinitionInventoryState.Update(db, state);
        }
    }

    internal static void InvalidateMapInputs(IDbConnection db)
    {
        if (!TableExists(db, DbDefinitionInventoryState.DefaultTableName))
        {
            return;
        }

        DbDefinitionInventoryState? state = ReadState(db);
        if (state is not null)
        {
            ClearMapBindings(state);
            DbDefinitionInventoryState.Update(db, state);
        }
    }

    internal static bool TableExists(IDbConnection db, string tableName)
    {
        using IDbCommand command = db.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName";
        IDbDataParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() is not null;
    }

    private static Dictionary<string, string> ReadBindings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException("Definition inventory map bindings are missing.");

    private static void ClearMapBindings(DbDefinitionInventoryState state)
    {
        state.PendingMapInventoryBindingsJson = "{}";
        state.CompletedMapInventoryBindingsJson = "{}";
    }

    private static DbDefinitionInventoryState RequireStartedState(IDbConnection db, string revision)
    {
        ArgumentException.ThrowIfNullOrEmpty(revision);
        DbDefinitionInventoryState state = ReadState(db)
            ?? throw new InvalidOperationException("Definition inventory import was not started.");
        if (state.FormatVersion != FormatVersion || state.PolicyVersion != PolicyVersion || state.StartedRevision != revision)
        {
            throw new InvalidOperationException($"Definition inventory '{revision}' is missing, incompatible, or stale.");
        }

        return state;
    }

    private static DbDefinitionInventoryState RequireCompletedState(IDbConnection db, string revision)
    {
        DbDefinitionInventoryState state = RequireStartedState(db, revision);
        if (state.CompletedRevision != revision)
        {
            throw new InvalidOperationException($"Definition inventory '{revision}' is incomplete.");
        }

        return state;
    }
}
