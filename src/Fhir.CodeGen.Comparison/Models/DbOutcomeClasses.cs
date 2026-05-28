using System.Data;
using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.SQLiteGenerator;

namespace Fhir.CodeGen.Comparison.Models;

public enum OutcomeValueSetActionCodes
{
    /// <summary>
    /// The value set is used with the same name in the target version.
    /// </summary>
    UseValueSetSameName,

    /// <summary>
    /// The value set is used but has been renamed in the target version.
    /// </summary>
    UseValueSetRenamed,

    /// <summary>
    /// The value set has no valid mapping to the target version, so a cross-version defined value set must be used.
    /// </summary>
    UseCrossVersionDefinition,

    /// <summary>
    /// The value set exists in the target version with the same name, but additional concepts are required from a cross-version defined value set.
    /// </summary>
    UseSameNameAndCrossVersion,

    /// <summary>
    /// The value set has been renamed in the target version, and additional concepts are required from a cross-version defined value set.
    /// </summary>
    UseRenamedAndCrossVersion,

    /// <summary>
    /// The value set has other mappings that account for all concepts, but this target is explicitly not mapped.
    /// </summary>
    UseOtherValueSets,

    /// <summary>
    /// The value set has other mappings, but this target is explicitly not mapped and there are concepts not covered.
    /// </summary>
    UseOtherAndCrossVersion,
}

public enum OutcomeValueSetConceptActionCodes
{


    /// <summary>
    /// The concept is used with the same code in the target version.
    /// </summary>
    UseConceptSameCode,

    /// <summary>
    /// The concept is used but has been changed to a different code in the target version.
    /// </summary>
    UseConceptChangedCode,

    /// <summary>
    /// The concept has no valid mapping to the target version.
    /// </summary>
    UnmappedConcept,

    /// <summary>
    /// The concept has no valid mapping to the target version, so a cross-version defined concept must be used.
    /// </summary>
    UseCrossVersionDefinition,

    /// <summary>
    /// The concept is mapped to a different value set in the target version.
    /// </summary>
    MappedElsewhere,

    UseCodeAndCrossVersion,
    UseOneOfMultipleCodes,
}

public enum OutcomeStructureActionCodes
{
    /// <summary>
    /// The structure is used with the same name in the target version.
    /// </summary>
    UseStructureSameName,
    /// <summary>
    /// The structure is used but has been renamed in the target version.
    /// </summary>
    UseStructureRenamed,
    /// <summary>
    /// The structure has no valid mapping to the target version, so a Basic resource must be used.
    /// </summary>
    UseBasicResource,
    /// <summary>
    /// The structure has no valid mapping to the target version, so a generated datatype extension must be used.
    /// </summary>
    UseDatatypeExtension,
    ///// <summary>
    ///// The structure is mapped to one of several possible structures in the target version.
    ///// </summary>
    //UseOneOf,
}

public enum OutcomeElementActionCodes
{
    /// <summary>
    /// The element is used with the same name in the target version.
    /// </summary>
    UseElementSameName,
    /// <summary>
    /// The element is used but has been renamed in the target version.
    /// </summary>
    UseElementRenamed,
    /// <summary>
    /// The element is represented as an extension in the target version.
    /// </summary>
    UseExtension,
    /// <summary>
    /// The element is represented as an extension inherited from an ancestor.
    /// </summary>
    UseExtensionFromAncestor,
    /// <summary>
    /// The element is mapped to a basic element in the target version.
    /// </summary>
    UseBasicElement,
    ///// <summary>
    ///// The element is mapped to one of several possible elements and possibly an extension in the target version.
    ///// </summary>
    //UseOneOf,
    /// <summary>
    /// The element has no valid mapping and cannot have an extension.
    /// </summary>
    Unresolved,
    /// <summary>
    /// The element represents an extension in the source version, so nothing is generated.
    /// </summary>
    IsExtension,
    /// <summary>
    /// The element represents an Element.id, so nothing is generated.
    /// </summary>
    IsElementId,
    /// <summary>
    /// The concept is mapped to a different element in the target version.
    /// </summary>
    MappedElsewhere,
}

public static class DbOutcomeClasses
{
    public static void LoadIndices(IDbConnection db)
    {
        try { DbValueSetOutcome.LoadMaxKey(db); } catch { }
        try { DbValueSetConceptOutcome.LoadMaxKey(db); } catch { }
        try { DbStructureOutcome.LoadMaxKey(db); } catch { }
        try { DbElementOutcome.LoadMaxKey(db); } catch { }
        try { DbElementOutcomeTarget.LoadMaxKey(db); } catch { }
    }

    public static void DropTables(
        IDbConnection db,
        bool forValueSets = true,
        bool forStructures = true)
    {
        if (forValueSets)
        {
            DbValueSetOutcome.DropTable(db);
            DbValueSetConceptOutcome.DropTable(db);
        }

        if (forStructures)
        {
            DbStructureOutcome.DropTable(db);
            DbElementOutcome.DropTable(db);
            DbElementOutcomeTarget.DropTable(db);
        }
    }

    public static void CreateTables(
        IDbConnection db,
        bool forValueSets = true,
        bool forStructures = true)
    {
        if (forValueSets)
        {
            DbValueSetOutcome.CreateTable(db);
            DbValueSetConceptOutcome.CreateTable(db);
        }

        if (forStructures)
        {
            DbStructureOutcome.CreateTable(db);
            DbElementOutcome.CreateTable(db);
            DbElementOutcomeTarget.CreateTable(db);
        }
    }
}

[CgSQLiteBaseClass]
public abstract class DbOutcomeBase : DbRecordBase
{
    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int SourceFhirPackageKey { get; set; }
    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence { get; set; }

    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int TargetFhirPackageKey { get; set; }
    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence { get; set; }

    public required bool RequiresXVerDefinition { get; set; }

    /// <summary>
    /// Number of artifacts that this artifact maps *to*:
    /// - 0: no target (has no equivalent artifact)
    /// - 1: single target (e.g., R4.Patient -> R5.Patient)
    /// - 2: two targets (e.g., R2.Practitioner -> R3.Practitioner + R3.PractitionerRole)
    /// </summary>
    public required int TotalTargetCount { get; set; }

    /// <summary>
    /// Number of artifacts that map *to* this artifact:
    /// - 0: no source (has no equivalent artifact)
    /// - 1: single source (e.g., R4.Patient -> R5.Patient)
    /// - 2: two sources (e.g., R3.Practitioner + R3.PractitionerRole -> R2.Practitioner)
    /// </summary>
    public required int TotalSourceCount { get; set; }

    /// <summary>
    /// If the mapping is an explicitly-tracked rename
    /// </summary>
    public required bool IsRenamed { get; set; }

    public required bool IsUnmapped { get; set; }
    public required bool IsIdentical { get; set; }
    public required bool IsEquivalent { get; set; }
    public required bool IsBroaderThanTarget { get; set; }
    public required bool IsNarrowerThanTarget { get; set; }

    public required bool FullyMapsToThisTarget { get; set; }
    public required bool FullyMapsAcrossAllTargets { get; set; }

    public required string Comments { get; set; }


    //[CgSQLiteIgnore]
    //public virtual int ComparisonKey { get; set; }

    //[CgSQLiteIgnore]
    //public virtual int SourceContentKey { get; set; }

    //[CgSQLiteIgnore]
    //public virtual int? TargetContentKey { get; set; }
}

[CgSQLiteBaseClass]
public abstract class DbArtifactOutcomeBase : DbOutcomeBase
{
    //public required string? PotentialGenResourceType { get; set; }
    public required string? GenLongId { get; set; }
    public required string? GenShortId { get; set; }
    public required string? GenUrl { get; set; }
    public required string? GenName { get; set; }
    public required string? GenFileName { get; set; }

    public required string? GenArtifactShort { get; set; }
    public required string? GenArtifactDescription { get; set; }
    public required string? GenArtifactComment { get; set; }
    public required string? GenMappingComment { get; set; }

    public required string SourceCanonicalVersioned { get; set; }
    public required string SourceCanonicalUnversioned { get; set; }
    public required string SourceId { get; set; }
    public required string SourceName { get; set; }
    public string SourceNameClean() => SourceName.EndsWith("[x]", StringComparison.Ordinal)
        ? SourceName[..^3]
        : SourceName;
    public required string SourceVersion { get; set; }
}

[CgSQLiteBaseClass]
public abstract class DbArtifactOutcomeWithTargetBase : DbArtifactOutcomeBase
{
    public required string? TargetCanonicalVersioned { get; set; }
    public required string? TargetCanonicalUnversioned { get; set; }
    public required string? TargetId { get; set; }
    public required string? TargetName { get; set; }
    public string? TargetNameClean() => TargetName is null
        ? null
        : TargetName.EndsWith("[x]", StringComparison.Ordinal)
        ? TargetName[..^3]
        : TargetName;
    public required string? TargetVersion { get; set; }
}

[CgSQLiteTable(tableName: "ValueSetOutcomes")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceValueSetKey), nameof(TargetFhirPackageKey), nameof(RequiresXVerDefinition))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(GenLongId), nameof(RequiresXVerDefinition))]
public partial class DbValueSetOutcome : DbArtifactOutcomeWithTargetBase
{
    [CgSQLiteForeignKey(referenceTable: "ValueSetComparisons", referenceColumn: nameof(DbValueSetComparison.Key))]
    public required int? ValueSetComparisonKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int ComparisonKey { get => this.ValueSetComparisonKey; set => this.ValueSetComparisonKey = value; }


    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int SourceValueSetKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int SourceContentKey { get => this.SourceValueSetKey; set => this.SourceValueSetKey = value; }


    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int? TargetValueSetKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int? TargetContentKey { get => this.TargetValueSetKey; set => this.TargetValueSetKey = value; }


    public required string? ConceptMapLongId { get; set; }
    public required string? ConceptMapShortId { get; set; }
    public required string? ConceptMapUrl { get; set; }
    public required string? ConceptMapName { get; set; }
    public required string? ConceptMapFileName { get; set; }
}

[CgSQLiteTable(tableName: "ValueSetConceptOutcomes")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey))]
[CgSQLiteIndex(nameof(ValueSetOutcomeKey), nameof(RequiresXVerDefinition), nameof(SourceSystem), nameof(SourceCode))]
[CgSQLiteIndex(nameof(ValueSetOutcomeKey), nameof(SourceSystem), nameof(SourceCode), nameof(TargetSystem), nameof(TargetCode))]
public partial class DbValueSetConceptOutcome : DbOutcomeBase
{
    [CgSQLiteForeignKey(referenceTable: "ValueSetOutcomes", referenceColumn: nameof(DbValueSetOutcome.Key))]
    public required int ValueSetOutcomeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConceptComparisons", referenceColumn: nameof(DbValueSetConceptComparison.Key))]
    public required int? ValueSetConceptComparisonKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int ComparisonKey { get => this.ValueSetConceptComparisonKey; set => this.ValueSetConceptComparisonKey = value; }


    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int SourceValueSetKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConcepts", referenceColumn: nameof(DbValueSetConcept.Key))]
    public required int SourceValueSetConceptKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int SourceContentKey { get => this.SourceValueSetConceptKey; set => this.SourceValueSetConceptKey = value; }

    [CgSQLiteForeignKey(referenceTable: "ValueSets", referenceColumn: nameof(DbValueSet.Key))]
    public required int? TargetValueSetKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ValueSetConcepts", referenceColumn: nameof(DbValueSetConcept.Key))]
    public required int? TargetValueSetConceptKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int? TargetContentKey { get => this.TargetValueSetConceptKey; set => this.TargetValueSetConceptKey = value; }

    public required string SourceSystem { get; set; }
    public required string SourceSystemVersion { get; set; }
    public required string SourceCode { get; set; }
    public required string? SourceDisplay { get; set; }

    public required string? TargetSystem { get; set; }
    public required string? TargetSystemVersion { get; set; }
    public required string? TargetCode { get; set; }
    public required string? TargetDisplay { get; set; }
}


[CgSQLiteTable(tableName: "StructureOutcomes")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(TargetFhirPackageKey), nameof(TargetStructureKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(TargetFhirPackageKey), nameof(RequiresXVerDefinition))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceName), nameof(TargetFhirPackageKey), nameof(TargetName))]
public partial class DbStructureOutcome : DbArtifactOutcomeWithTargetBase
{
    [CgSQLiteForeignKey(referenceTable: "StructureComparisons", referenceColumn: nameof(DbStructureComparison.Key))]
    public required int? StructureComparisonKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int ComparisonKey { get => this.StructureComparisonKey ?? -1; set => this.StructureComparisonKey = value; }


    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int SourceStructureKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int SourceContentKey { get => this.SourceStructureKey; set => this.SourceStructureKey = value; }

    public required Fhir.CodeGen.Common.Models.FhirArtifactClassEnum SourceArtifactClass { get; set; } = Fhir.CodeGen.Common.Models.FhirArtifactClassEnum.Unknown;

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key))]
    public required int? TargetStructureKey { get; set; }

    public required Fhir.CodeGen.Common.Models.FhirArtifactClassEnum? TargetArtifactClass { get; set; } = null;


    public required string? ElementConceptMapLongId { get; set; }
    public required string? ElementConceptMapShortId { get; set; }
    public required string? ElementConceptMapUrl { get; set; }
    public required string? ElementConceptMapName { get; set; }
    public required string? ElementConceptMapFileName { get; set; }
}

[CgSQLiteTable(tableName: "ElementOutcomes")]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(SourceElementKey), nameof(TargetFhirPackageKey))]
[CgSQLiteIndex(nameof(SourceElementKey), nameof(TargetFhirPackageKey))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(SourceStructureKey), nameof(SourceResourceOrder))]
[CgSQLiteIndex(nameof(SourceFhirPackageKey), nameof(TargetFhirPackageKey), nameof(SourceStructureKey), nameof(SourceResourceOrder))]
[CgSQLiteIndex(
    nameof(SourceFhirPackageKey),
    nameof(TargetFhirPackageKey),
    nameof(RequiresXVerDefinition),
    nameof(RequiresExtensionDefinition),
    nameof(RequiresSliceDefinition),
    nameof(ExtensionSubstitutionKey),
    nameof(ParentElementOutcomeKey),
    nameof(SourceStructureKey),
    nameof(SourceResourceOrder))]
[CgSQLiteIndex(nameof(ParentElementOutcomeKey), nameof(RequiresXVerDefinition), nameof(SourceResourceOrder))]
[CgSQLiteIndex(
    nameof(SourceFhirPackageKey),
    nameof(TargetFhirPackageKey),
    nameof(SourceUsedAsContentReference),
    nameof(SourceAncestorUsedAsContentReferenceId),
    nameof(RequiresXVerDefinition),
    nameof(RequiresExtensionDefinition),
    nameof(RequiresSliceDefinition))]
public partial class DbElementOutcome : DbArtifactOutcomeBase
{
    //[CgSQLiteForeignKey(referenceTable: "StructureOutcomes", referenceColumn: nameof(DbStructureOutcome.Key), modelTypeName: nameof(DbStructureOutcome))]
    //public required int StructureOutcomeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key), modelTypeName: nameof(DbStructureDefinition))]
    public required int SourceStructureKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key), modelTypeName: nameof(DbElement))]
    public required int SourceElementKey { get; set; }
    //[CgSQLiteIgnore]
    //public override int SourceContentKey { get => this.SourceElementKey; set => this.SourceElementKey = value; }

    public required bool RequiresExtensionDefinition { get; set; }
    public required bool RequiresSliceDefinition { get; set; }
    public required string? GenSliceName { get; set; }

    public required int SourceResourceOrder { get; set; }
    public required int SourceComponentOrder { get; set; }
    public required int SourceMinCardinality { get; set; }
    public required string SourceMaxCardinalityString { get; set; }
    public required int SourceChildElementCount { get; set; }
    public required bool SourceUsedAsContentReference { get; set; }
    public required string? SourceAncestorUsedAsContentReferenceId { get; set; }
    public required int? SourceAncestorContentReferenceOutcomeKey { get; set; }

    public string? AlternateCanonicalTargetsLiteral { get; set; }
    [CgSQLiteIgnore]
    public List<string> AlternateCanonicalTargets
    {
        get => AlternateCanonicalTargetsLiteral is null
            ? []
            : AlternateCanonicalTargetsLiteral.Split(',').ToList();

        set
        {
            if (value.Count == 0)
            {
                AlternateCanonicalTargetsLiteral = null;
                return;
            }

            AlternateCanonicalTargetsLiteral = string.Join(',', value);
        }
    }

    public string? AlternateCanonicalContextLiteral { get; set; }
    [CgSQLiteIgnore]
    public List<string> AlternateCanonicalContexts
    {
        get => AlternateCanonicalContextLiteral is null
            ? []
            : AlternateCanonicalContextLiteral.Split(',').ToList();

        set
        {
            if (value.Count == 0)
            {
                AlternateCanonicalContextLiteral = null;
                return;
            }

            AlternateCanonicalContextLiteral = string.Join(',', value);
        }
    }

    public string? AlternateReferenceTargetsLiteral { get; set; }
    [CgSQLiteIgnore]
    public List<string> AlternateReferenceTargets
    {
        get => AlternateReferenceTargetsLiteral is null
            ? []
            : AlternateReferenceTargetsLiteral.Split(',').ToList();

        set
        {
            if (value.Count == 0)
            {
                AlternateReferenceTargetsLiteral = null;
                return;
            }

            AlternateReferenceTargetsLiteral = string.Join(',', value);
        }
    }

    public string? AlternateReferenceContextLiteral { get; set; }
    [CgSQLiteIgnore]
    public List<string> AlternateReferenceContexts
    {
        get => AlternateReferenceContextLiteral is null
            ? []
            : AlternateReferenceContextLiteral.Split(',').ToList();

        set
        {
            if (value.Count == 0)
            {
                AlternateReferenceContextLiteral = null;
                return;
            }

            AlternateReferenceContextLiteral = string.Join(',', value);
        }
    }

    [CgSQLiteForeignKey(referenceTable: "ElementOutcomes", referenceColumn: nameof(Key), modelTypeName: nameof(DbElementOutcome))]
    public required int? AncestorElementOutcomeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ElementOutcomes", referenceColumn: nameof(Key), modelTypeName: nameof(DbElementOutcome))]
    public required int? ParentElementOutcomeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ExtensionSubstitutions", referenceColumn: nameof(Key), modelTypeName: nameof(DbExtensionSubstitution))]
    public required int? ExtensionSubstitutionKey { get; set; }
    public required string? ExtensionSubstitutionUrl { get; set; }
    public required string? BasicElementBaseId { get; set; }
    public required string? BasicElementId { get; set; }

    public required string? ExtensionElementBaseId { get; set; }
    public required string? ExtensionElementId { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ElementOutcomes", referenceColumn: nameof(Key), modelTypeName: nameof(DbElementOutcome))]
    public required int? ContentReferenceOutcomeKey { get; set; }
    public required string? ContentReferenceExtensionUrl { get; set; }
    public required bool? ContentReferenceRequiresXVerDefinition { get; set; }
    public required string? ContentReferenceAncestorId { get; set; }
    public required bool? RequiresDefinitionAsContentReference { get; set; }

    public required bool ExtensionDefinitionIsProhibited { get; set; }
    public required string? ExtensionProhibitionReason { get; set; }

    public required int? OutcomeTargetCount { get; set; }

    public required bool SourceIsModifier { get; set; }
    public required bool DefineAsModifier { get; set; }
    public required string? ModifierReason { get; set; }
    public required bool SourceIsDeprecated { get; set; }

    public string? ExtensionContextsLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<string> ExtensionContexts
    {
        get => ExtensionContextsLiteral is null
            ? []
            : ExtensionContextsLiteral.Split(',').ToList();

        set
        {
            if (value.Count == 0)
            {
                ExtensionContextsLiteral = null;
                return;
            }

            ExtensionContextsLiteral = string.Join(',', value);
        }
    }

    public string? MappedTypeKeysLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<int> MappedTypeKeys
    {
        get => MappedTypeKeysLiteral is null
            ? []
            : MappedTypeKeysLiteral.Split(',').Select(x => int.Parse(x)).ToList();
        set
        {
            if (value.Count == 0)
            {
                MappedTypeKeysLiteral = null;
                return;
            }
            MappedTypeKeysLiteral = string.Join(',', value);
        }
    }

    public string? MappedTypeNamesLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<string> MappedTypeNames
    {
        get => MappedTypeNamesLiteral is null
            ? []
            : MappedTypeNamesLiteral.Split(',').ToList();
        set
        {
            if (value.Count == 0)
            {
                MappedTypeNamesLiteral = null;
                return;
            }
            MappedTypeNamesLiteral = string.Join(',', value);
        }
    }

    public string? UnmappedTypeKeysLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<int> UnmappedTypeKeys
    {
        get => UnmappedTypeKeysLiteral is null
            ? []
            : UnmappedTypeKeysLiteral.Split(',').Select(x => int.Parse(x)).ToList();
        set
        {
            if (value.Count == 0)
            {
                UnmappedTypeKeysLiteral = null;
                return;
            }
            UnmappedTypeKeysLiteral = string.Join(',', value);
        }
    }

    public string? UnmappedTypeNamesLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<string> UnmappedTypeNames
    {
        get => UnmappedTypeNamesLiteral is null
            ? []
            : UnmappedTypeNamesLiteral.Split(',').ToList();
        set
        {
            if (value.Count == 0)
            {
                UnmappedTypeNamesLiteral = null;
                return;
            }
            UnmappedTypeNamesLiteral = string.Join(',', value);
        }
    }

    public string? MappedTypeChildElementKeysLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<int> MappedTypeChildElementKeys
    {
        get => MappedTypeChildElementKeysLiteral is null
            ? []
            : MappedTypeChildElementKeysLiteral.Split(',').Select(x => int.Parse(x)).ToList();
        set
        {
            if (value.Count == 0)
            {
                MappedTypeChildElementKeysLiteral = null;
                return;
            }
            MappedTypeChildElementKeysLiteral = string.Join(',', value);
        }
    }

    public string? MappedChildTypeElementNamesLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<string> MappedChildTypeElementNames
    {
        get => MappedChildTypeElementNamesLiteral is null
            ? []
            : MappedChildTypeElementNamesLiteral.Split(',').ToList();
        set
        {
            if (value.Count == 0)
            {
                MappedChildTypeElementNamesLiteral = null;
                return;
            }
            MappedChildTypeElementNamesLiteral = string.Join(',', value);
        }
    }

    public string? UnmappedTypeChildElementKeysLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<int> UnmappedTypeChildKeys
    {
        get => UnmappedTypeChildElementKeysLiteral is null
            ? []
            : UnmappedTypeChildElementKeysLiteral.Split(',').Select(x => int.Parse(x)).ToList();
        set
        {
            if (value.Count == 0)
            {
                UnmappedTypeChildElementKeysLiteral = null;
                return;
            }
            UnmappedTypeChildElementKeysLiteral = string.Join(',', value);
        }
    }

    public string? UnmappedChildTypeElementNamesLiteral { get; set; } = null;
    [CgSQLiteIgnore]
    public List<string> UnmappedChildTypeNames
    {
        get => UnmappedChildTypeElementNamesLiteral is null
            ? []
            : UnmappedChildTypeElementNamesLiteral.Split(',').ToList();
        set
        {
            if (value.Count == 0)
            {
                UnmappedChildTypeElementNamesLiteral = null;
                return;
            }
            UnmappedChildTypeElementNamesLiteral = string.Join(',', value);
        }
    }

    //public bool NeedsExtensionDefinition()
    //{
    //    if (BasicElementBaseId is not null)
    //    {
    //        return false;
    //    }

    //    if (RequiresXVerDefinition &&
    //        (ExtensionSubstitutionKey is null) &&
    //        (ContentReferenceExtensionUrl is null) &&
    //        (ParentRequiresXverDefinition != true))
    //    {
    //        return true;
    //    }

    //    if (RequiresDefinitionAsContentReference == true)
    //    {
    //        return true;
    //    }

    //    if (RequiresDefinitionForGroupRepetitions == true)
    //    {
    //        return true;
    //    }

    //    return false;
    //}
}

[CgSQLiteTable(tableName: "ElementOutcomeTargets")]
[CgSQLiteIndex(nameof(ElementOutcomeKey), nameof(StructureOutcomeKey))]
[CgSQLiteIndex(nameof(ElementOutcomeKey), nameof(TargetStructureKey), nameof(TargetElementId))]
[CgSQLiteIndex(nameof(ElementOutcomeKey), nameof(TargetElementId), nameof(ContextElementId))]
public partial class DbElementOutcomeTarget : DbRecordBase
{
    [CgSQLiteForeignKey(referenceTable: "ElementOutcomes", referenceColumn: nameof(DbElementOutcome.Key), modelTypeName: nameof(DbElementOutcome))]
    public required int ElementOutcomeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "StructureOutcomes", referenceColumn: nameof(DbStructureOutcome.Key), modelTypeName: nameof(DbStructureOutcome))]
    public required int StructureOutcomeKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "ElementComparisons", referenceColumn: nameof(DbElementComparison.Key))]
    public required int? ElementComparisonKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int SourceFhirPackageKey { get; set; }
    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes SourceFhirSequence { get; set; }

    [CgSQLiteForeignKey(referenceTable: "FhirPackages", referenceColumn: nameof(DbFhirPackage.Key))]
    public required int TargetFhirPackageKey { get; set; }
    public required Fhir.CodeGen.Common.Packaging.FhirReleases.FhirSequenceCodes TargetFhirSequence { get; set; }

    public required bool FullyMapsToThisTarget { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Structures", referenceColumn: nameof(DbStructureDefinition.Key), modelTypeName: nameof(DbStructureDefinition))]
    public required int? TargetStructureKey { get; set; }

    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key), modelTypeName: nameof(DbElement))]
    public required int? TargetElementKey { get; set; }
    public required string? TargetElementId { get; set; }

    public required int? TargetResourceOrder { get; set; }
    public required int? TargetComponentOrder { get; set; }

    public required int? TargetMinCardinality { get; set; }
    public required string? TargetMaxCardinalityString { get; set; }

    public required bool? TargetIsModifier { get; set; }


    [CgSQLiteForeignKey(referenceTable: "Elements", referenceColumn: nameof(DbElement.Key))]
    public required int? ContextElementKey { get; set; }
    public required string? ContextElementId { get; set; }
    public required string? ContextRootExtensionUrl { get; set; }
    public required string? ContextParentExtensionUrl { get; set; }

    public required string? Comments { get; set; }
}

