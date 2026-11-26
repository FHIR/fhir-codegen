// <copyright file="DbUnsafeCodeClasses.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.SQLiteGenerator;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using Microsoft.Data.Sqlite;

namespace Fhir.CodeGen.Comparison.Models;

[CgSQLiteTable(tableName: "UnsafeCodeElementAnnotations")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceElementId), nameof(TargetElementId))]
public partial class DbUnsafeCodeElementAnnotation : DbRecordBase
{
    public required int SourceFhirPackageKey { get; set; }
    public required int TargetFhirPackageKey { get; set; }
    public required string SourceElementId { get; set; }
    public required string TargetElementId { get; set; }
    public required bool RequiresOmittedMandatoryCode { get; set; }
    public required string SourceRelativeFilename { get; set; }
    public string? ConceptMapId { get; set; }
    public string? ConceptMapUrl { get; set; }
    public required int GroupIndex { get; set; }
    public required int SourceIndex { get; set; }
    public required int TargetIndex { get; set; }
}

[CgSQLiteTable(tableName: "UnsafeCodeMapEvidence")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceSystem), nameof(SourceCode))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceValueSetUrl), nameof(TargetValueSetUrl))]
public partial class DbUnsafeCodeMapEvidence : DbRecordBase
{
    public required int SourceFhirPackageKey { get; set; }
    public required int TargetFhirPackageKey { get; set; }
    public string? SourceValueSetUrl { get; set; }
    public string? SourceValueSetVersion { get; set; }
    public string? TargetValueSetUrl { get; set; }
    public string? TargetValueSetVersion { get; set; }
    public string? SourceSystem { get; set; }
    public string? SourceSystemVersion { get; set; }
    public string? SourceCode { get; set; }
    public string? TargetSystem { get; set; }
    public string? TargetSystemVersion { get; set; }
    public string? TargetCode { get; set; }
    public Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship { get; set; }
    public required bool ExplicitNoMap { get; set; }
    public required string SourceRelativeFilename { get; set; }
    public string? ConceptMapId { get; set; }
    public string? ConceptMapUrl { get; set; }
    public required int GroupIndex { get; set; }
    public required int SourceIndex { get; set; }
    public int? TargetIndex { get; set; }
}

[CgSQLiteTable(tableName: "UnsafeCodeRules")]
[CgSQLiteIndex(nameof(System), nameof(Code), nameof(Version))]
public partial class DbUnsafeCodeRule : DbRecordBase
{
    public required string System { get; set; }
    public string? Version { get; set; }
    public required string Code { get; set; }
    public required bool NoMapOnly { get; set; }
    public required string ExcludedRelationshipLiteral { get; set; }
    public required string ExcludedVersionLiteral { get; set; }
    public required string SourceRelativeFilename { get; set; }
    public required int EntryIndex { get; set; }
}

[CgSQLiteTable(tableName: "UnsafeCodeTriggers")]
[CgSQLiteIndex(nameof(StructureOutcomeKey), nameof(SourceElementId))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceElementId))]
public partial class DbUnsafeCodeTrigger : DbRecordBase
{
    public required int StructureOutcomeKey { get; set; }
    public required int SourceFhirPackageKey { get; set; }
    public required int TargetFhirPackageKey { get; set; }
    public required string SourceElementId { get; set; }
    public required string TargetElementId { get; set; }
    public required string SourcePathJson { get; set; }
    public required string System { get; set; }
    public required string Code { get; set; }
    public string? SourceVersion { get; set; }
    public required bool IsWildcardVersion { get; set; }
    public required string VersionsJson { get; set; }
    public required string EvidenceKeysJson { get; set; }
    public required string RuleKeysJson { get; set; }
    public string AnnotationKeysJson { get; set; } = "[]";
}

[CgSQLiteTable(tableName: "UnsafeCodeInputState")]
public partial class DbUnsafeCodeInputState : DbRecordBase
{
    public required string SnapshotId { get; set; }
    public required int FormatVersion { get; set; }
    public required string SourceMode { get; set; }
    public string? SourceRoot { get; set; }
    public string? CompletedInputRevision { get; set; }
}

[CgSQLiteTable(tableName: "UnsafeCodePairState")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey))]
public partial class DbUnsafeCodePairState : DbRecordBase
{
    public required int SourceFhirPackageKey { get; set; }
    public required int TargetFhirPackageKey { get; set; }
    public string? InputRevision { get; set; }
    public string? ComparisonRevision { get; set; }
    public string? OutcomeInputRevision { get; set; }
    public string? OutcomeComparisonRevision { get; set; }
    public int? OutcomeFormatVersion { get; set; }
}

internal static class UnsafeCodeInputStore
{
    internal static IReadOnlyList<FhirPackageComparisonPair> RequireExportReady(IDbConnection db, IReadOnlyList<FhirPackageComparisonPair> schedule,
        XVerPreparedOperation operation)
    {
        operation.RequireConnection(db);
        operation.Audit();
        if (schedule.Count == 0)
        {
            return [];
        }
        operation.PrepareExport(schedule);
        return AuditExportReady(db, schedule, operation);
    }

    internal static IReadOnlyList<FhirPackageComparisonPair> AuditExportReady(IDbConnection db, IReadOnlyList<FhirPackageComparisonPair> schedule,
        XVerPreparedOperation operation)
    {
        try
        {
            operation.RequireConnection(db);
            operation.Audit();
            RequireExportState(db, schedule);
            List<FhirPackageComparisonPair> verified = [.. schedule.Where(scheduled =>
                ProfileUseFacts.RequirePersistedDecisions(db, scheduled,
                    operation.ExportDecisions(scheduled.SourcePackageKey, scheduled.TargetPackageKey), operation.Catalog))];
            operation.Audit();
            return verified;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    internal static IReadOnlyList<FhirPackageComparisonPair> RequireExportReady(IDbConnection db, IReadOnlyList<FhirPackageComparisonPair> schedule)
    {
        DbUnsafeCodeInputState? input = RequireExportState(db, schedule);
        if (input is null)
        {
            return [];
        }
        try
        {
            DefinitionInventoryStore.RequireMapBinding(db, input.SnapshotId);
            return [.. schedule.Where(scheduled => ProfileUseFacts.RequirePersistedDecisions(db, scheduled))];
        }
        catch (DbException error) when (error is SqliteException { SqliteErrorCode: 1 } &&
            (error.Message.Contains("no such table:", StringComparison.Ordinal) ||
                error.Message.Contains("no such column:", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Unsafe-code export schema could not be read or is incompatible. " +
                "Regenerate the database with the existing load stages, then compare, outcomes, and export.", error);
        }
    }

    // Preserve the original schema/stamp diagnostics before acquiring the wider
    // construction closure. This is not inventory validation or a readiness proof.
    internal static DbUnsafeCodeInputState? RequireExportState(IDbConnection db, IReadOnlyList<FhirPackageComparisonPair> schedule)
    {
        if (schedule.Count == 0)
        {
            return null;
        }

        const string recovery = "Regenerate the database with the existing load stages, then compare, outcomes, and export.";
        try
        {
            // Generated SELECTs check all columns as well as table existence. This
            // boundary is deliberately read-only, including for legacy databases.
            List<DbUnsafeCodeInputState> inputs = DbUnsafeCodeInputState.SelectList(db);
            List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(db);
            DbUnsafeCodeTrigger.SelectList(db);
            DbUnsafeCodeElementAnnotation.SelectList(db);
            DbUnsafeCodeMapEvidence.SelectList(db);
            DbUnsafeCodeRule.SelectList(db);
            if (inputs.Count != 1 || inputs[0].Key != 1 ||
                inputs[0].FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
                string.IsNullOrEmpty(inputs[0].CompletedInputRevision))
            {
                throw new InvalidOperationException($"Unsafe-code export inputs are missing, incomplete, or incompatible. {recovery}");
            }

            string revision = inputs[0].CompletedInputRevision!;
            foreach (FhirPackageComparisonPair scheduled in schedule)
            {
                DbUnsafeCodePairState[] matches = [.. pairs.Where(pair =>
                    pair.SourceFhirPackageKey == scheduled.SourcePackageKey && pair.TargetFhirPackageKey == scheduled.TargetPackageKey)];
                if (matches.Length != 1 || matches[0].InputRevision != revision ||
                    string.IsNullOrEmpty(matches[0].ComparisonRevision) ||
                    matches[0].OutcomeInputRevision != revision ||
                    matches[0].OutcomeComparisonRevision != matches[0].ComparisonRevision ||
                    matches[0].OutcomeFormatVersion != UnsafeCodeConstants.OutcomeFormatVersion)
                {
                    throw new InvalidOperationException(
                        $"Unsafe-code structure export {scheduled.SourcePackage.PackageId}|{scheduled.SourcePackage.PackageVersion}" +
                        $" -> {scheduled.TargetPackage.PackageId}|{scheduled.TargetPackage.PackageVersion} is missing, incomplete, incompatible, or stale. {recovery}");
                }
            }
            return inputs[0];
        }
        catch (DbException error) when (error is SqliteException { SqliteErrorCode: 1 } &&
            (error.Message.Contains("no such table:", StringComparison.Ordinal) ||
                error.Message.Contains("no such column:", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Unsafe-code export schema could not be read or is incompatible. {recovery}", error);
        }
    }

    internal static IReadOnlyList<UnsafeCodeConstraintPlan> ReadConstraintPlans(IDbConnection db, int sourceKey, int targetKey) =>
        ReadConstraintPlans(db, sourceKey, targetKey, null);

    internal static IReadOnlyList<UnsafeCodeConstraintPlan> ReadConstraintPlans(IDbConnection db, int sourceKey, int targetKey,
        XVerPreparedOperation? operation)
    {
        operation?.RequireConnection(db);
        operation?.Audit();
        List<DbUnsafeCodeTrigger> triggers = operation is null
            ? DbUnsafeCodeTrigger.SelectList(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey)
            : [.. operation.Evidence.Rows<DbUnsafeCodeTrigger>(DbUnsafeCodeTrigger.DefaultTableName)
                .Where(row => row.SourceFhirPackageKey == sourceKey && row.TargetFhirPackageKey == targetKey)];
        if (triggers.Count == 0)
        {
            return [];
        }

        Dictionary<int, string> evidence = (operation?.Evidence.Rows<DbUnsafeCodeMapEvidence>(DbUnsafeCodeMapEvidence.DefaultTableName) ??
            DbUnsafeCodeMapEvidence.SelectList(db)).Where(row => row.SourceFhirPackageKey == sourceKey && row.TargetFhirPackageKey == targetKey)
            .ToDictionary(row => row.Key, row => $"{row.SourceRelativeFilename} ({row.ConceptMapUrl ?? row.ConceptMapId}, group {row.GroupIndex}, source {row.SourceIndex}, target {row.TargetIndex})");
        HashSet<int> annotationKeys = [.. triggers.SelectMany(row => JsonSerializer.Deserialize<int[]>(row.AnnotationKeysJson)!)];
        if (annotationKeys.Count > 0)
        {
            Dictionary<int, DbFhirPackage> packages = (operation?.Evidence.Rows<DbFhirPackage>(DbFhirPackage.DefaultTableName) ??
                DbFhirPackage.SelectList(db)).ToDictionary(row => row.Key);
            new UnsafeCodeOutcomeGenerator(db).ValidateAnnotationProvenance(new(packages[sourceKey], packages[targetKey]), triggers, operation);
        }
        Dictionary<int, string> annotations = (operation?.Evidence.Rows<DbUnsafeCodeElementAnnotation>(DbUnsafeCodeElementAnnotation.DefaultTableName) ??
            DbUnsafeCodeElementAnnotation.SelectList(db)).Where(row => annotationKeys.Contains(row.Key))
            .ToDictionary(row => row.Key, row => $"{row.SourceRelativeFilename} ({row.ConceptMapUrl ?? row.ConceptMapId}, group {row.GroupIndex}, source {row.SourceIndex}, target {row.TargetIndex})");
        Dictionary<int, string> rules = (operation?.Evidence.Rows<DbUnsafeCodeRule>(DbUnsafeCodeRule.DefaultTableName) ?? DbUnsafeCodeRule.SelectList(db))
            .ToDictionary(row => row.Key, row => $"{row.SourceRelativeFilename} entry {row.EntryIndex}");
        Dictionary<int, string?> profiles = (operation?.Evidence.Rows<DbStructureOutcome>(DbStructureOutcome.DefaultTableName) ??
            DbStructureOutcome.SelectList(db)).Where(row => row.SourceFhirPackageKey == sourceKey && row.TargetFhirPackageKey == targetKey)
            .ToDictionary(row => row.Key, row => row.GenUrl);
        return triggers.Select(row => new UnsafeCodeConstraintPlan(
            row,
            JsonSerializer.Deserialize<ImmutableArray<UnsafeCodePathStep>>(row.SourcePathJson),
            new(row.IsWildcardVersion, JsonSerializer.Deserialize<ImmutableArray<string>>(row.VersionsJson)),
            string.Join("; ", Provenance(row.EvidenceKeysJson, evidence)
                .Concat(Provenance(row.AnnotationKeysJson, annotations)).Concat(Provenance(row.RuleKeysJson, rules))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            profiles.TryGetValue(row.StructureOutcomeKey, out string? canonical) && !string.IsNullOrEmpty(canonical) ? canonical
                : throw new InvalidOperationException("Unsafe-code trigger profile outcome or canonical is missing. Regenerate load, compare, outcomes, and export."))).ToArray();

        static IEnumerable<string> Provenance(string json, Dictionary<int, string> facts) =>
            JsonSerializer.Deserialize<int[]>(json)!.Select(key => facts.TryGetValue(key, out string? fact)
                ? fact : throw new InvalidOperationException("Unsafe-code trigger provenance is incomplete. Regenerate load, compare, outcomes, and export."));
    }

    internal static void InvalidateComparisons(IDbConnection db)
    {
        InvalidateOutcomes(db);
        List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(db);
        foreach (DbUnsafeCodePairState pair in pairs)
        {
            pair.ComparisonRevision = null;
        }

        pairs.Update(db);
    }

    internal static void InvalidateOutcomes(IDbConnection db)
    {
        EnsureStateTables(db);
        List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(db);
        foreach (DbUnsafeCodePairState pair in pairs)
        {
            ClearOutcome(pair);
        }

        pairs.Update(db);
    }

    internal static string RequireInputs(IDbConnection db)
    {
        DefinitionInventoryStore.RequireReady(db);
        if (!DefinitionInventoryStore.TableExists(db, DbUnsafeCodeInputState.DefaultTableName))
        {
            throw new InvalidOperationException("Unsafe-code inputs are missing. Reload the database, load maps, compare, generate outcomes, then export.");
        }
        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        if (input?.FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
            string.IsNullOrEmpty(input.CompletedInputRevision))
        {
            throw new InvalidOperationException("Unsafe-code inputs are missing, incomplete, or incompatible. Reload cross-version maps, rerun structure comparisons, and regenerate outcomes.");
        }

        DefinitionInventoryStore.RequireMapBinding(db, input.SnapshotId);
        return input.CompletedInputRevision;
    }

    internal static DbUnsafeCodePairState RequireComparison(IDbConnection db, int sourceKey, int targetKey)
    {
        string revision = RequireInputs(db);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (pair?.InputRevision != revision || string.IsNullOrEmpty(pair.ComparisonRevision))
        {
            throw new InvalidOperationException($"Unsafe-code structure comparison {sourceKey}->{targetKey} is missing or stale. Rerun structure comparisons after loading maps, then regenerate outcomes.");
        }

        return pair;
    }

    internal static string RequireInputs(IDbConnection db, XVerPreparedOperation operation)
    {
        operation.RequireConnection(db);
        operation.Audit();
        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        DbUnsafeCodeInputState captured = operation.Evidence.Rows<DbUnsafeCodeInputState>(DbUnsafeCodeInputState.DefaultTableName).Single();
        if (input?.FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
            string.IsNullOrEmpty(input.CompletedInputRevision) || operation.InventoryRevision is null ||
            input.CompletedInputRevision != captured.CompletedInputRevision || input.SnapshotId != captured.SnapshotId)
        {
            operation.Dispose();
            throw new InvalidOperationException("Unsafe-code inputs are missing, incomplete, or incompatible. Reload maps and compare.");
        }
        // The exact inventory and its snapshot binding were validated at entry;
        // the audit above checks their raw live values, not just this stamp.
        return input.CompletedInputRevision;
    }

    internal static DbUnsafeCodePairState RequireComparison(IDbConnection db, int sourceKey, int targetKey,
        XVerPreparedOperation operation)
    {
        string revision = RequireInputs(db, operation);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        DbUnsafeCodePairState? captured = operation.Evidence.Rows<DbUnsafeCodePairState>(DbUnsafeCodePairState.DefaultTableName)
            .SingleOrDefault(row => row.SourceFhirPackageKey == sourceKey && row.TargetFhirPackageKey == targetKey);
        if (pair?.InputRevision != revision || string.IsNullOrEmpty(pair.ComparisonRevision) ||
            pair.ComparisonRevision != captured?.ComparisonRevision)
        {
            operation.Dispose();
            throw new InvalidOperationException($"Unsafe-code structure comparison {sourceKey}->{targetKey} is missing or stale. Rerun structure comparisons.");
        }
        return pair;
    }

    internal static string BeginComparison(IDbConnection db, int sourceKey, int targetKey, XVerPreparedOperation operation)
    {
        string revision = RequireInputs(db, operation);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (pair is not null)
        {
            pair.ComparisonRevision = null;
            ClearOutcome(pair);
        }
        operation.Evidence.ApplyComparisonWrites(new(XVerOperandFamily.PairControl, XVerOutputTransitionKind.PairBegin, sourceKey, targetKey),
            [], pair is null ? [] : [pair], () =>
            {
                if (pair is not null)
                {
                    DbUnsafeCodePairState.Update(db, [pair]);
                }
            });
        return revision;
    }

    internal static void CompleteComparison(IDbConnection db, int sourceKey, int targetKey, string? revision,
        XVerPreparedOperation operation)
    {
        string currentRevision = RequireInputs(db, operation);
        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (currentRevision != revision || input?.CompletedInputRevision != revision ||
            input.FormatVersion != UnsafeCodeConstants.InputFormatVersion || pair?.InputRevision != revision ||
            pair.ComparisonRevision is not null)
        {
            operation.Dispose();
            throw new InvalidOperationException("Catalog, map inputs or pair control changed during comparison. Reload maps and compare.");
        }
        operation.ClassifyComparisonPair(sourceKey, targetKey);
        operation.Audit();
        pair.ComparisonRevision = Guid.NewGuid().ToString("N");
        ClearOutcome(pair);
        operation.Evidence.ApplyComparisonWrites(new(XVerOperandFamily.PairControl, XVerOutputTransitionKind.PairComplete, sourceKey, targetKey),
            [], [pair], () => DbUnsafeCodePairState.Update(db, [pair]));
    }

    internal static string? BeginComparison(IDbConnection db, int sourceKey, int targetKey)
    {
        string revision = RequireInputs(db);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (pair is not null)
        {
            pair.ComparisonRevision = null;
            ClearOutcome(pair);
            DbUnsafeCodePairState.Update(db, [pair]);
        }

        return revision;
    }

    internal static void CompleteComparison(IDbConnection db, int sourceKey, int targetKey, string? revision)
    {
        if (revision is null)
        {
            return;
        }

        string currentRevision = RequireInputs(db);
        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (currentRevision != revision || input?.CompletedInputRevision != revision || input.FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
            pair?.InputRevision != revision)
        {
            throw new InvalidOperationException("Catalog or map inputs changed during comparison. Reload maps, compare, generate outcomes, then export.");
        }

        Dictionary<int, DbFhirPackage> packages = DbFhirPackage.SelectDict(db);
        ProfileUseFacts.ClassifyPair(db, new(packages[sourceKey], packages[targetKey]));
        pair.ComparisonRevision = Guid.NewGuid().ToString("N");
        ClearOutcome(pair);
        DbUnsafeCodePairState.Update(db, [pair]);
    }

    internal static DbUnsafeCodePairState BeginOutcomes(IDbConnection db, int sourceKey, int targetKey)
    {
        DbUnsafeCodePairState pair = RequireComparison(db, sourceKey, targetKey);
        ClearOutcome(pair);
        DbUnsafeCodePairState.Update(db, [pair]);
        DbUnsafeCodeTrigger.Delete(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        return pair;
    }

    internal static DbUnsafeCodePairState BeginOutcomes(IDbConnection db, int sourceKey, int targetKey,
        XVerPreparedOperation operation)
    {
        operation.OutcomeDecisions(sourceKey, targetKey);
        DbUnsafeCodePairState pair = RequireComparison(db, sourceKey, targetKey, operation);
        ClearOutcome(pair);
        operation.Evidence.BeginOutcomePair(sourceKey, targetKey, pair, () =>
        {
            DbUnsafeCodePairState.Update(db, [pair]);
            DbUnsafeCodeTrigger.Delete(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        });
        return pair;
    }

    internal static void CompleteOutcomes(IDbConnection db, DbUnsafeCodePairState dependency,
        XVerPreparedOperation operation)
    {
        DbUnsafeCodePairState pair = RequireComparison(db, dependency.SourceFhirPackageKey, dependency.TargetFhirPackageKey, operation);
        if (pair.InputRevision != dependency.InputRevision || pair.ComparisonRevision != dependency.ComparisonRevision ||
            pair.OutcomeInputRevision is not null || pair.OutcomeComparisonRevision is not null || pair.OutcomeFormatVersion is not null)
        {
            operation.Dispose();
            throw new InvalidOperationException("Unsafe-code dependencies changed during outcomes. Rerun comparisons and outcomes.");
        }
        Dictionary<int, DbFhirPackage> packages = operation.Evidence.Rows<DbFhirPackage>(DbFhirPackage.DefaultTableName).ToDictionary(row => row.Key);
        if (!ProfileUseFacts.RequirePersistedDecisions(db, new(packages[pair.SourceFhirPackageKey], packages[pair.TargetFhirPackageKey]),
            operation.OutcomeDecisions(pair.SourceFhirPackageKey, pair.TargetFhirPackageKey), operation.Catalog))
        {
            return;
        }
        operation.Audit();
        pair.OutcomeInputRevision = pair.InputRevision;
        pair.OutcomeComparisonRevision = pair.ComparisonRevision;
        pair.OutcomeFormatVersion = UnsafeCodeConstants.OutcomeFormatVersion;
        operation.Evidence.ApplyOutcomeWrites(
            new(XVerOperandFamily.PairControl, XVerOutputTransitionKind.PairComplete, pair.SourceFhirPackageKey, pair.TargetFhirPackageKey),
            [], [pair], () => DbUnsafeCodePairState.Update(db, [pair]));
    }

    internal static void CompleteOutcomes(IDbConnection db, DbUnsafeCodePairState dependency)
    {
        DbUnsafeCodePairState pair = RequireComparison(db, dependency.SourceFhirPackageKey, dependency.TargetFhirPackageKey);
        if (pair.InputRevision != dependency.InputRevision || pair.ComparisonRevision != dependency.ComparisonRevision)
        {
            throw new InvalidOperationException($"Unsafe-code dependencies changed during outcomes for {pair.SourceFhirPackageKey}->{pair.TargetFhirPackageKey}. Rerun structure comparisons and outcomes.");
        }

        Dictionary<int, DbFhirPackage> packages = DbFhirPackage.SelectDict(db);
        if (!ProfileUseFacts.RequirePersistedDecisions(db,
            new(packages[pair.SourceFhirPackageKey], packages[pair.TargetFhirPackageKey])))
        {
            return;
        }
        pair.OutcomeInputRevision = pair.InputRevision;
        pair.OutcomeComparisonRevision = pair.ComparisonRevision;
        pair.OutcomeFormatVersion = UnsafeCodeConstants.OutcomeFormatVersion;
        DbUnsafeCodePairState.Update(db, [pair]);
    }

    internal static void InsertTriggers(
        IDbConnection db,
        IEnumerable<UnsafeCodeTrigger> triggers, XVerPreparedOperation? operation = null)
    {
        List<DbUnsafeCodeTrigger> rows = triggers.Select(trigger => new DbUnsafeCodeTrigger
        {
            Key = DbUnsafeCodeTrigger.GetIndex(),
            StructureOutcomeKey = trigger.StructureOutcomeKey,
            SourceFhirPackageKey = trigger.Context.SourceFhirPackageKey,
            TargetFhirPackageKey = trigger.Context.TargetFhirPackageKey,
            SourceElementId = trigger.Context.SourceElementId,
            TargetElementId = trigger.Context.TargetElementId,
            SourcePathJson = JsonSerializer.Serialize(trigger.SourcePath),
            System = trigger.Identity.System,
            Code = trigger.Identity.Code,
            SourceVersion = trigger.Identity.Version,
            IsWildcardVersion = trigger.VersionMatch.IsWildcard,
            VersionsJson = JsonSerializer.Serialize(trigger.VersionMatch.Versions),
            EvidenceKeysJson = JsonSerializer.Serialize(trigger.EvidenceKeys),
            RuleKeysJson = JsonSerializer.Serialize(trigger.RuleKeys),
            AnnotationKeysJson = JsonSerializer.Serialize(trigger.AnnotationKeys),
        }).ToList();
        if (operation is null)
        {
            rows.Insert(db, insertPrimaryKey: true);
        }
        else if (rows.Count == 0)
        {
            operation.Audit();
        }
        else
        {
            operation.Evidence.ApplyOutcomeWrites(
                new(XVerOperandFamily.StructureOutcomes, XVerOutputTransitionKind.PairFlush,
                    rows[0].SourceFhirPackageKey, rows[0].TargetFhirPackageKey),
                rows, [], () => rows.Insert(db, insertPrimaryKey: true));
        }
    }

    private static void ClearOutcome(DbUnsafeCodePairState pair)
    {
        pair.OutcomeInputRevision = null;
        pair.OutcomeComparisonRevision = null;
        pair.OutcomeFormatVersion = null;
    }

    internal static void EnsureStateTables(IDbConnection db)
    {
        DbUnsafeCodeInputState.CreateTable(db);
        DbUnsafeCodePairState.CreateTable(db);
    }

    internal static void InvalidateInputs(IDbConnection db)
    {
        EnsureStateTables(db);
        List<DbUnsafeCodeInputState> inputs = DbUnsafeCodeInputState.SelectList(db);
        foreach (DbUnsafeCodeInputState input in inputs)
        {
            input.CompletedInputRevision = null;
        }

        inputs.Update(db);
        List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(db);
        foreach (DbUnsafeCodePairState pair in pairs)
        {
            pair.InputRevision = null;
            pair.ComparisonRevision = null;
            pair.OutcomeInputRevision = null;
            pair.OutcomeComparisonRevision = null;
            pair.OutcomeFormatVersion = null;
        }

        pairs.Update(db);
    }

    internal static void BeginInputs(IDbConnection db, string sourceMode, string? sourceRoot)
    {
        string inventoryRevision = DefinitionInventoryStore.RequireReady(db);
        InvalidateInputs(db);
        DbUnsafeCodeInputState.Delete(db);
        new DbUnsafeCodeInputState
        {
            Key = 1,
            SnapshotId = Guid.NewGuid().ToString("N"),
            FormatVersion = UnsafeCodeConstants.InputFormatVersion,
            SourceMode = sourceMode,
            SourceRoot = sourceRoot,
        }.Insert(db, insertPrimaryKey: true);
        DefinitionInventoryStore.BeginMapInputs(db, DbUnsafeCodeInputState.SelectSingle(db, Key: 1)!.SnapshotId, inventoryRevision);
    }

    internal static void CompleteInputs(IDbConnection db, IReadOnlyList<DbFhirPackage> packages)
    {
        DbUnsafeCodeInputState input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1)
            ?? throw new InvalidOperationException("Unsafe-code input load was not started.");
        string inventoryRevision = DefinitionInventoryStore.RequirePendingMapBinding(db, input.SnapshotId);
        string revision = Guid.NewGuid().ToString("N");
        DbUnsafeCodePairState.Delete(db);
        List<DbUnsafeCodePairState> pairs = [];
        foreach (DbFhirPackage source in packages)
        {
            foreach (DbFhirPackage target in packages)
            {
                if (source.Key != target.Key)
                {
                    pairs.Add(new()
                    {
                        Key = DbUnsafeCodePairState.GetIndex(),
                        SourceFhirPackageKey = source.Key,
                        TargetFhirPackageKey = target.Key,
                        InputRevision = revision,
                    });
                }
            }
        }

        pairs.Insert(db, insertPrimaryKey: true);
        DefinitionInventoryStore.CompleteMapInputs(db, input.SnapshotId, inventoryRevision);
        input.CompletedInputRevision = revision;
        DbUnsafeCodeInputState.Update(db, [input]);
    }

    internal static void ReplaceWithEmptyInputs(IDbConnection db, IReadOnlyList<DbFhirPackage> packages)
    {
        DefinitionInventoryStore.RequireReady(db);
        BeginInputs(db, "none", null);
        DefinitionInventoryStore.InvalidateMapInputs(db);
        DbProfileMapOwnerFacts.DropTable(db);
        DbProfileMapOwnerFacts.CreateTable(db);
        DefinitionInventoryStore.BeginMapInputs(db, DbUnsafeCodeInputState.SelectSingle(db, Key: 1)!.SnapshotId,
            DefinitionInventoryStore.RequireReady(db));
        DbUnsafeCodeElementAnnotation.DropTable(db);
        DbUnsafeCodeMapEvidence.DropTable(db);
        DbUnsafeCodeRule.DropTable(db);
        DbUnsafeCodeElementAnnotation.CreateTable(db);
        DbUnsafeCodeMapEvidence.CreateTable(db);
        DbUnsafeCodeRule.CreateTable(db);
        CompleteInputs(db, packages);
    }

    internal static void InsertRules(IDbConnection db, IEnumerable<UnsafeCodeRule> rules)
    {
        List<DbUnsafeCodeRule> rows = rules.Select(rule => new DbUnsafeCodeRule
        {
            Key = DbUnsafeCodeRule.GetIndex(),
            System = rule.Identity.System,
            Version = rule.Identity.Version,
            Code = rule.Identity.Code,
            NoMapOnly = rule.NoMapOnly,
            ExcludedRelationshipLiteral = string.Join(',', rule.ExcludedRelationships.Select(value => value.GetLiteral()).Order(StringComparer.Ordinal)),
            ExcludedVersionLiteral = string.Join(',', rule.ExcludedVersions.Order(StringComparer.Ordinal)),
            SourceRelativeFilename = rule.SourceRelativeFilename,
            EntryIndex = rule.EntryIndex,
        }).ToList();
        rows.Insert(db, insertPrimaryKey: true);
    }

    internal static UnsafeCodeRule ReadRule(DbUnsafeCodeRule row) => new(
        new(row.System, row.Version, row.Code),
        row.NoMapOnly,
        row.ExcludedRelationshipLiteral.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => EnumUtility.ParseLiteral<ConceptMap.ConceptMapRelationship>(value)!.Value)
            .ToImmutableHashSet(),
        row.ExcludedVersionLiteral.Split(',', StringSplitOptions.RemoveEmptyEntries).ToImmutableHashSet(StringComparer.Ordinal),
        row.SourceRelativeFilename,
        row.EntryIndex);
}
