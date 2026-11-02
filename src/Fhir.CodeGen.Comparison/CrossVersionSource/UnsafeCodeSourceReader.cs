// <copyright file="UnsafeCodeSourceReader.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;

namespace Fhir.CodeGen.Comparison.CrossVersionSource;

internal static class UnsafeCodeSourceReader
{
    internal static void Capture(
        IDbConnection db,
        ConceptMap map,
        string category,
        string filename,
        string sourceRelativeFilename,
        int sourcePackageKey,
        int targetPackageKey)
    {
        if (category == "elements")
        {
            CaptureAnnotations(db, map, filename, sourceRelativeFilename, sourcePackageKey, targetPackageKey);
        }
        else if (category == "codes")
        {
            CaptureCodeEvidence(db, map, sourceRelativeFilename, sourcePackageKey, targetPackageKey);
        }
    }

    private static void CaptureAnnotations(IDbConnection db, ConceptMap map, string filename, string relativeFilename, int sourceKey, int targetKey)
    {
        List<ConceptMap.PropertyComponent> declarations = map.Property
            .Where(property => property.Code == UnsafeCodeConstants.PropertyCode).ToList();
        if (!declarations.Any(property => property.System == UnsafeCodeConstants.PropertySystem))
        {
            return;
        }

        if (declarations.Select(property => (property.System, property.Type)).Distinct().Count() > 1)
        {
            throw new InvalidDataException($"Unsafe-code source '{filename}', ConceptMap '{map.Url}' ({map.Id}), JSON path $.property: ambiguous declaration of '{UnsafeCodeConstants.PropertyCode}'.");
        }

        if (declarations[0].Type != ConceptMap.ConceptMapPropertyType.Boolean)
        {
            return;
        }

        List<DbUnsafeCodeElementAnnotation> rows = [];
        for (int groupIndex = 0; groupIndex < map.Group.Count; groupIndex++)
        {
            ConceptMap.GroupComponent group = map.Group[groupIndex];
            for (int sourceIndex = 0; sourceIndex < group.Element.Count; sourceIndex++)
            {
                ConceptMap.SourceElementComponent source = group.Element[sourceIndex];
                for (int targetIndex = 0; targetIndex < source.Target.Count; targetIndex++)
                {
                    ConceptMap.TargetElementComponent target = source.Target[targetIndex];
                    if (!target.Property.Any(property => property.Code == UnsafeCodeConstants.PropertyCode && property.Value is FhirBoolean { Value: true }))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(source.Code) || string.IsNullOrWhiteSpace(target.Code))
                    {
                        throw new InvalidDataException($"Unsafe-code source '{filename}', ConceptMap '{map.Url}' ({map.Id}), JSON path $.group[{groupIndex}].element[{sourceIndex}].target[{targetIndex}]: annotated mapping requires source and target element identities.");
                    }

                    UnsafeCodeElementAnnotation annotation = new(
                        new(sourceKey, targetKey, source.Code, target.Code),
                        new(relativeFilename, map.Id, map.Url, groupIndex, sourceIndex, targetIndex));
                    rows.Add(new()
                    {
                        Key = DbUnsafeCodeElementAnnotation.GetIndex(),
                        SourceFhirPackageKey = annotation.Context.SourceFhirPackageKey,
                        TargetFhirPackageKey = annotation.Context.TargetFhirPackageKey,
                        SourceElementId = annotation.Context.SourceElementId,
                        TargetElementId = annotation.Context.TargetElementId,
                        RequiresOmittedMandatoryCode = true,
                        SourceRelativeFilename = annotation.Provenance.SourceRelativeFilename,
                        ConceptMapId = annotation.Provenance.ConceptMapId,
                        ConceptMapUrl = annotation.Provenance.ConceptMapUrl,
                        GroupIndex = groupIndex,
                        SourceIndex = sourceIndex,
                        TargetIndex = targetIndex,
                    });
                }
            }
        }

        rows.Insert(db, insertPrimaryKey: true);
    }

    private static void CaptureCodeEvidence(IDbConnection db, ConceptMap map, string relativeFilename, int sourceKey, int targetKey)
    {
        UnsafeCodeCanonical sourceScope = UnsafeCodeCanonical.Parse((map.SourceScope as PrimitiveType)?.ObjectValue as string);
        UnsafeCodeCanonical targetScope = UnsafeCodeCanonical.Parse((map.TargetScope as PrimitiveType)?.ObjectValue as string);
        List<DbUnsafeCodeMapEvidence> rows = [];
        for (int groupIndex = 0; groupIndex < map.Group.Count; groupIndex++)
        {
            ConceptMap.GroupComponent group = map.Group[groupIndex];
            UnsafeCodeCanonical sourceSystem = UnsafeCodeCanonical.Parse(group.Source);
            UnsafeCodeCanonical targetSystem = UnsafeCodeCanonical.Parse(group.Target);
            for (int sourceIndex = 0; sourceIndex < group.Element.Count; sourceIndex++)
            {
                ConceptMap.SourceElementComponent source = group.Element[sourceIndex];
                if (source.NoMap == true || source.Target.Count == 0)
                {
                    AddEvidence(null, null, null, source.NoMap == true);
                }

                for (int targetIndex = 0; targetIndex < source.Target.Count; targetIndex++)
                {
                    ConceptMap.TargetElementComponent target = source.Target[targetIndex];
                    AddEvidence(targetIndex, target.Code, target.Relationship, false);
                }

                void AddEvidence(int? targetIndex, string? targetCode, ConceptMap.ConceptMapRelationship? relationship, bool noMap)
                {
                    UnsafeCodeMapEvidence evidence = new(
                        sourceKey, targetKey, sourceScope, targetScope, sourceSystem, source.Code,
                        targetSystem, targetCode, relationship, noMap,
                        new(relativeFilename, map.Id, map.Url, groupIndex, sourceIndex, targetIndex));
                    rows.Add(new()
                    {
                        Key = DbUnsafeCodeMapEvidence.GetIndex(),
                        SourceFhirPackageKey = evidence.SourceFhirPackageKey,
                        TargetFhirPackageKey = evidence.TargetFhirPackageKey,
                        SourceValueSetUrl = evidence.SourceValueSet.Url,
                        SourceValueSetVersion = evidence.SourceValueSet.Version,
                        TargetValueSetUrl = evidence.TargetValueSet.Url,
                        TargetValueSetVersion = evidence.TargetValueSet.Version,
                        SourceSystem = evidence.SourceSystem.Url,
                        SourceSystemVersion = evidence.SourceSystem.Version,
                        SourceCode = evidence.SourceCode,
                        TargetSystem = evidence.TargetSystem.Url,
                        TargetSystemVersion = evidence.TargetSystem.Version,
                        TargetCode = evidence.TargetCode,
                        Relationship = evidence.Relationship,
                        ExplicitNoMap = evidence.ExplicitNoMap,
                        SourceRelativeFilename = evidence.Provenance.SourceRelativeFilename,
                        ConceptMapId = evidence.Provenance.ConceptMapId,
                        ConceptMapUrl = evidence.Provenance.ConceptMapUrl,
                        GroupIndex = groupIndex,
                        SourceIndex = sourceIndex,
                        TargetIndex = targetIndex,
                    });
                }
            }
        }

        rows.Insert(db, insertPrimaryKey: true);
    }
}
