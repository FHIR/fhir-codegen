using System;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.SQLiteGenerator;

namespace Fhir.CodeGen.Comparison.Models;

public enum BidirectionalRelationshipCodes
{
    None = 0,
    Equivalent,
    NewerNarrows,
    NewerBroadens,
    Related,
    NotRelated,
    Mismatched,
}

public static class DbComparisonClasses
{
    public static void LoadIndices(IDbConnection db)
    {
        try { DbValueSetComparison.LoadMaxKey(db); } catch { }
        try { DbValueSetConceptComparison.LoadMaxKey(db); } catch { }

        try { DbStructureComparison.LoadMaxKey(db); } catch { }

        try { DbElementComparison.LoadMaxKey(db); } catch { }
        try { DbElementTypeComparison.LoadMaxKey(db); } catch { }
    }

    public static void DropTables(
        IDbConnection db,
        bool forValueSets = true,
        bool forStructures = true)
    {
        if (forStructures)
        {
            UnsafeCodeInputStore.InvalidateComparisons(db);
        }

        if (forValueSets)
        {
            DbValueSetComparison.DropTable(db);
            DbValueSetConceptComparison.DropTable(db);
        }

        if (forStructures)
        {
            DbStructureComparison.DropTable(db);
            DbElementComparison.DropTable(db);
            //DbCollatedTypeComparison.DropTable(db);
            DbElementTypeComparison.DropTable(db);
        }
    }

    public static void CreateTables(
        IDbConnection db,
        bool forValueSets = true,
        bool forStructures = true)
    {
        if (forValueSets)
        {
            DbValueSetComparison.CreateTable(db);
            DbValueSetConceptComparison.CreateTable(db);
        }

        if (forStructures)
        {
            DbStructureComparison.CreateTable(db);
            DbElementComparison.CreateTable(db);
            //DbCollatedTypeComparison.CreateTable(db);
            DbElementTypeComparison.CreateTable(db);
        }
    }
}

public interface IDbComparisonCell
{
    IDbComparisonCell? LeftCell { get; }
    DbComparisonBase? LeftComparison { get; }

    IDbComparisonCell? RightCell { get; }
    DbComparisonBase? RightComparison { get; }
}

public static class DbComparisonCellExtensions
{
    public static string ToRightMessage(this IDbComparisonCell c) => (c.RightComparison == null)
        ? string.Empty
        : $"{c.RightComparison.Relationship}: {c.RightComparison.TechnicalMessage}";

    public static string FromRightMessage(this IDbComparisonCell c) => (c.RightCell?.LeftComparison == null)
        ? string.Empty
        : $"{c.RightCell.LeftComparison.Relationship}: {c.RightCell.LeftComparison.TechnicalMessage}";

    public static string ToLeftMessage(this IDbComparisonCell c) => (c.LeftComparison == null)
        ? string.Empty
        : $"{c.LeftComparison.Relationship}: {c.LeftComparison.TechnicalMessage}";
    public static string FromLeftMessage(this IDbComparisonCell c) => (c.LeftCell?.RightComparison == null)
        ? string.Empty
        : $"{c.LeftCell.RightComparison.Relationship}: {c.LeftCell.RightComparison.TechnicalMessage}";

    public static Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? LeftToRight(this BidirectionalRelationshipCodes br) => br switch
    {
        BidirectionalRelationshipCodes.Equivalent => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent,
        BidirectionalRelationshipCodes.NewerNarrows => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget,
        BidirectionalRelationshipCodes.NewerBroadens => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget,
        BidirectionalRelationshipCodes.Related => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.RelatedTo,
        BidirectionalRelationshipCodes.NotRelated => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.NotRelatedTo,
        _ => null,
    };

    public static Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? RightToLeft(this BidirectionalRelationshipCodes br) => br switch
    {
        BidirectionalRelationshipCodes.Equivalent => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent,
        BidirectionalRelationshipCodes.NewerNarrows => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget,
        BidirectionalRelationshipCodes.NewerBroadens => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget,
        BidirectionalRelationshipCodes.Related => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.RelatedTo,
        BidirectionalRelationshipCodes.NotRelated => Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.NotRelatedTo,
        _ => null,
    };

    public static BidirectionalRelationshipCodes? BidirectionalRight(this IDbComparisonCell c)
    {
        if ((c.RightComparison == null) || (c.RightCell?.LeftComparison == null))
        {
            return null;
        }

        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? up = c.RightComparison.Relationship;
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? down = c.RightCell.LeftComparison.Relationship;

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent))
        {
            return BidirectionalRelationshipCodes.Equivalent;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget))
        {
            return BidirectionalRelationshipCodes.NewerBroadens;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget))
        {
            return BidirectionalRelationshipCodes.NewerNarrows;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.RelatedTo) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.RelatedTo))
        {
            return BidirectionalRelationshipCodes.Related;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.NotRelatedTo) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.NotRelatedTo))
        {
            return BidirectionalRelationshipCodes.NotRelated;
        }

        return BidirectionalRelationshipCodes.Mismatched;
    }

    public static BidirectionalRelationshipCodes? BidirectionalLeft(this IDbComparisonCell c)
    {
        if ((c.LeftComparison == null) || (c.LeftCell?.RightComparison == null))
        {
            return null;
        }

        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? up = c.LeftCell.RightComparison.Relationship;
        Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? down = c.LeftComparison.Relationship;

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent))
        {
            return BidirectionalRelationshipCodes.Equivalent;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget))
        {
            return BidirectionalRelationshipCodes.NewerBroadens;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget))
        {
            return BidirectionalRelationshipCodes.NewerNarrows;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.RelatedTo) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.RelatedTo))
        {
            return BidirectionalRelationshipCodes.Related;
        }

        if ((up == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.NotRelatedTo) &&
            (down == Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.NotRelatedTo))
        {
            return BidirectionalRelationshipCodes.NotRelated;
        }

        return BidirectionalRelationshipCodes.Mismatched;
    }
}


[CgSQLiteBaseClass]
public abstract class DbComparisonBase : DbRecordBase
{
    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int SourceFhirPackageKey { get; set; }

    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence { get; set; }

    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int TargetFhirPackageKey { get; set; }

    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence { get; set; }

    public required int Steps { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship { get; set; }
    public required bool NotMapped { get; set; }
    public required bool? IsIdentical { get; set; }

    public required string? TechnicalMessage { get; set; }
    public required string? UserMessage { get; set; }
    //public required bool? IsGenerated { get; set; }
    //public string? LastReviewedBy { get; set; } = null;
    //public DateTime? LastReviewedOn { get; set; } = null;

    public int? ContentKeyR2 { get; set; } = null;
    public int? ContentKeyR3 { get; set; } = null;
    public int? ContentKeyR4 { get; set; } = null;
    public int? ContentKeyR4B { get; set; } = null;
    public int? ContentKeyR5 { get; set; } = null;
    public int? ContentKeyR6 { get; set; } = null;

    [CgSQLiteIgnore]
    public int?[] ContentKeys
    {
        get => [ContentKeyR2, ContentKeyR3, ContentKeyR4, ContentKeyR4B, ContentKeyR5, ContentKeyR6];
        set
        {
            switch (value.Length)
            {
                case 0:
                    ContentKeyR2 = null;
                    ContentKeyR3 = null;
                    ContentKeyR4 = null;
                    ContentKeyR4B = null;
                    ContentKeyR5 = null;
                    ContentKeyR6 = null;
                    return;

                case 5:
                    ContentKeyR2 = value[0];
                    ContentKeyR3 = value[1];
                    ContentKeyR4 = value[2];
                    ContentKeyR4B = value[3];
                    ContentKeyR5 = value[4];
                    ContentKeyR6 = null;
                    return;

                case 6:
                    ContentKeyR2 = value[0];
                    ContentKeyR3 = value[1];
                    ContentKeyR4 = value[2];
                    ContentKeyR4B = value[3];
                    ContentKeyR5 = value[4];
                    ContentKeyR6 = value[5];
                    return;
            }

            throw new ArgumentException($"Invalid number of keys: {value.Length}. Expected 5 or 6.");
        }
    }

    [CgSQLiteIgnore]
    public int?[] ContentKeysInverted
    {
        get => [ContentKeyR6, ContentKeyR5, ContentKeyR4B, ContentKeyR4, ContentKeyR3, ContentKeyR2];
    }

    public int? GetContentKeyFromSource(int offsetFromSource)
    {
        int targetIndex = (int)SourceFhirSequence - 1;

        int index = (SourceFhirSequence > TargetFhirSequence)
            ? targetIndex + offsetFromSource
            : targetIndex - offsetFromSource;

        switch (index)
        {
            case 0: return ContentKeyR2;
            case 1: return ContentKeyR3;
            case 2: return ContentKeyR4;
            case 3: return ContentKeyR4B;
            case 4: return ContentKeyR5;
            case 5: return ContentKeyR6;
            default: return null;
        }
    }

    public int? GetContentKeyFromTarget(int offsetFromTarget)
    {
        int targetIndex = (int)TargetFhirSequence - 1;

        int index = (SourceFhirSequence > TargetFhirSequence)
            ? targetIndex + offsetFromTarget
            : targetIndex - offsetFromTarget;

        switch (index)
        {
            case 0: return ContentKeyR2;
            case 1: return ContentKeyR3;
            case 2: return ContentKeyR4;
            case 3: return ContentKeyR4B;
            case 4: return ContentKeyR5;
            case 5: return ContentKeyR6;
            default: return null;
        }
    }

    [CgSQLiteIgnore]
    public int? PriorContentKeyFromArray => GetContentKeyFromTarget(1);

    [CgSQLiteIgnore]
    public int? PriorContentKeyFromArrayInverted => GetContentKeyFromSource(1);


    [CgSQLiteIgnore]
    public virtual int SourceContentKey { get; set; }

    [CgSQLiteIgnore]
    public virtual int? TargetContentKey { get; set; }
}

[CgSQLiteBaseClass]
public abstract class DbArtifactComparisonBase : DbComparisonBase
{
    public required string SourceCanonicalVersioned { get; set; }
    public required string SourceCanonicalUnversioned { get; set; }
    public required string SourceId { get; set; }
    public required string SourceName { get; set; }
    public required string SourceVersion { get; set; }

    public required string? TargetCanonicalVersioned { get; set; }
    public required string? TargetCanonicalUnversioned { get; set; }
    public required string? TargetId { get; set; }
    public required string? TargetName { get; set; }
    public required string? TargetVersion { get; set; }
}


[CgSQLiteTable(tableName: "ValueSetComparisons")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(TargetValueSetKey), nameof(Key))]
public partial class DbValueSetComparison : DbArtifactComparisonBase
{
    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int SourceValueSetKey { get; set; }

    [CgSQLiteIgnore]
    public override int SourceContentKey { get => this.SourceValueSetKey; set => this.SourceValueSetKey = value; }


    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int? TargetValueSetKey { get; set; }

    [CgSQLiteIgnore]
    public override int? TargetContentKey { get => this.TargetValueSetKey; set => this.TargetValueSetKey = value; }


    [CgSQLiteForeignKey(referenceTable: "ValueSetMappings", referenceColumn: nameof(DbValueSetMapping.Key))]
    public required int? ValueSetMappingKey { get; set; }


    public required bool? CodeLiteralsAreIdentical { get; set; }
}

[CgSQLiteTable(tableName: "ValueSetConceptComparisons")]
[CgSQLiteIndex(nameof(ValueSetComparisonKey))]
[CgSQLiteIndex(nameof(SourceValueSetKey), nameof(TargetConceptKey), nameof(Key))]
[CgSQLiteIndex(nameof(SourceConceptKey), nameof(TargetValueSetKey), nameof(Key))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceFhirPackageKey))]
public partial class DbValueSetConceptComparison : DbComparisonBase
{
    [CgSQLiteForeignKey(referenceTable: "ValueSetComparisons", referenceColumn: nameof(DbValueSetComparison.Key))]
    public required int ValueSetComparisonKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int SourceValueSetKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConcepts", referenceColumn: nameof(DbValueSetConcept.Key))]
    public required int SourceConceptKey { get; set; }

    [CgSQLiteIgnore]
    public override int SourceContentKey { get => this.SourceConceptKey; set => this.SourceConceptKey = value; }

    public required string SourceSystem { get; set; }
    public required string SourceCode { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int? TargetValueSetKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConcepts", referenceColumn: nameof(DbValueSetConcept.Key))]
    public required int? TargetConceptKey { get; set; }

    [CgSQLiteIgnore]
    public override int? TargetContentKey { get => this.TargetConceptKey; set => this.TargetConceptKey = value; }

    public required string? TargetSystem { get; set; }
    public required string? TargetCode { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConceptMappings", referenceColumn: nameof(DbValueSetConceptMapping.Key))]
    public int? ValueSetConceptMappingKey { get; set; }


    public required bool? CodeLiteralsAreIdentical { get; set; }
    public required bool SourceCodeTreatedAsEscapeValve { get; set; }
    public required bool? TargetCodeTreatedAsEscapeValve { get; set; }
}


[CgSQLiteTable(tableName: "StructureComparisons")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceStructureKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceStructureKey), nameof(TargetStructureKey))]
public partial class DbStructureComparison : DbArtifactComparisonBase
{
    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int SourceStructureKey { get; set; }

    [CgSQLiteIgnore]
    public override int SourceContentKey { get => this.SourceStructureKey; set => this.SourceStructureKey = value; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int? TargetStructureKey { get; set; }

    [CgSQLiteIgnore]
    public override int? TargetContentKey { get => this.TargetStructureKey; set => this.TargetStructureKey = value; }

    [CgSQLiteForeignKey(referenceTable: "StructureMappings", referenceColumn: nameof(DbStructureMapping.Key))]
    public required int? StructureMappingKey { get; set; }

    public required bool? ElementRelativePathsAreIdentical { get; set; }

    public required bool IsBasedOnFallbackMapping { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship { get; set; }
    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship { get; set; }
}


[CgSQLiteTable(tableName: "ElementComparisons")]
[CgSQLiteIndex(nameof(StructureComparisonKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceElementKey), nameof(SourceRequiresMoreValues))]
public partial class DbElementComparison : DbComparisonBase
{
    [CgSQLiteForeignKey(referenceTable: "StructureComparisons", referenceColumn: nameof(DbStructureComparison.Key))]
    public required int StructureComparisonKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int SourceStructureKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int SourceElementKey { get; set; }

    [CgSQLiteIgnore]
    public override int SourceContentKey { get => this.SourceElementKey; set => this.SourceElementKey = value; }

    public required string SourceElementId { get; set; }


    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int? TargetStructureKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int? TargetElementKey { get; set; }

    [CgSQLiteIgnore]
    public override int? TargetContentKey { get => this.TargetElementKey; set => this.TargetElementKey = value; }

    public required string? TargetElementId { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ElementMappings", referenceColumn: nameof(DbElementMapping.Key))]
    public int? ElementMappingKey { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship { get; set; }
    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship { get; set; }

    public required bool? RelativePathsAreIdentical { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetComparisons", referenceColumn: nameof(DbValueSetComparison.Key))]
    public required int? BoundValueSetComparisonKey { get; set; }
    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? BoundValueSetRelationship { get; set; }

    public required bool? SourceRequiresMoreValues { get; set; }
    public required bool? TargetRequiresMoreValues { get; set; }

    public required bool? SourceAllowsMoreValues { get; set; }
    public required bool? TargetAllowsMoreValues { get; set; }


    //[CgSQLiteForeignKey(referenceTable: "ElementTypeComparisons", referenceColumn: nameof(DbCollatedTypeComparison.Key))]
    //public required int? CollatedTypeComparisonKey { get; set; }
    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeRelationship { get; set; }
    public required string? TypeMessage { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TargetProfileRelationship { get; set; }
    public required string? TargetProfileMessage { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeProfileRelationship { get; set; }
    public required string? TypeProfileMessage { get; set; }

}

//[CgSQLiteTable(tableName: "CollatedTypeComparisons")]
//[CgSQLiteIndex(nameof(SourceElementKey), nameof(TargetElementKey))]
//public partial class DbCollatedTypeComparison : DbComparisonBase
//{
//    [CgSQLiteForeignKey(referenceTable: "ElementComparisons", referenceColumn: nameof(DbElementComparison.Key))]
//    public required int ElementComparisonKey { get; set; }

//    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
//    public required int SourceElementKey { get; set; }

//    [CgSQLiteForeignKey(referenceTable: "CollatedTypes", referenceColumn: nameof(DbCollatedType.Key))]
//    public required int SourceCollatedTypeKey { get; set; }

//    [CgSQLiteIgnore]
//    public override int SourceContentKey { get => this.SourceCollatedTypeKey; set => this.SourceCollatedTypeKey = value; }


//    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
//    public required int? TargetElementKey { get; set; }

//    [CgSQLiteForeignKey(referenceTable: "CollatedTypes", referenceColumn: nameof(DbCollatedType.Key))]
//    public required int? TargetCollatedTypeKey { get; set; }

//    [CgSQLiteIgnore]
//    public override int? TargetContentKey { get => this.TargetCollatedTypeKey; set => this.TargetCollatedTypeKey = value; }


//    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TargetProfileRelationship { get; set; }
//    public required string? TargetProfileMessage { get; set; }

//    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeProfileRelationship { get; set; }
//    public required string? TypeProfileMessage { get; set; }


//    //public string? IdenticalTypesKeysLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<int> IdenticalTypesKeys
//    //{
//    //    get => IdenticalTypesKeysLiteral is null
//    //        ? []
//    //        : (IdenticalTypesKeysLiteral
//    //            .Split(',')
//    //            .Select(v => int.TryParse(v, out int iv) ? (int)iv : (int?)null)
//    //            .Where(v => v is not null) as IEnumerable<int>)!
//    //            .ToList();
//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            IdenticalTypesKeysLiteral = null;
//    //            return;
//    //        }

//    //        IdenticalTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? IdenticalTypesSymbolsLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<string> IdenticalTypesSymbols
//    //{
//    //    get => IdenticalTypesSymbolsLiteral is null
//    //        ? []
//    //        : IdenticalTypesSymbolsLiteral.Split(',').ToList();

//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            IdenticalTypesSymbolsLiteral = null;
//    //            return;
//    //        }

//    //        IdenticalTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}


//    //public string? EquivalentTypesKeysLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<int> EquivalentTypesKeys
//    //{
//    //    get => EquivalentTypesKeysLiteral is null
//    //        ? []
//    //        : (EquivalentTypesKeysLiteral
//    //            .Split(',')
//    //            .Select(v => int.TryParse(v, out int iv) ? (int)iv : (int?)null)
//    //            .Where(v => v is not null) as IEnumerable<int>)!
//    //            .ToList();
//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            EquivalentTypesKeysLiteral = null;
//    //            return;
//    //        }

//    //        EquivalentTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? EquivalentTypesSymbolsLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<string> EquivalentTypesSymbols
//    //{
//    //    get => EquivalentTypesSymbolsLiteral is null
//    //        ? []
//    //        : EquivalentTypesSymbolsLiteral.Split(',').ToList();

//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            EquivalentTypesSymbolsLiteral = null;
//    //            return;
//    //        }

//    //        EquivalentTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}


//    //public string? BroaderTypesKeysLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<int> BroaderTypesKeys
//    //{
//    //    get => BroaderTypesKeysLiteral is null
//    //        ? []
//    //        : (BroaderTypesKeysLiteral
//    //            .Split(',')
//    //            .Select(v => int.TryParse(v, out int iv) ? (int)iv : (int?)null)
//    //            .Where(v => v is not null) as IEnumerable<int>)!
//    //            .ToList();
//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            BroaderTypesKeysLiteral = null;
//    //            return;
//    //        }

//    //        BroaderTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? BroaderTypesSymbolsLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<string> BroaderTypesSymbols
//    //{
//    //    get => BroaderTypesSymbolsLiteral is null
//    //        ? []
//    //        : BroaderTypesSymbolsLiteral.Split(',').ToList();

//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            BroaderTypesSymbolsLiteral = null;
//    //            return;
//    //        }

//    //        BroaderTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? NarrowerTypesKeysLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<int> NarrowerTypesKeys
//    //{
//    //    get => NarrowerTypesKeysLiteral is null
//    //        ? []
//    //        : (NarrowerTypesKeysLiteral
//    //            .Split(',')
//    //            .Select(v => int.TryParse(v, out int iv) ? (int)iv : (int?)null)
//    //            .Where(v => v is not null) as IEnumerable<int>)!
//    //            .ToList();
//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            NarrowerTypesKeysLiteral = null;
//    //            return;
//    //        }

//    //        NarrowerTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? NarrowerTypesSymbolsLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<string> NarrowerTypesSymbols
//    //{
//    //    get => NarrowerTypesSymbolsLiteral is null
//    //        ? []
//    //        : NarrowerTypesSymbolsLiteral.Split(',').ToList();

//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            NarrowerTypesSymbolsLiteral = null;
//    //            return;
//    //        }

//    //        NarrowerTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? AddedTypesKeysLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<int> AddedTypesKeys
//    //{
//    //    get => AddedTypesKeysLiteral is null
//    //        ? []
//    //        : (AddedTypesKeysLiteral
//    //            .Split(',')
//    //            .Select(v => int.TryParse(v, out int iv) ? (int)iv : (int?)null)
//    //            .Where(v => v is not null) as IEnumerable<int>)!
//    //            .ToList();
//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            AddedTypesKeysLiteral = null;
//    //            return;
//    //        }

//    //        AddedTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? AddedTypesSymbolsLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<string> AddedTypesSymbols
//    //{
//    //    get => AddedTypesSymbolsLiteral is null
//    //        ? []
//    //        : AddedTypesSymbolsLiteral.Split(',').ToList();

//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            AddedTypesSymbolsLiteral = null;
//    //            return;
//    //        }

//    //        AddedTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? RemovedTypesKeysLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<int> RemovedTypesKeys
//    //{
//    //    get => RemovedTypesKeysLiteral is null
//    //        ? []
//    //        : (RemovedTypesKeysLiteral
//    //            .Split(',')
//    //            .Select(v => int.TryParse(v, out int iv) ? (int)iv : (int?)null)
//    //            .Where(v => v is not null) as IEnumerable<int>)!
//    //            .ToList();
//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            RemovedTypesKeysLiteral = null;
//    //            return;
//    //        }

//    //        RemovedTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}

//    //public string? RemovedTypesSymbolsLiteral { get; set; } = null;
//    //[CgSQLiteIgnore]
//    //public List<string> RemovedTypesSymbols
//    //{
//    //    get => RemovedTypesSymbolsLiteral is null
//    //        ? []
//    //        : RemovedTypesSymbolsLiteral.Split(',').ToList();

//    //    set
//    //    {
//    //        if (value.Count == 0)
//    //        {
//    //            RemovedTypesSymbolsLiteral = null;
//    //            return;
//    //        }

//    //        RemovedTypesKeysLiteral = string.Join(',', value);
//    //    }
//    //}
//}


[CgSQLiteTable(tableName: "ElementTypeComparisons")]
[CgSQLiteIndex(nameof(SourceElementKey), nameof(TargetElementKey))]
public partial class DbElementTypeComparison : DbComparisonBase
{
    [CgSQLiteForeignKey(referenceTable: "ElementComparisons", referenceColumn: nameof(DbElementComparison.Key))]
    public required int ElementComparisonKey { get; set; }

    //[CgSQLiteForeignKey(referenceTable: "CollatedTypeComparisons", referenceColumn: nameof(DbCollatedTypeComparison.Key))]
    //public required int CollatedTypeComparisonKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int SourceElementKey { get; set; }

    public required string SourceElementId { get; set; }

    //[CgSQLiteForeignKey(referenceTable: "CollatedTypes", referenceColumn: nameof(DbCollatedType.Key))]
    //public required int SourceCollatedTypeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ElementTypes", referenceColumn: nameof(DbElementType.Key))]
    public required int SourceElementTypeKey { get; set; }

    [CgSQLiteIgnore]
    public override int SourceContentKey { get => this.SourceElementTypeKey; set => this.SourceElementTypeKey = value; }

    public required string SourceTypeLiteral { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int? TargetElementKey { get; set; }

    public required string? TargetElementId { get; set; }

    //[CgSQLiteForeignKey(referenceTable: "CollatedTypes", referenceColumn: nameof(DbCollatedType.Key))]
    //public required int? TargetCollatedTypeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ElementTypes", referenceColumn: nameof(DbElementType.Key))]
    public required int? TargetElementTypeKey { get; set; }

    [CgSQLiteIgnore]
    public override int? TargetContentKey { get => this.TargetElementTypeKey; set => this.TargetElementTypeKey = value; }

    public required string? TargetTypeLiteral { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TargetProfileRelationship { get; set; }
    public required string? TargetProfileMessage { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? TypeProfileRelationship { get; set; }
    public required string? TypeProfileMessage { get; set; }
}

public class DbComparisonCache<T>
    where T : DbComparisonBase
{
    private readonly Dictionary<int, T> _byKey = [];
    private readonly Dictionary<(int sourceKey, int? targetKey), T> _byPair = [];

    private readonly Dictionary<int, T> _toAdd = [];
    private readonly Dictionary<int, T> _toUpdate = [];
    private readonly Dictionary<int, T> _toDelete = [];

    public bool TryGet(int key, [NotNullWhen(true)] out T? value) => _byKey.TryGetValue(key, out value);
    public bool TryGet((int sourceKey, int? targetKey) pair, [NotNullWhen(true)] out T? value) => _byPair.TryGetValue(pair, out value);

    public bool Contains(int key) => _byKey.ContainsKey(key);
    public bool Contains((int sourceKey, int? targetKey) pair) => _byPair.ContainsKey(pair);

    public T? Get(int key) => _byKey.TryGetValue(key, out T? value) ? value : default(T);
    public T? Get(int sourceKey, int? targetKey) => _byPair.TryGetValue((sourceKey, targetKey), out T? value) ? value : default(T);

    public IEnumerable<T> ForSource(int sourceKey)
    {
        return _byPair
            .Where(kvp => kvp.Key.sourceKey == sourceKey)
            .Select(kvp => kvp.Value);
    }

    public IEnumerable<T> ForTarget(int targetKey)
    {
        return _byPair
            .Where(kvp => kvp.Key.targetKey == targetKey)
            .Select(kvp => kvp.Value);
    }

    public IEnumerable<T> Values => _byKey.Values;
    public int ValueCount => _byKey.Count;

    public IEnumerable<T> ToAdd => _toAdd.Values;
    public int ToAddCount => _toAdd.Count;

    public IEnumerable<T> ToUpdate => _toUpdate.Values;
    public int ToUpdateCount => _toUpdate.Count;

    public IEnumerable<T> ToDelete => _toDelete.Values;
    public int ToDeleteCount => _toDelete.Count;

    public void Clear()
    {
        _byKey.Clear();
        _byPair.Clear();
        _toAdd.Clear();
        _toUpdate.Clear();
        _toDelete.Clear();
    }

    public void CacheAdd(T item)
    {
        _byKey[item.Key] = item;
        _byPair[(item.SourceContentKey, item.TargetContentKey)] = item;
        _toAdd[item.Key] = item;
    }

    public void CacheUpdate(T item)
    {
        _byKey[item.Key] = item;
        _byPair[(item.SourceContentKey, item.TargetContentKey)] = item;
        _toUpdate[item.Key] = item;
    }

    public void CacheDelete(T item)
    {
        _byKey[item.Key] = item;
        _byPair[(item.SourceContentKey, item.TargetContentKey)] = item;
        _toDelete[item.Key] = item;
    }

    public int Count => _byKey.Count;
}
