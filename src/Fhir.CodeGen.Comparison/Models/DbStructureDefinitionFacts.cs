// <copyright file="DbStructureDefinitionFacts.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
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

    internal static StructureDefinition ReadDefinition(DbStructureDefinitionFacts facts)
    {
        try
        {
            return new FhirJsonParser().Parse<StructureDefinition>(facts.StructureDefinitionJson);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Malformed normalized StructureDefinition payload in facts {facts.Key}, package {facts.FhirPackageKey}, owner {facts.StructureKey}.",
                exception);
        }
    }

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
    internal const int PolicyVersion = 2;

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
        InvalidateCompletion(db);
        ClearMapBindings(state);
        ValidateInventory(db, state);
        state.CompletedRevision = startedRevision;
        DbDefinitionInventoryState.Update(db, state);
    }

    private static void ValidateInventory(IDbConnection db, DbDefinitionInventoryState state)
    {
        DefinitionInventoryPackageCounts[] expected = ReadExpectedPackageCounts(state);
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(db);
        ValidatePackageSet(packages, expected);
        List<DbStructureDefinition> structures = DbStructureDefinition.SelectList(db);
        List<DbStructureDefinitionFacts> facts = DbStructureDefinitionFacts.SelectList(db);
        List<DbElement> elements = DbElement.SelectList(db);
        List<DbElementType> types = DbElementType.SelectList(db);
        List<DbElementAdditionalBinding> bindings = DbElementAdditionalBinding.SelectList(db);
        ValidateInventoryRows(expected, packages, structures, facts, elements, types, bindings);
        XVerStructureCatalog catalog = new(db);
        catalog.ValidateStoredLinks(types);
    }

    internal static void ValidateInventory(
        DbDefinitionInventoryState state,
        IReadOnlyList<DbFhirPackage> packages,
        IReadOnlyList<DbStructureDefinition> structures,
        IReadOnlyList<DbStructureDefinitionFacts> facts,
        IReadOnlyList<DbElement> elements,
        IReadOnlyList<DbElementType> types,
        IReadOnlyList<DbElementAdditionalBinding> bindings,
        XVerStructureCatalog catalog)
    {
        DefinitionInventoryPackageCounts[] expected = ReadExpectedPackageCounts(state);
        ValidatePackageSet(packages, expected);
        ValidateInventoryRows(expected, packages, structures, facts, elements, types, bindings);
        catalog.ValidateStoredLinks(types);
    }

    private static DefinitionInventoryPackageCounts[] ReadExpectedPackageCounts(DbDefinitionInventoryState state) =>
        JsonSerializer.Deserialize<DefinitionInventoryPackageCounts[]>(state.ExpectedPackageCountsJson)
            ?? throw new InvalidOperationException("Definition inventory expected source counts are missing.");

    private static void ValidatePackageSet(
        IReadOnlyList<DbFhirPackage> packages, DefinitionInventoryPackageCounts[] expected)
    {
        if (!packages.Select(row => row.Key).Order().SequenceEqual(expected.Select(row => row.FhirPackageKey).Order()) ||
            expected.Select(row => row.FhirPackageKey).Distinct().Count() != expected.Length)
        {
            throw new InvalidOperationException("Definition inventory package set does not match the expected source package set.");
        }
    }

    private static void ValidateInventoryRows(
        DefinitionInventoryPackageCounts[] expected,
        IReadOnlyList<DbFhirPackage> packages,
        IReadOnlyList<DbStructureDefinition> structures,
        IReadOnlyList<DbStructureDefinitionFacts> facts,
        IReadOnlyList<DbElement> elements,
        IReadOnlyList<DbElementType> types,
        IReadOnlyList<DbElementAdditionalBinding> bindings)
    {
        Dictionary<int, DbStructureDefinition> owners = structures.ToDictionary(row => row.Key);
        Dictionary<int, DbElement> elementOwners = elements.ToDictionary(row => row.Key);
        ILookup<int, DbElement> elementsByOwner = elements.ToLookup(row => row.StructureKey);
        ILookup<int?, DbElement> children = elements.ToLookup(row => row.ParentElementKey);
        ILookup<int, DbElementType> typesByElement = types.ToLookup(row => row.ElementKey);
        ILookup<int, DbElementAdditionalBinding> bindingsByElement = bindings.ToLookup(row => row.ElementKey);
        HashSet<int> packageKeys = [.. packages.Select(row => row.Key)];
        if (structures.Any(row => !packageKeys.Contains(row.FhirPackageKey)) ||
            facts.Count != structures.Count || facts.GroupBy(row => row.StructureKey).Any(group => group.Count() != 1) ||
            facts.Any(row => !owners.TryGetValue(row.StructureKey, out DbStructureDefinition? owner) || owner.FhirPackageKey != row.FhirPackageKey))
        {
            throw new InvalidOperationException("Definition inventory requires exactly one package-consistent facts row per structure.");
        }

        foreach (DbElement element in elements)
        {
            int separator = element.Id.LastIndexOf('.');
            if (!owners.TryGetValue(element.StructureKey, out DbStructureDefinition? owner) || owner.FhirPackageKey != element.FhirPackageKey ||
                (element.ParentElementKey is int parentKey && (!elementOwners.TryGetValue(parentKey, out DbElement? parent) ||
                    parent.StructureKey != element.StructureKey || parent.FhirPackageKey != element.FhirPackageKey ||
                    separator < 0 || parent.Id != element.Id[..separator])) ||
                element.ChildElementCount != children[element.Key].Count())
            {
                throw new InvalidOperationException($"Definition inventory element {element.Key} '{element.Id}' has an invalid owner, parent, or child count.");
            }
        }

        foreach ((int packageKey, int structureKey, int elementKey) in types.Select(row => (row.FhirPackageKey, row.StructureKey, row.ElementKey))
            .Concat(bindings.Select(row => (row.FhirPackageKey, row.StructureKey, row.ElementKey))))
        {
            if (!elementOwners.TryGetValue(elementKey, out DbElement? element) ||
                element.FhirPackageKey != packageKey || element.StructureKey != structureKey)
            {
                throw new InvalidOperationException($"Definition inventory projection has invalid owner {packageKey}/{structureKey}, element {elementKey}.");
            }
        }

        foreach (DbStructureDefinitionFacts row in facts)
        {
            DbStructureDefinition owner = owners[row.StructureKey];
            StructureDefinition definition = StructureDefinitionFactsStore.ReadDefinition(row);
            DefinitionSnapshotAvailability availability = definition.Snapshot is null ? DefinitionSnapshotAvailability.Missing :
                definition.Snapshot.Element.Count == 0 ? DefinitionSnapshotAvailability.Empty : DefinitionSnapshotAvailability.Available;
            if (definition.Url != owner.UnversionedUrl || definition.Version != owner.Version ||
                definition.Type != row.Type || definition.BaseDefinition != row.BaseDefinition ||
                definition.Kind?.GetLiteral() != row.Kind || definition.Derivation?.GetLiteral() != row.Derivation ||
                availability != row.SnapshotAvailability ||
                owner.SnapshotCount != (definition.Snapshot?.Element.Count ?? 0) ||
                owner.DifferentialCount != (definition.Differential?.Element.Count ?? 0))
            {
                throw new InvalidOperationException($"Definition inventory facts contradict owner {owner.Key} '{owner.VersionedUrl}'.");
            }

            List<Hl7.Fhir.Model.ElementDefinition> projection = owner.ArtifactClass != FhirArtifactClassEnum.Profile && owner.Message == "Manually excluded"
                ? [] : availability == DefinitionSnapshotAvailability.Available ? definition.Snapshot!.Element : definition.Differential?.Element ?? [];
            DbElement[] ownedElements = [.. elementsByOwner[owner.Key]];
            if (!projection.Select(element => (element.ElementId, element.Path)).Order()
                .SequenceEqual(ownedElements.Select(element => (element.Id, element.Path)).Order()))
            {
                throw new InvalidOperationException($"Definition inventory available element projection contradicts owner {owner.Key} '{owner.VersionedUrl}'.");
            }

            foreach (Hl7.Fhir.Model.ElementDefinition source in projection)
            {
                DbElement element = ownedElements.Single(element => element.Id == source.ElementId);
                int expectedTypes = source.Type.Sum(type => Math.Max(1, type.ProfileElement.Count) * Math.Max(1, type.TargetProfileElement.Count));
                if (source.Type.Count == 0 && availability == DefinitionSnapshotAvailability.Available &&
                    element.ResourceFieldOrder == 0 && !string.IsNullOrEmpty(definition.Type))
                {
                    expectedTypes++;
                }

                if (typesByElement[element.Key].Count() != expectedTypes ||
                    bindingsByElement[element.Key].Count() != (source.Binding?.Additional.Count ?? 0) ||
                    element.AdditionalBindingCount != (source.Binding?.Additional.Count ?? 0))
                {
                    throw new InvalidOperationException($"Definition inventory type/binding projection contradicts owner {owner.Key} '{owner.VersionedUrl}' element '{source.ElementId}'.");
                }
            }
        }

        foreach (DefinitionInventoryPackageCounts counts in expected)
        {
            int packageKey = counts.FhirPackageKey;
            DefinitionInventoryPackageCounts actual = new(packageKey,
                structures.Count(row => row.FhirPackageKey == packageKey && row.ArtifactClass == FhirArtifactClassEnum.PrimitiveType),
                structures.Count(row => row.FhirPackageKey == packageKey && row.ArtifactClass == FhirArtifactClassEnum.ComplexType),
                structures.Count(row => row.FhirPackageKey == packageKey && row.ArtifactClass == FhirArtifactClassEnum.Resource),
                structures.Count(row => row.FhirPackageKey == packageKey && row.ArtifactClass == FhirArtifactClassEnum.Profile),
                elements.Count(row => row.FhirPackageKey == packageKey), types.Count(row => row.FhirPackageKey == packageKey),
                bindings.Count(row => row.FhirPackageKey == packageKey));
            if (actual != counts)
            {
                throw new InvalidOperationException($"Definition inventory package {packageKey} does not match independent expected source counts. Expected {counts}; stored {actual}.");
            }
        }
    }

    internal sealed class ValidatedInventory
    {
        private readonly XVerOperandEvidence _evidence;
        private readonly string _completedMapBindings;

        internal ValidatedInventory(XVerOperandEvidence evidence, XVerStructureCatalog catalog)
        {
            DbDefinitionInventoryState state = RequireCapturedCompletedState(evidence);
            ValidateInventory(state, evidence.Rows<DbFhirPackage>(DbFhirPackage.DefaultTableName),
                evidence.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName),
                evidence.Rows<DbStructureDefinitionFacts>(DbStructureDefinitionFacts.DefaultTableName),
                evidence.Rows<DbElement>(DbElement.DefaultTableName), evidence.Rows<DbElementType>(DbElementType.DefaultTableName),
                evidence.Rows<DbElementAdditionalBinding>(DbElementAdditionalBinding.DefaultTableName), catalog);
            catalog.ReleasePayloads();
            _evidence = evidence;
            Revision = state.CompletedRevision!;
            _completedMapBindings = state.CompletedMapInventoryBindingsJson;
        }

        internal string Revision { get; }

        internal void RequireMapBinding(string snapshotId)
        {
            _evidence.RequireActive();
            RequireBoundSnapshot(_completedMapBindings, snapshotId, Revision);
        }
    }

    internal static ValidatedInventory RequireReady(XVerOperandEvidence evidence, XVerStructureCatalog catalog) =>
        new(evidence, catalog);

    private static DbDefinitionInventoryState RequireCapturedCompletedState(XVerOperandEvidence evidence)
    {
        List<DbDefinitionInventoryState> states = evidence.Rows<DbDefinitionInventoryState>(DbDefinitionInventoryState.DefaultTableName);
        if (states.Count != 1 || states[0].Key != 1 ||
            states[0].FormatVersion != FormatVersion || states[0].PolicyVersion != PolicyVersion ||
            string.IsNullOrEmpty(states[0].CompletedRevision) || states[0].CompletedRevision != states[0].StartedRevision)
        {
            throw new InvalidOperationException("Definition catalog is incomplete, incompatible, or stale. Reload the database, load maps, compare, generate outcomes, then export.");
        }

        return states[0];
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
            throw new InvalidOperationException($"Definition inventory '{revision}' is missing, incompatible, or stale. Reload the database, load maps, compare, generate outcomes, then export.");
        }

        return state;
    }

    internal static string RequireReady(IDbConnection db)
    {
        const string recovery = "Reload the database, load maps, compare, generate outcomes, then export.";
        if (!TableExists(db, DbDefinitionInventoryState.DefaultTableName) ||
            !TableExists(db, DbStructureDefinitionFacts.DefaultTableName))
        {
            throw new InvalidOperationException($"Definition catalog is missing or incompatible. {recovery}");
        }

        DbDefinitionInventoryState? state = ReadState(db);
        if (state is null || state.FormatVersion != FormatVersion || state.PolicyVersion != PolicyVersion ||
            string.IsNullOrEmpty(state.CompletedRevision) || state.CompletedRevision != state.StartedRevision)
        {
            throw new InvalidOperationException($"Definition catalog is incomplete, incompatible, or stale. {recovery}");
        }

        ValidateInventory(db, state);
        return state.CompletedRevision;
    }

    internal static void RequireMapBinding(IDbConnection db, string snapshotId)
    {
        string revision = RequireReady(db);
        DbDefinitionInventoryState state = RequireCompletedState(db, revision);
        RequireBoundSnapshot(state.CompletedMapInventoryBindingsJson, snapshotId, revision);
    }

    internal static void RequireMapBinding(ValidatedInventory validatedState, string snapshotId) =>
        validatedState.RequireMapBinding(snapshotId);

    private static void RequireBoundSnapshot(string completedMapBindings, string snapshotId, string revision)
    {
        if (!ReadBindings(completedMapBindings).TryGetValue(snapshotId, out string? bound) ||
            bound != revision)
        {
            throw new InvalidOperationException("Map inputs are not bound to the completed definition catalog. Reload the database if incompatible, load maps, compare, generate outcomes, then export.");
        }
    }

    internal static string RequirePendingMapBinding(IDbConnection db, string snapshotId)
    {
        string revision = RequireReady(db);
        DbDefinitionInventoryState state = RequireCompletedState(db, revision);
        if (!ReadBindings(state.PendingMapInventoryBindingsJson).TryGetValue(snapshotId, out string? bound) ||
            bound != revision)
        {
            throw new InvalidOperationException("Definition inventory changed during map loading. Reload the database, load maps, compare, generate outcomes, then export.");
        }

        return revision;
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
