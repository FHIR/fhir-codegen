// <copyright file="DbUnsafeCodeClasses.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Fhir.CodeGen.SQLiteGenerator;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;

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
        EnsureStateTables(db);
        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        if (input?.FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
            string.IsNullOrEmpty(input.CompletedInputRevision))
        {
            throw new InvalidOperationException("Unsafe-code inputs are missing, incomplete, or incompatible. Reload cross-version maps, rerun structure comparisons, and regenerate outcomes.");
        }

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

    internal static string? BeginComparison(IDbConnection db, int sourceKey, int targetKey)
    {
        EnsureStateTables(db);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (pair is not null)
        {
            pair.ComparisonRevision = null;
            ClearOutcome(pair);
            DbUnsafeCodePairState.Update(db, [pair]);
        }

        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        return input?.FormatVersion == UnsafeCodeConstants.InputFormatVersion ? input.CompletedInputRevision : null;
    }

    internal static void CompleteComparison(IDbConnection db, int sourceKey, int targetKey, string? revision)
    {
        if (revision is null)
        {
            return;
        }

        DbUnsafeCodeInputState? input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1);
        DbUnsafeCodePairState? pair = DbUnsafeCodePairState.SelectSingle(db, SourceFhirPackageKey: sourceKey, TargetFhirPackageKey: targetKey);
        if (input?.CompletedInputRevision != revision || input.FormatVersion != UnsafeCodeConstants.InputFormatVersion ||
            pair?.InputRevision != revision)
        {
            return;
        }

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

    internal static void CompleteOutcomes(IDbConnection db, DbUnsafeCodePairState dependency)
    {
        DbUnsafeCodePairState pair = RequireComparison(db, dependency.SourceFhirPackageKey, dependency.TargetFhirPackageKey);
        if (pair.InputRevision != dependency.InputRevision || pair.ComparisonRevision != dependency.ComparisonRevision)
        {
            throw new InvalidOperationException($"Unsafe-code dependencies changed during outcomes for {pair.SourceFhirPackageKey}->{pair.TargetFhirPackageKey}. Rerun structure comparisons and outcomes.");
        }

        pair.OutcomeInputRevision = pair.InputRevision;
        pair.OutcomeComparisonRevision = pair.ComparisonRevision;
        pair.OutcomeFormatVersion = UnsafeCodeConstants.OutcomeFormatVersion;
        DbUnsafeCodePairState.Update(db, [pair]);
    }

    internal static void InsertTriggers(
        IDbConnection db,
        IEnumerable<UnsafeCodeTrigger> triggers,
        IReadOnlyDictionary<UnsafeCodeMappingContext, int[]> annotationKeys)
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
            AnnotationKeysJson = JsonSerializer.Serialize(trigger.EvidenceKeys.Length > 0 && annotationKeys.TryGetValue(trigger.Context, out int[]? keys) ? keys : []),
        }).ToList();
        rows.Insert(db, insertPrimaryKey: true);
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
    }

    internal static void CompleteInputs(IDbConnection db, IReadOnlyList<DbFhirPackage> packages)
    {
        DbUnsafeCodeInputState input = DbUnsafeCodeInputState.SelectSingle(db, Key: 1)
            ?? throw new InvalidOperationException("Unsafe-code input load was not started.");
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
        input.CompletedInputRevision = revision;
        DbUnsafeCodeInputState.Update(db, [input]);
    }

    internal static void ReplaceWithEmptyInputs(IDbConnection db, IReadOnlyList<DbFhirPackage> packages)
    {
        BeginInputs(db, "none", null);
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
