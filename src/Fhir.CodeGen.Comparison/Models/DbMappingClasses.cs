using Fhir.CodeGen.SQLiteGenerator;
using System.Diagnostics.CodeAnalysis;
using System.Data;


namespace Fhir.CodeGen.Comparison.Models;

public static class DbMappingClasses
{
    public static void LoadIndices(IDbConnection db)
    {
        try { DbMappingSourceFile.LoadMaxKey(db); } catch { }
        try { DbValueSetMapping.LoadMaxKey(db); } catch { }
        try { DbValueSetConceptMapping.LoadMaxKey(db); } catch { }
        try { DbStructureMapping.LoadMaxKey(db); } catch { }
        try { DbElementMapping.LoadMaxKey(db); } catch { }
        try { DbUnsafeCodeElementAnnotation.LoadMaxKey(db); } catch { }
        try { DbUnsafeCodeMapEvidence.LoadMaxKey(db); } catch { }
        try { DbUnsafeCodeRule.LoadMaxKey(db); } catch { }
        try { DbUnsafeCodeInputState.LoadMaxKey(db); } catch { }
        try { DbUnsafeCodePairState.LoadMaxKey(db); } catch { }
    }

    public static void DropTables(IDbConnection db)
    {
        UnsafeCodeInputStore.InvalidateInputs(db);
        DbUnsafeCodeElementAnnotation.DropTable(db);
        DbUnsafeCodeMapEvidence.DropTable(db);
        DbUnsafeCodeRule.DropTable(db);
        DbUnsafeCodePairState.DropTable(db);
        DbUnsafeCodeInputState.DropTable(db);
        DbMappingSourceFile.DropTable(db);
        DbValueSetMapping.DropTable(db);
        DbValueSetConceptMapping.DropTable(db);
        DbStructureMapping.DropTable(db);
        DbElementMapping.DropTable(db);
    }

    public static void CreateTables(IDbConnection db)
    {
        DbMappingSourceFile.CreateTable(db);
        DbValueSetMapping.CreateTable(db);
        DbValueSetConceptMapping.CreateTable(db);
        DbStructureMapping.CreateTable(db);
        DbElementMapping.CreateTable(db);
        DbUnsafeCodeElementAnnotation.CreateTable(db);
        DbUnsafeCodeMapEvidence.CreateTable(db);
        DbUnsafeCodeRule.CreateTable(db);
        UnsafeCodeInputStore.EnsureStateTables(db);
    }
}

[CgSQLiteTable(tableName: "MappingSourceFiles")]
[CgSQLiteIndex(nameof(SourceRelativeFilename))]
public partial class DbMappingSourceFile : DbRecordBase
{
    public required string Filename { get; set; }
    public required string SourceRelativeFilename { get; set; }
    public required bool IsConceptMap { get; set; }
    public required bool IsFml { get; set; }

    public required string Url { get; set; }
}


[CgSQLiteBaseClass]
public abstract class DbMappingBase : DbRecordBase
{
    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int SourceFhirPackageKey { get; set; }

    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence { get; set; }

    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int TargetFhirPackageKey { get; set; }

    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence { get; set; }

    [CgSQLiteForeignKey(referenceTable: "MappingSourceFiles", referenceColumn: nameof(DbMappingSourceFile.Key))]
    public required int? ConceptMapSourceKey { get; set; } = null;
    public required string? ConceptMapFilename { get; set; }


    [CgSQLiteForeignKey(referenceTable: "MappingSourceFiles", referenceColumn: nameof(DbMappingSourceFile.Key))]
    public required int? FmlSourceKey { get; set; } = null;
    public required string? FmlFilename { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? Relationship { get; set; }

    public required bool ExplicitNoMap { get; set; }

    public string? Comments { get; set; } = null;
    public string? TechnicalNotes { get; set; } = null;
}

[CgSQLiteBaseClass]
public abstract class DbMappingArtifactBase : DbMappingBase
{
}

[CgSQLiteTable(tableName: "ValueSetMappings")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceValueSetKey), nameof(TargetFhirPackageKey), nameof(TargetValueSetKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetValueSetKey), nameof(TargetValueSetKey), nameof(Key))]
[CgSQLiteIndex(nameof(SourceValueSetKey), nameof(TargetValueSetKey))]
public partial class DbValueSetMapping : DbMappingArtifactBase
{
    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int SourceValueSetKey { get; set; }
    public required string SourceValueSetId { get; set; }
    public required string SourceValueSetUrl { get; set; }
    public required string SourceValueSetVersion { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int? TargetValueSetKey { get; set; }
    public required string? TargetValueSetId { get; set; }
    public required string? TargetValueSetUrl { get; set; }
    public required string? TargetValueSetVersion { get; set; }
}

[CgSQLiteTable(tableName: "ValueSetConceptMappings")]
[CgSQLiteIndex(nameof(ValueSetMappingKey), nameof(SourceValueSetConceptKey), nameof(TargetValueSetConceptKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetValueSetConceptKey), nameof(Key))]
[CgSQLiteIndex(nameof(SourceValueSetKey), nameof(TargetValueSetKey))]
public partial class DbValueSetConceptMapping : DbMappingBase
{
    [CgSQLiteForeignKey(referenceTable: "ValueSetMappings", referenceColumn: nameof(DbValueSetMapping.Key))]
    public required int ValueSetMappingKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int SourceValueSetKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConcepts", referenceColumn: nameof(DbValueSetConcept.Key))]
    public required int SourceValueSetConceptKey { get; set; }

    public required string SourceSystem { get; set; }
    public required string SourceCode { get; set; }


    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int? TargetValueSetKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConcepts", referenceColumn: nameof(DbValueSetConcept.Key))]
    public required int? TargetValueSetConceptKey { get; set; }

    public required string? TargetSystem { get; set; }
    public required string? TargetCode { get; set; }
}


[CgSQLiteTable(tableName: "StructureMappings")]
[CgSQLiteIndex(nameof(IsFallback), nameof(SourceStructureKey), nameof(TargetStructureKey))]
[CgSQLiteIndex(nameof(IsFallback), nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(TargetFhirPackageKey), nameof(TargetStructureKey))]
[CgSQLiteIndex(nameof(IsFallback), nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(TargetFhirPackageKey), nameof(TargetStructureId))]
[CgSQLiteIndex(nameof(IsFallback), nameof(SourceFhirPackageKey), nameof(SourceStructureId), nameof(TargetFhirPackageKey), nameof(TargetStructureId))]
[CgSQLiteIndex(nameof(IsFallback), nameof(SourceFhirPackageKey), nameof(SourceStructureUrl), nameof(TargetFhirPackageKey), nameof(TargetStructureUrl))]
public partial class DbStructureMapping : DbMappingArtifactBase
{
    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int SourceStructureKey { get; set; }
    public required string SourceStructureId { get; set; }
    public required string SourceStructureUrl { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int? TargetStructureKey { get; set; }
    public required string? TargetStructureId { get; set; }
    public required string? TargetStructureUrl { get; set; }

    public required bool IsFallback { get; set; }

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship { get; set; }
    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship { get; set; }
}


[CgSQLiteTable(tableName: "ElementMappings")]
[CgSQLiteIndex(nameof(StructureMappingKey), nameof(SourceElementKey), nameof(TargetElementKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceElementId), nameof(TargetElementId))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceElementId), nameof(TargetFhirPackageKey), nameof(TargetStructureKey))]
public partial class DbElementMapping : DbMappingBase
{
    [CgSQLiteForeignKey(referenceTable: "StructureMappings", referenceColumn: nameof(DbStructureMapping.Key))]
    public required int? StructureMappingKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int SourceStructureKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int? SourceElementKey { get; set; }

    public required string SourceElementId { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int? TargetStructureKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int? TargetElementKey { get; set; }

    public required string? TargetElementId { get; set; }

    //[CgSQLiteForeignKey(referenceTable: "MappingSourceFiles", referenceColumn: nameof(DbMappingSourceFile.Key))]
    //public required int? ConceptMapSourceKey { get; set; } = null;

    //[CgSQLiteForeignKey(referenceTable: "MappingSourceFiles", referenceColumn: nameof(DbMappingSourceFile.Key))]
    //public required int? FmlSourceKey { get; set; } = null;

    public required bool? FmlIsSimpleCopy { get; set; } = null;

    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ConceptDomainRelationship { get; set; }
    public required Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship? ValueDomainRelationship { get; set; }
}
