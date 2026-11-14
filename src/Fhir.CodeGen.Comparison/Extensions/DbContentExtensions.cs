// <copyright file="DbContentExtensions.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using Hl7.Fhir.Model;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.Models;

namespace Fhir.CodeGen.Comparison.Extensions;

public static class DbContentExtensions
{
    public static bool ElementHasNonPrimitiveTypes(
        this IDbConnection dbConnection,
        DbElement element,
        string? elementTypeTableName = null,
        string? structureTypeTableName = null)
    {
        elementTypeTableName ??= DbElementType.DefaultTableName;
        structureTypeTableName ??= DbStructureDefinition.DefaultTableName;

        IDbCommand command = dbConnection.CreateCommand();
        command.CommandText = $$$"""
            SELECT COUNT({{{nameof(DbElementType.Key)}}})
            FROM {{{elementTypeTableName}}}
            WHERE {{{nameof(DbElementType.ElementKey)}}} = {{{element.Key}}}
            AND {{{nameof(DbElementType.TypeName)}}} NOT IN (
                SELECT DISTINCT {{{nameof(DbStructureDefinition.Id)}}}
                FROM {{{structureTypeTableName}}}
                WHERE {{{nameof(DbStructureDefinition.FhirPackageKey)}}} = {{{element.FhirPackageKey}}}
                AND {{{nameof(DbStructureDefinition.ArtifactClass)}}} = 'PrimitiveType'
            )
            """;

        int count = Convert.ToInt32(command.ExecuteScalar());

        return count > 0;
    }

    internal static string? ProcessCoreTextForLinks(
        this string? input,
        string fhirVersionLiteral)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // Process special markdown links first, then HTML links, then core links.
        string? output = FhirSanitizationUtils.ProcessFhirSpecialMdLinks(input, fhirVersionLiteral);
        output = FhirSanitizationUtils.ProcessEmbeddedHtmlLinks(output);
        output = FhirSanitizationUtils.ProcessFhirCoreLinks(output, fhirVersionLiteral);

        return output;
    }

    internal static Narrative? ProcessCoreTextForLinks(this Narrative? input, string fhirVersionLiteral)
    {
        if ((input == null) || string.IsNullOrEmpty(input.Div))
        {
            return input;
        }

        // Process special markdown links first, then HTML links, then core links.
        string? output = FhirSanitizationUtils.ProcessFhirSpecialMdLinks(input.Div, fhirVersionLiteral);
        output = FhirSanitizationUtils.ProcessEmbeddedHtmlLinks(output);
        output = FhirSanitizationUtils.ProcessFhirCoreLinks(output, fhirVersionLiteral);

        return new Narrative
        {
            Status = input.Status,
            Div = output,
        };
    }

    public static int ConceptCountWithLiteralMatches(
        this IDbConnection dbConnection,
        int vsKeyA,
        int vsKeyB,
        string? conceptTableName = null)
    {
        conceptTableName ??= DbValueSetConcept.DefaultTableName;

        IDbCommand command = dbConnection.CreateCommand();
        command.CommandText = $$$"""
            SELECT COUNT(C1.{{{nameof(DbValueSetConcept.Key)}}})
            FROM {{{conceptTableName}}} C1
            JOIN {{{conceptTableName}}} C2 ON C1.Code = C2.Code
            WHERE C1.{{{nameof(DbValueSetConcept.ValueSetKey)}}} = {{{vsKeyA}}}
            AND C2.{{{nameof(DbValueSetConcept.ValueSetKey)}}} = {{{vsKeyB}}}
            """;

        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static int ElementCountThatLookIdentical(
        this IDbConnection dbConnection,
        int structureKeyA,
        int structureKeyB,
        string? elementTableName = null)
    {
        elementTableName ??= DbElement.DefaultTableName;

        IDbCommand command = dbConnection.CreateCommand();
        command.CommandText = $$$"""
            SELECT COUNT(E1.{{{nameof(DbElement.Key)}}})
            FROM {{{elementTableName}}} E1
            JOIN {{{elementTableName}}} E2 ON E1.Id = E2.Id
            WHERE E1.{{{nameof(DbElement.StructureKey)}}} = {{{structureKeyA}}}
            AND E2.{{{nameof(DbElement.StructureKey)}}} = {{{structureKeyB}}}
            AND (
                E1.{{{nameof(DbElement.MinCardinality)}}} != E2.{{{nameof(DbElement.MinCardinality)}}}
                OR E1.{{{nameof(DbElement.MaxCardinality)}}} != E2.{{{nameof(DbElement.MaxCardinality)}}}
                OR E1.{{{nameof(DbElement.FullCollatedTypeLiteral)}}} != E2.{{{nameof(DbElement.FullCollatedTypeLiteral)}}}
                OR (
                    (E1.{{{nameof(DbElement.ValueSetBindingStrength)}}} is NULL AND E2.{{{nameof(DbElement.ValueSetBindingStrength)}}} is NOT NULL)
                    or (E1.{{{nameof(DbElement.ValueSetBindingStrength)}}} is NOT NULL AND E2.{{{nameof(DbElement.ValueSetBindingStrength)}}} is NULL)
                    or (E1.{{{nameof(DbElement.ValueSetBindingStrength)}}} != E2.{{{nameof(DbElement.ValueSetBindingStrength)}}})
                    )
                OR E1.{{{nameof(DbElement.IsModifier)}}} != E2.{{{nameof(DbElement.IsModifier)}}}
                )
            """;

        return Convert.ToInt32(command.ExecuteScalar());
    }

    //public static int UpdateCollatedTypeStructureKeys(
    //    this IDbConnection dbConnection,
    //    int FhirPackageKey,
    //    string? collatedTypeTableName = null,
    //    string? structureTableName = null)
    //{
    //    collatedTypeTableName ??= DbCollatedType.DefaultTableName;
    //    structureTableName ??= DbStructureDefinition.DefaultTableName;

    //    IDbCommand command = dbConnection.CreateCommand();
    //    command.CommandText = $$$"""
    //        UPDATE {{{collatedTypeTableName}}}
    //        SET
    //            {{{nameof(DbCollatedType.TypeStructureKey)}}} = S.{{{nameof(DbStructureDefinition.Key)}}}
    //        FROM {{{structureTableName}}} S
    //        WHERE {{{collatedTypeTableName}}}.{{{nameof(DbCollatedType.TypeName)}}} = S.{{{nameof(DbStructureDefinition.Id)}}}
    //        AND {{{collatedTypeTableName}}}.{{{nameof(DbCollatedType.FhirPackageKey)}}} = S.{{{nameof(DbStructureDefinition.FhirPackageKey)}}}
    //        AND {{{collatedTypeTableName}}}.{{{nameof(DbCollatedType.FhirPackageKey)}}} = {{{FhirPackageKey}}}
    //        """;

    //    return command.ExecuteNonQuery();
    //}

    public static int UpdateElementTypeStructureKeys(
        this IDbConnection dbConnection,
        int FhirPackageKey,
        string? elementTypeTableName = null,
        string? structureTableName = null)
    {
        return UpdateTypeKeys(dbConnection, FhirPackageKey, elementTypeTableName, structureTableName, 0);
    }

    public static int UpdateElementTypeProfileStructureKeys(
        this IDbConnection dbConnection,
        int FhirPackageKey,
        string? elementTypeTableName = null,
        string? structureTableName = null)
    {
        return UpdateTypeKeys(dbConnection, FhirPackageKey, elementTypeTableName, structureTableName, 1);
    }

    public static int UpdateElementTargetProfileStructureKeys(
        this IDbConnection dbConnection,
        int FhirPackageKey,
        string? elementTypeTableName = null,
        string? structureTableName = null)
    {
        return UpdateTypeKeys(dbConnection, FhirPackageKey, elementTypeTableName, structureTableName, 2);
    }

    public static int UpdateElementBaseKeys(
        this IDbConnection dbConnection,
        int FhirPackageKey,
        string? dbTableName = null)
    {
        XVerStructureCatalog catalog = new(dbConnection, elementTableName: dbTableName);
        List<DbElement> changed = [];
        foreach (DbElement element in DbElement.SelectList(dbConnection, dbTableName: dbTableName, FhirPackageKey: FhirPackageKey))
        {
            DbElement? inherited = catalog.ResolveBaseElement(element, required: false);
            if (inherited is not null && (element.BaseElementKey != inherited.Key || element.BaseStructureKey != inherited.StructureKey))
            {
                element.BaseElementKey = inherited.Key;
                element.BaseStructureKey = inherited.StructureKey;
                changed.Add(element);
            }
        }

        DbElement.Update(dbConnection, changed, dbTableName: dbTableName);
        return changed.Count;
    }

    private static int UpdateTypeKeys(
        IDbConnection db, int packageKey, string? elementTypeTableName, string? structureTableName, int kind)
    {
        List<DbStructureDefinition> structures = DbStructureDefinition.SelectList(db, dbTableName: structureTableName);
        List<DbElementType> changed = [];
        foreach (DbElementType type in DbElementType.SelectList(db, dbTableName: elementTypeTableName, FhirPackageKey: packageKey))
        {
            string context = $"element type {type.Key}, owner {packageKey}/{type.StructureKey}";
            int? existing = kind == 0 ? type.TypeStructureKey : kind == 1 ? type.TypeProfileStructureKey : type.TargetProfileStructureKey;
            string? literal = kind == 0 ? type.TypeName : kind == 1 ? type.TypeProfile : type.TargetProfile;
            if (literal is null && existing is null)
            {
                continue;
            }

            DbStructureDefinition? resolved = kind == 0
                ? XVerStructureCatalog.ResolveNativeType(structures, packageKey, literal, existing, required: false, context)
                : XVerStructureCatalog.ResolveDefinition(structures, packageKey, literal, existing, required: false, context);
            if (resolved is null || resolved.Key == existing)
            {
                continue;
            }

            if (kind == 0)
            {
                type.TypeStructureKey = resolved.Key;
            }
            else if (kind == 1)
            {
                type.TypeProfileStructureKey = resolved.Key;
            }
            else
            {
                type.TargetProfileStructureKey = resolved.Key;
            }

            changed.Add(type);
        }

        DbElementType.Update(db, changed, dbTableName: elementTypeTableName);
        return changed.Count;
    }

    private static readonly string[] _quantityTypes = [
        "Quantity",
        "Age",
        "Count",
        "Distance",
        "Duration",
        "Money",
        "MoneyQuantity",
        "SimpleQuantity",
        ];

    public static bool IsQuantityType(this DbElementType et)
    {
        if (et == null || string.IsNullOrEmpty(et.TypeName))
        {
            return false;
        }

        if (_quantityTypes.Contains(et.TypeName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static string GetNormalizedName(this DbElementType et)
    {
        if (string.IsNullOrEmpty(et.TypeName))
        {
            return string.Empty;
        }

        if ((et.TypeName == "Quantity") &&
            (et.TypeProfile?.StartsWith("http://hl7.org/", StringComparison.Ordinal) == true))
        {
            return et.TypeProfile.Split('/')[^1];
        }

        return et.TypeName;
    }

    public static bool IsEquivalent(this DbElementType a, DbElementType b)
    {
        if (a == null || b == null)
        {
            return false;
        }

        if ((a.TypeName == b.TypeName) &&
            equalsOrBothNull(a.TypeProfile, b.TypeProfile) &&
            equalsOrBothNull(a.TargetProfile, b.TargetProfile))
        {
            return true;
        }

        return false;

        bool equalsOrBothNull(string? a, string? b)
        {
            return (a == b) || ((a == null) && (b == null));
        }
    }

    public static bool HaveEquivalentProfiles(this DbElementType a, DbElementType b)
    {
        if (a == null || b == null)
        {
            return false;
        }

        if (equalsOrBothNull(a.TypeProfile, b.TypeProfile) &&
            equalsOrBothNull(a.TargetProfile, b.TargetProfile))
        {
            return true;
        }

        return false;

        bool equalsOrBothNull(string? a, string? b)
        {
            return (a == b) || ((a == null) && (b == null));
        }
    }

}
