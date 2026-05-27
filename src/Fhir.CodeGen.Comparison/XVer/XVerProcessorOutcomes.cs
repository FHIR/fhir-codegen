using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.Data;
using System.Data.Common;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Language;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Snapshot;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;
using Microsoft.Extensions.Logging;
using Octokit;
using static System.Net.Mime.MediaTypeNames;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;
using Tasks = System.Threading.Tasks;

namespace Fhir.CodeGen.Comparison.XVer;

public partial class XVerProcessor
{

    /// <summary>
    /// Enumerates the possible elementOutcomes for cross-version element mapping in FHIR processing.
    /// </summary>
    private enum XverOutcomeCodes
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
        /// <summary>
        /// The element is mapped to one of several possible elements, and possibly an extension, in the target version.
        /// </summary>
        UseOneOf,
    }

    /// <summary>
    /// Represents the elementOutcome of a cross-version element mapping operation.
    /// </summary>
    [Obsolete]
    private record class XverOutcome
    {
        /// <summary>
        /// Gets the key of the source FHIR package.
        /// </summary>
        public required int SourcePackageKey { get; init; }

        /// <summary>
        /// Gets the name of the source structure.
        /// </summary>
        public required string SourceStructureName { get; init; }

        /// <summary>
        /// Gets the identifier of the source element.
        /// </summary>
        public required string SourceElementId { get; init; }

        public required string? SourceElementBasePath { get; init; }

        /// <summary>
        /// Gets the field order of the source element within the structure.
        /// </summary>
        public required int SourceElementFieldOrder { get; init; }

        /// <summary>
        /// Gets the key of the target FHIR package.
        /// </summary>
        public required int TargetPackageKey { get; init; }

        /// <summary>
        /// Gets the elementOutcome code describing the mapping result.
        /// </summary>
        public required XverOutcomeCodes OutcomeCode { get; init; }

        /// <summary>
        /// Gets the identifier of the target element, if applicable.
        /// </summary>
        public required string? TargetElementId { get; init; }

        public required string? TargetElementBasePath { get; init; }

        /// <summary>
        /// Gets the URL of the target extension, if the mapping resulted in an extension.
        /// </summary>
        public required string? TargetExtensionUrl { get; init; }

        /// <summary>
        /// Gets the URL of the target extension, if the mapping resulted in an extension.
        /// </summary>
        public required string? TargetExtensionId { get; init; }

        /// <summary>
        /// Gets the URL of a replacement extension, if the mapping resulted in a substitution.
        /// </summary>
        public required string? ReplacementExtensionUrl { get; init; }
    }


    private void recreateTables(IDbConnection db)
    {
        DbValueSetOutcome.DropTable(db);
        DbValueSetConceptOutcome.DropTable(db);
        DbStructureOutcome.DropTable(db);
        DbElementOutcome.DropTable(db);

        DbValueSetOutcome.CreateTable(db);
        DbValueSetConceptOutcome.CreateTable(db);
        DbStructureOutcome.CreateTable(db);
        DbElementOutcome.CreateTable(db);

        DbValueSetOutcome._indexValue = 0;
        DbValueSetConceptOutcome._indexValue = 0;
        DbStructureOutcome._indexValue = 0;
        DbElementOutcome._indexValue = 0;
    }

    public void GenerateOutcomesFromComparisons()
    {
        // check for no database
        if (_db == null)
        {
            throw new Exception("Cannot generate outcomes without a loaded database!");
        }

        _logger.LogInformation($"Building cross-version outcomes from existing comparisons.");

        // recreate the elementOutcome tables
        recreateTables(_db.DbConnection);

        // grab the FHIR Packages we are processing
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(_db.DbConnection, orderByProperties: [nameof(DbFhirPackage.ShortName)]);
        List<FhirPackageComparisonPair> packageComparisonPairs = FhirPackageComparisonPair.GetPairs(packages)
            .OrderBy(p => p.SortKey)
            .ToList();

        HashSet<(int sourcePackageKey, int targetPackageKey)> processedPackagePairs = [];

        // traverse all the processed pairs to build the neighbor-pair elementOutcomes
        foreach (FhirPackageComparisonPair packagePair in packageComparisonPairs)
        {
            _logger.LogInformation(
                $"Processing outcomes for package pair: {packagePair.SourcePackageShortName} -> {packagePair.TargetPackageShortName}");

            DbFhirPackage? sourcePackage = packages.FirstOrDefault(p => p.Key == packagePair.SourcePackageKey);
            DbFhirPackage? targetPackage = packages.FirstOrDefault(p => p.Key == packagePair.TargetPackageKey);

            if ((sourcePackage is null) ||
                (targetPackage is null))
            {
                _logger.LogWarning(
                    "Could not find source or target package for comparison pair: {SourcePackageKey} -> {TargetPackageKey}",
                    packagePair.SourcePackageKey,
                    packagePair.TargetPackageKey);
                throw new Exception("Missing package for comparison pair!");
            }

            // build the initial elementOutcomes for this package pair
            generateOutcomes(
                sourcePackage,
                targetPackage,
                packagePair);

            // flag this pair as processed
            processedPackagePairs.Add((sourcePackage.Key, targetPackage.Key));
        }

        // TODO: traverse the packages to extend elementOutcomes to non-neighbor pairs (all combinatorial elementOutcomes)
    }

    [Obsolete]
    private void updateStructureOutcomeActions(
        List<DbStructureOutcome> outcomes,
        DbStructureDefinition targetBasicStructure,
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage)
    {
#if false
        // traverse structure elementOutcomes without actions
        foreach (DbStructureOutcome outcome in outcomes)
        {
            if (outcome.OutcomeAction is not null)
            {
                continue;
            }

            // check for unmapped
            if (outcome.TargetStructureKey is null)
            {
                // resources map to basic, datatypes map to extensions
                if (outcome.SourceArtifactClass == FhirArtifactClassEnum.Resource)
                {
                    outcome.TargetStructureKey = targetBasicStructure.Key;
                    outcome.TargetStructureName = targetBasicStructure.Name;
                    outcome.OutcomeAction = OutcomeStructureActionCodes.UseBasicResource;

                    outcome.Comments += "\n\n" +
                        $"The FHIR {sourcePackage.ShortName} resource `{outcome.SourceStructureName}` has no mapping to" +
                        $" FHIR {targetPackage.ShortName} and is represented using the `Basic` resource with extensions.";
                }
                else
                {
                    outcome.TargetStructureKey = null;
                    outcome.TargetStructureName = null;
                    outcome.OutcomeAction = OutcomeStructureActionCodes.UseDatatypeExtension;

                    outcome.Comments += "\n\n" +
                        $"The FHIR {sourcePackage.ShortName} Structure ({outcome.SourceArtifactClass.ToString()})" +
                        $" `{outcome.SourceStructureName}` has no mapping to FHIR {targetPackage.ShortName} and" +
                        $" is represented by a complex extension that includes an explicit `_datatype`.";
                }

                continue;
            }

            // check for multiple targets
            if (outcome.TotalTargetCount > 1)
            {
                outcome.Comments += "\n\n" +
                    $"Note that the FHIR {sourcePackage.ShortName} Structure ({outcome.SourceArtifactClass.ToString()})" +
                    $" `{outcome.SourceStructureName}` maps to multiple structures in FHIR {targetPackage.ShortName}." +
                    $" Some elements that are not mapped to `{outcome.TargetStructureName}` are mapped to another structure.";
            }

            // if we have a target, the elementOutcome is either renamed or same-name
            if (outcome.IsRenamed == true)
            {
                outcome.OutcomeAction = OutcomeStructureActionCodes.UseStructureRenamed;
            }
            else
            {
                outcome.OutcomeAction = OutcomeStructureActionCodes.UseStructureSameName;
            }
        }
#endif
    }

    private bool elementCannotHaveExtension(DbElement e) => 
        (e.IsSimpleType == true) ||
        (e.FullCollatedTypeLiteral == "Resource") ||
        (e.FullCollatedTypeLiteral == "Extension") ||
        (e.FullCollatedTypeLiteral == "Base") ||
        e.FullCollatedTypeLiteral.StartsWith("Resource(");

    private void updateElementOutcomeActions(
        List<DbElementOutcome> elementOutcomes,
        List<DbStructureOutcome> structureOutcomes,
        DbStructureDefinition targetBasicStructure,
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage)
    {
#if false
        // build a lookup for structure elementOutcomes
        ILookup<int, DbStructureOutcome> structureOutcomeLookup = structureOutcomes.ToLookup(so => so.Key);

        DbElement? rootBasicElement = null;

        // get the list of elements from the basic structure
        Dictionary<string, DbElement> basicMappableElements = [];

        // iterate over the elements
        foreach (DbElement element in DbElement.SelectEnumerable(_db!.DbConnection, StructureKey: targetBasicStructure.Key))
        {
            if ((element.ParentElementKey is null) ||
                (element.ResourceFieldOrder == 0))
            {
                rootBasicElement = element;
                continue;
            }

            // skip root, elements with empty paths, and `code` element
            if (string.IsNullOrEmpty(element.Path) ||
                element.Path.Equals("Basic.code", StringComparison.Ordinal))
            {
                continue;
            }

            // add the path to the dictionary, but strip "Basic" from the front
            basicMappableElements.Add(element.Path[5..], element);
        }

        DbStructureOutcome? sdOutcome = null;
        int sdNameLength = 0;
        Dictionary<int, DbElementOutcome> elementKeysWithExtensions = [];
        List<DbElementOutcome> elementOutcomesForThisSource = [];
        DbElement? sourceElement = null;
        bool isFullyMapped = false;

        // traverse the element elementOutcomes without actions
        foreach (DbElementOutcome elementOutcome in elementOutcomes.OrderBy(eo => eo.SourceElementKey))
        {
            if (elementOutcome.OutcomeAction is not null)
            {
                continue;
            }

            // update our current structure if necessary
            if (elementOutcome.StructureOutcomeKey != sdOutcome?.Key)
            {
                sdOutcome = structureOutcomeLookup[elementOutcome.StructureOutcomeKey].FirstOrDefault();

                if (sdOutcome is null)
                {
                    throw new Exception(
                        $"Element outcome with key {elementOutcome.Key} has invalid structure outcome key {elementOutcome.StructureOutcomeKey}!");
                }

                sdNameLength = sdOutcome.SourceStructureName.Length;
            }

            if (sourceElement?.Key != elementOutcome.SourceElementKey)
            {
                if (elementOutcomesForThisSource.Count != 0)
                {
                    foreach (DbElementOutcome eo in elementOutcomesForThisSource)
                    {
                        eo.FullyMapsAcrossAllTargets = isFullyMapped;

                        if (isFullyMapped &&
                            (eo.OutcomeAction == OutcomeElementActionCodes.UseExtension))
                        {
                            eo.OutcomeAction = OutcomeElementActionCodes.MappedElsewhere;
                        }
                    }

                    elementOutcomesForThisSource.Clear();
                }

                isFullyMapped = false;

                // resolve the source element for this elementOutcome
                sourceElement = DbElement.SelectSingle(
                    _db.DbConnection,
                    Key: elementOutcome.SourceElementKey);

                if (sourceElement is null)
                {
                    throw new Exception(
                        $"Element outcome with key {elementOutcome.Key} has invalid source element key {elementOutcome.SourceElementKey}!");
                }
            }

            elementOutcomesForThisSource.Add(elementOutcome);
            if (elementOutcome.FullyMapsToThisTarget || elementOutcome.FullyMapsAcrossAllTargets)
            {
                isFullyMapped = true;
            }

            // check to see if we are on the root element of a structure
            if ((sourceElement.ParentElementKey is null) ||
                (sourceElement.ResourceFieldOrder == 0))
            {
                // use the structure elementOutcome action
                switch (sdOutcome.OutcomeAction)
                {
                    case OutcomeStructureActionCodes.UseStructureSameName:
                        elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseElementSameName;
                        break;

                    case OutcomeStructureActionCodes.UseStructureRenamed:
                        elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseElementRenamed;
                        break;

                    case OutcomeStructureActionCodes.UseBasicResource:
                        elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseBasicElement;
                        elementOutcome.TargetStructureKey = targetBasicStructure.Key;
                        elementOutcome.TargetElementKey = rootBasicElement?.Key;
                        elementOutcome.TargetElementId = rootBasicElement?.Id;
                        elementOutcome.TargetResourceOrder = rootBasicElement?.ResourceFieldOrder;
                        elementOutcome.TargetComponentOrder = rootBasicElement?.ComponentFieldOrder;

                        elementOutcome.Comments += "\n\n" +
                            $"The FHIR {sourcePackage.ShortName} Resource `{sdOutcome.SourceStructureName}` has no" +
                            $" mapping to FHIR {targetPackage.ShortName} and is represented using the `Basic` resource" +
                            $" with extensions.";
                        break;

                    case OutcomeStructureActionCodes.UseDatatypeExtension:
                        elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseExtension;
                        elementOutcome.Comments += "\n\n" +
                            $"The FHIR {sourcePackage.ShortName} Structure ({sdOutcome.SourceArtifactClass.ToString()})" +
                            $" `{sdOutcome.SourceStructureName}` has no mapping to FHIR {targetPackage.ShortName} and" +
                            $" is represented using a complex extension that includes an explicit `_datatype`.";
                        break;
                    default:
                        throw new Exception(
                            $"Unhandled structure outcome action code {sdOutcome.OutcomeAction} for structure outcome {sdOutcome.Key}!");
                }

                continue;
            }

            // check for an element that maps to the basic resource
            if ((sdOutcome.OutcomeAction == OutcomeStructureActionCodes.UseBasicResource) &&
                basicMappableElements.TryGetValue(elementOutcome.SourceElementId[sdNameLength..], out DbElement? targetBasicElement))
            {
                elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseBasicElement;
                elementOutcome.TargetStructureKey = targetBasicStructure.Key;
                elementOutcome.TargetElementKey = targetBasicElement.Key;
                elementOutcome.TargetElementId = targetBasicElement.Path;
                elementOutcome.TargetResourceOrder = targetBasicElement.ResourceFieldOrder;
                elementOutcome.TargetComponentOrder = targetBasicElement.ComponentFieldOrder;

                elementOutcome.Comments += "\n\n" +
                    $"The FHIR {sourcePackage.ShortName} Resource `{sdOutcome.SourceStructureName}` has no mapping" +
                    $" for FHIR {targetPackage.ShortName} and is represented by the `Basic` resource. The" +
                    $" element `{elementOutcome.SourceElementId}` maps to the existing `Basic` element `{targetBasicElement.Path}`.";

                continue;
            }

            // check for an element of type extension
            if (sourceElement.FullCollatedTypeLiteral == "Extension")
            {
                elementOutcome.OutcomeAction = OutcomeElementActionCodes.IsExtension;
                elementOutcome.Comments += "\n\n" +
                    $"The FHIR {sourcePackage.ShortName} element `{elementOutcome.SourceElementId}` is of type `Extension`." +
                    $" Use an extension in FHIR {targetPackage.ShortName} on an appropriate element or extension.";
                continue;
            }

            // check for `Element.id`
            if (sourceElement.IsSimpleType &&
                (sourceElement.BasePath == "Element.id"))
            {
                elementOutcome.OutcomeAction = OutcomeElementActionCodes.IsElementId;
                elementOutcome.Comments += "\n\n" +
                    $"The FHIR {sourcePackage.ShortName} element `{elementOutcome.SourceElementId}` is an Element ID" +
                    $" and should be represented by the Element.id of the element or extension that represents" +
                    $" the parent.";
                continue;
            }

            // check for elements that we will not generate extensions for
            if (elementCannotHaveExtension(sourceElement))
            {
                if (elementOutcome.TargetElementKey is null)
                {
                    elementOutcome.OutcomeAction = OutcomeElementActionCodes.Unresolved;
                    elementOutcome.Comments += "\n\n" +
                        $"The FHIR {sourcePackage.ShortName} element has no valid target and cannot be mapped to an extension." +
                        $" If you believe this is incorrect, please contact the FHIR Infrastructure" +
                        $" Work Group or file a ticket against this specification in Jira.";
                    continue;
                }

                // if there is a target element, it is either renamed or same-name
                if (elementOutcome.IsRenamed == true)
                {
                    elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseElementRenamed;
                }
                else
                {
                    elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseElementSameName;
                }

                continue;
            }

            // check to see if we are in a backbone that already has been moved into extension space
            if (elementKeysWithExtensions.TryGetValue(sourceElement.ParentElementKey.Value, out DbElementOutcome? ancestorOutcome))
            {
                // use the extension from ancestor
                elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseExtensionFromAncestor;
                elementOutcome.TargetStructureKey = ancestorOutcome.TargetStructureKey;
                elementOutcome.RelatedAncestorOutcomeKey = ancestorOutcome.Key;
                elementOutcome.Comments += "\n\n" +
                    $"The FHIR {sourcePackage.ShortName} element `{elementOutcome.SourceElementId}` is contained within the backbone element" +
                    $" `{ancestorOutcome.SourceElementId}`, which is mapped to an extension." +
                    $" This element is defined as part of the extension `{ancestorOutcome.PotentialGenUrl}`.";

                // add this element to the extensions list, but use the ancestor elementOutcome so we have the correct extension when looking up
                elementKeysWithExtensions.Add(elementOutcome.Key, ancestorOutcome);
                continue;
            }

            // check for non-generating elementOutcomes
            if ((elementOutcome.TargetElementKey is not null) &&
                elementOutcome.FullyMapsAcrossAllTargets)
            {
                if (elementOutcome.IsRenamed == true)
                {
                    elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseElementRenamed;
                }
                else
                {
                    elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseElementSameName;
                }

                // check for multiple targets
                if (elementOutcome.TotalTargetCount > 1)
                {
                    elementOutcome.Comments += "\n\n" +
                        $"Note that rhe FHIR {sourcePackage.ShortName} element `{elementOutcome.SourceElementId}`" +
                        $" maps to multiple elements in FHIR {targetPackage.ShortName}.";
                }

                continue;
            }

            // need to generate an extension
            elementOutcome.OutcomeAction = OutcomeElementActionCodes.UseExtension;
            elementKeysWithExtensions.Add(elementOutcome.Key, elementOutcome);
        }

        // check last element
        if (elementOutcomesForThisSource.Count != 0)
        {
            foreach (DbElementOutcome eo in elementOutcomesForThisSource)
            {
                eo.FullyMapsAcrossAllTargets = isFullyMapped;

                if (isFullyMapped &&
                    (eo.OutcomeAction == OutcomeElementActionCodes.UseExtension))
                {
                    eo.OutcomeAction = OutcomeElementActionCodes.MappedElsewhere;
                }
            }

            elementOutcomesForThisSource.Clear();
            isFullyMapped = false;
        }
#endif
    }

    private void updateValueSetOutcomeActions(List<DbValueSetOutcome> outcomes)
    {
        throw new NotImplementedException("Transitioning outcome structures...");
#if false
        // traverse the value set elementOutcomes without actions
        foreach (DbValueSetOutcome outcome in outcomes)
        {
            if (outcome.OutcomeAction is not null)
            {
                continue;
            }

            // check for a complete mapping
            if (outcome.FullyMapsToThisTarget || outcome.FullyMapsAcrossAllTargets)
            {
                if (outcome.IsRenamed == true)
                {
                    outcome.OutcomeAction = OutcomeValueSetActionCodes.UseValueSetRenamed;
                }
                else
                {
                    outcome.OutcomeAction = OutcomeValueSetActionCodes.UseValueSetSameName;
                }

                continue;
            }

            // check for cross-version only
            if (outcome.IsUnmapped == true)
            {
                outcome.OutcomeAction = OutcomeValueSetActionCodes.UseCrossVersionDefinition;
                continue;
            }

            // the remaining has a source and a target and needs the cross-version definition
            if (outcome.IsRenamed == true)
            {
                outcome.OutcomeAction = OutcomeValueSetActionCodes.UseRenamedAndCrossVersion;
            }
            else
            {
                outcome.OutcomeAction = OutcomeValueSetActionCodes.UseSameNameAndCrossVersion;
            }
        }
#endif
    }

    private void updateValueSetConceptOutcomeActions(
        List<DbValueSetConceptOutcome> outcomes,
        List<DbValueSetOutcome> vsOutcomes,
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage)
    {
        throw new NotImplementedException("Transitioning outcome structures...");
#if false
        // build a lookup for elementOutcomes based on source concept key
        ILookup<int, DbValueSetConceptOutcome> conceptOutcomeLookup = outcomes.ToLookup(o => o.SourceValueSetConceptKey);

        List<DbValueSetConceptOutcome> conceptOutcomesForThisSource = [];
        int? sourceConceptKey = null;
        bool isFullyMapped = false;

        // traverse the value set concept elementOutcomes
        foreach (DbValueSetConceptOutcome outcome in outcomes.OrderBy(co => co.SourceValueSetConceptKey))
        {
            if (outcome.OutcomeAction is not null)
            {
                continue;
            }

            if (sourceConceptKey != outcome.SourceValueSetConceptKey)
            {
                if (conceptOutcomesForThisSource.Count != 0)
                {
                    foreach (DbValueSetConceptOutcome co in conceptOutcomesForThisSource)
                    {
                        co.FullyMapsAcrossAllTargets = isFullyMapped;

                        if (isFullyMapped &&
                            (co.OutcomeAction == OutcomeValueSetConceptActionCodes.UseCrossVersionDefinition))
                        {
                            co.OutcomeAction = OutcomeValueSetConceptActionCodes.MappedElsewhere;
                        }
                    }
                    conceptOutcomesForThisSource.Clear();
                }
                isFullyMapped = false;
                sourceConceptKey = outcome.SourceValueSetConceptKey;
            }

            conceptOutcomesForThisSource.Add(outcome);
            if (outcome.FullyMapsToThisTarget || outcome.FullyMapsAcrossAllTargets)
            {
                isFullyMapped = true;
            }

            // check for unmapped
            if (outcome.IsUnmapped ||
                (outcome.TargetCode is null))
            {
                // check to see if this concept has a mapping to another value set
                if (conceptOutcomeLookup[outcome.SourceValueSetConceptKey].Any(o => !o.IsUnmapped && (o.TargetCode is not null)))
                {
                    outcome.OutcomeAction = OutcomeValueSetConceptActionCodes.MappedElsewhere;
                    outcome.Comments += "\n\n" +
                        $"The FHIR {sourcePackage.ShortName} ValueSet Concept `{outcome.SourceCode}` has no mapping" +
                        $" in FHIR {targetPackage.ShortName} for this ValueSet, but does have a mapping to another ValueSet." +
                        $" Review other ValueSets in FHIR {targetPackage.ShortName} for the mapped concept.";
                }
                else
                {
                    outcome.OutcomeAction = OutcomeValueSetConceptActionCodes.UseCrossVersionDefinition;
                    outcome.Comments += "\n\n" +
                        $"The FHIR {sourcePackage.ShortName} ValueSet Concept `{outcome.SourceCode}` has no mapping" +
                        $" in FHIR {targetPackage.ShortName}.";
                }

                continue;
            }

            // check for equivalent or identical
            if (outcome.IsIdentical || outcome.IsEquivalent)
            {
                // check for same code
                if (outcome.SourceCode == outcome.TargetCode)
                {
                    outcome.OutcomeAction = OutcomeValueSetConceptActionCodes.UseConceptSameCode;
                }
                else
                {
                    outcome.OutcomeAction = OutcomeValueSetConceptActionCodes.UseConceptChangedCode;
                }

                outcome.Comments += "\n\n" +
                    $"The FHIR {sourcePackage.ShortName} ValueSet Concept" +
                    $" `{outcome.SourceSystem}`#`{outcome.SourceCode}` is mapped as" +
                    $" equivalent to FHIR {targetPackage.ShortName} ValueSet Concept" +
                    $" `{outcome.TargetSystem}`#`{outcome.TargetCode}`.";

                continue;
            }

            // default to cross-version definition
            outcome.OutcomeAction = OutcomeValueSetConceptActionCodes.UseCrossVersionDefinition;
            outcome.Comments += "\n\n" +
                $"The FHIR {sourcePackage.ShortName} ValueSet Concept" +
                $" `{outcome.SourceSystem}`#`{outcome.SourceCode}` is mapped as" +
                $" a non-equivalent concept to FHIR {targetPackage.ShortName} ValueSet Concept" +
                $" `{outcome.TargetSystem}`#`{outcome.TargetCode}`." +
                $" Implementers must determine which of the definitions are appropriate for use.";
        }

        if (conceptOutcomesForThisSource.Count != 0)
        {
            foreach (DbValueSetConceptOutcome co in conceptOutcomesForThisSource)
            {
                co.FullyMapsAcrossAllTargets = isFullyMapped;

                if (isFullyMapped &&
                    (co.OutcomeAction == OutcomeValueSetConceptActionCodes.UseCrossVersionDefinition))
                {
                    co.OutcomeAction = OutcomeValueSetConceptActionCodes.MappedElsewhere;
                }
            }
            conceptOutcomesForThisSource.Clear();
        }
        isFullyMapped = false;
#endif
    }


    private void generateOutcomes(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        FhirPackageComparisonPair packagePair)
    {
        // process value sets
        generateOutcomesVs(
            sourcePackage,
            targetPackage,
            packagePair);

        // process structures
        generateOutcomesSd(
            sourcePackage,
            targetPackage,
            packagePair);
    }

    private (string idLong, string idShort) generateExtensionId(
        string sourcePackageShortName,
        string sourceElementPath)
    {
        string idLong = $"extension-{sourceElementPath.Replace("[x]", string.Empty)}";
        string idShort = $"ext-{sourcePackageShortName}-{collapsePathForId(sourceElementPath)}";

        return (idLong, idShort);

        string collapsePathForId(string path)
        {
            string pathClean = path.Replace("[x]", string.Empty);
            string[] components = pathClean.Split('.');
            switch (components.Length)
            {
                case 0:
                    return pathClean;

                case 1:
                    return pathClean;

                case 2:
                    {
                        if (pathClean.Length > 45)
                        {
                            string rName = (components[0].Length > 20)
                                ? new string(components[0].Where(char.IsUpper).ToArray())
                                : components[0];

                            string eName = (components[1].Length > 20)
                                ? $"{components[1][0]}" + new string(components[1].Where(char.IsUpper).ToArray())
                                : components[1];

                            return rName + "." + eName;
                        }

                        return pathClean;
                    }

                default:
                    {
                        // use the full first and last, and one character from each in-between
                        if (components[0].Length > 20)
                        {
                            components[0] = new string(components[0].Where(char.IsUpper).ToArray());
                        }

                        for (int i = 1; i < components.Length - 1; i++)
                        {
                            if (components[i].Length > 3)
                            {
                                components[i] = $"{components[i][0]}{components[i][1]}";
                            }
                        }

                        if (components.Last().Length > 20)
                        {
                            components[components.Length - 1] = $"{components[components.Length - 1][0]}" + new string(components[0].Where(char.IsUpper).ToArray());
                        }

                        return string.Join('.', components);
                    }
            }
        }
    }

    private void generateOutcomesVs(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        FhirPackageComparisonPair? packagePair)
    {
#if false
        List<DbValueSetOutcome> vsOutcomesToAdd = [];
        List<DbValueSetConceptOutcome> vsConceptOutcomesToAdd = [];

        // get the list of all value sets for the source package
        List<DbValueSet> sourceValueSets = DbValueSet.SelectList(
            _db!.DbConnection,
            FhirPackageKey: sourcePackage.Key);

        // iterate over each source value set
        foreach (DbValueSet sourceVs in sourceValueSets)
        {
            // check for any comparisons for this value set against the target package
            List<DbValueSetComparison> vsComparisons = DbValueSetComparison.SelectList(
                _db!.DbConnection,
                SourceValueSetKey: sourceVs.Key,
                TargetFhirPackageKey: targetPackage.Key);

            (string idLong, string idShort) = GenerateArtifactId(
                sourcePackage.ShortName,
                sourceVs.Id,
                targetPackage.ShortName);

            // shortcut if there are no comparisons
            if (vsComparisons.Count == 0)
            {
                DbValueSetOutcome noMapOutcome = new()
                {
                    Key = DbValueSetOutcome.GetIndex(),
                    SourceFhirPackageKey = sourcePackage.Key,
                    TargetFhirPackageKey = targetPackage.Key,
                    SourceValueSetKey = sourceVs.Key,
                    SourceValueSetUnversionedUrl = sourceVs.UnversionedUrl,
                    SourceValueSetVersion = sourceVs.Version,
                    TargetValueSetKey = null,
                    TargetValueSetUnversionedUrl = null,
                    TargetValueSetVersion = null,

                    PotentialGenResourceType = "ValueSet",
                    PotentialGenLongId = idLong,
                    PotentialGenShortId = idShort,
                    PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/ValueSet/{idLong}",
                    OutcomeAction = null,

                    TotalSourceCount = 1,
                    TotalTargetCount = 0,

                    IsRenamed = false,
                    IsUnmapped = true,
                    IsIdentical = false,
                    IsEquivalent = false,
                    IsBroaderThanTarget = false,
                    IsNarrowerThanTarget = false,
                    FullyMapsToThisTarget = false,
                    FullyMapsAcrossAllTargets = false,
                    ConceptDomainRelationship = null,
                    ValueDomainRelationship = null,

                    Comments =
                        $"The FHIR {sourcePackage.ShortName} ValueSet `{sourceVs.UnversionedUrl}|{sourceVs.Version}`" +
                        $" (`{sourceVs.Id}`) has no mapping to FHIR {targetPackage.ShortName}.",
                };

                // track to add to the database
                vsOutcomesToAdd.Add(noMapOutcome);
                continue;
            }

            // get the concepts for this value set
            List<DbValueSetConcept> sourceVsConcepts = DbValueSetConcept.SelectList(
                _db.DbConnection,
                ValueSetKey: sourceVs.Key);

            int[] vsOutcomeKeys = new int[vsComparisons.Count];
            for (int i = 0; i < vsComparisons.Count; i++)
            {
                vsOutcomeKeys[i] = DbValueSetOutcome.GetIndex();
            }

            HashSet<int> conceptsThatFullyMapToAnyTarget = [];
            List<DbValueSetOutcome> vsOutcomesForThisSource = [];

            bool multipleTargets = vsComparisons.Count(c => c.TargetContentKey is not null) > 1;

            // iterate over each comparison for this value set
            foreach ((DbValueSetComparison vsComparison, int comparisonIndex) in vsComparisons.Select((c, i) => (c, i)))
            {
                HashSet<int> conceptsThatFullyMapToThisTarget = [];

                DbValueSet? targetVs = DbValueSet.SelectSingle(
                    _db.DbConnection,
                    Key: vsComparison.TargetValueSetKey);

                if ((targetVs is null) ||
                    (vsComparison.TargetValueSetKey is null))
                {
                    throw new Exception(
                        $"ValueSet comparison for source ValueSet {sourceVs.Id} " +
                        $"has invalid target ValueSet key {vsComparison.TargetValueSetKey}!");
                }

                // generated ids need to have target artifacts if there are multiple targets
                if (multipleTargets)
                {
                    (idLong, idShort) = GenerateArtifactId(
                        sourcePackage.ShortName,
                        sourceVs.Id,
                        targetPackage.ShortName,
                        targetVs.Id);
                }

                // iterate over the concepts in this value set
                foreach (DbValueSetConcept sourceConcept in sourceVsConcepts)
                {
                    // check for concept comparisons for this source concept
                    List<DbValueSetConceptComparison> conceptComparisons = DbValueSetConceptComparison.SelectList(
                        _db.DbConnection,
                        ValueSetComparisonKey: vsComparison.Key,
                        SourceConceptKey: sourceConcept.Key);

                    // shortcut if there are no concept comparisons
                    if (conceptComparisons.Count == 0)
                    {
                        DbValueSetConceptOutcome noMapOutcome = new()
                        {
                            Key = DbValueSetConceptOutcome.GetIndex(),
                            SourceFhirPackageKey = sourcePackage.Key,
                            TargetFhirPackageKey = targetPackage.Key,
                            SourceValueSetKey = sourceVs.Key,
                            SourceValueSetConceptKey = sourceConcept.Key,
                            SourceSystem = sourceConcept.System,
                            SourceCode = sourceConcept.Code,
                            TargetValueSetKey = targetVs.Key,
                            TargetValueSetConceptKey = null,
                            TargetSystem = null,
                            TargetCode = null,

                            ValueSetOutcomeKey = vsOutcomeKeys[comparisonIndex],

                            TotalSourceCount = 1,
                            TotalTargetCount = 0,

                            IsRenamed = false,
                            IsUnmapped = true,
                            IsIdentical = false,
                            IsEquivalent = false,
                            IsBroaderThanTarget = false,
                            IsNarrowerThanTarget = false,
                            CodeLiteralsMatch = false,
                            CodeTreatedAsEscapeValve = false,

                            FullyMapsToThisTarget = false,
                            FullyMapsAcrossAllTargets = false,
                            ConceptDomainRelationship = null,
                            ValueDomainRelationship = null,

                            OutcomeAction = null,

                            Comments = 
                                $"The FHIR {sourcePackage.ShortName} ValueSet `{sourceVs.UnversionedUrl}|{sourceVs.Version}`" +
                                $" Concept `{sourceConcept.System}#{sourceConcept.Code}` has no mapping to FHIR {targetPackage.ShortName}.",
                        };

                        vsConceptOutcomesToAdd.Add(noMapOutcome);
                        continue;
                    }

                    bool multipleContentTargets = conceptComparisons.Count(c => c.TargetContentKey is not null) > 1;

                    // iterate over the concept comparisons to build concept elementOutcomes
                    foreach (DbValueSetConceptComparison conceptComparison in conceptComparisons)
                    {
                        // can have comparison that is unmapped
                        if (conceptComparison.TargetConceptKey is null)
                        {
                            DbValueSetConceptOutcome noMapOutcome = new()
                            {
                                Key = DbValueSetConceptOutcome.GetIndex(),
                                SourceFhirPackageKey = sourcePackage.Key,
                                TargetFhirPackageKey = targetPackage.Key,
                                SourceValueSetKey = sourceVs.Key,
                                SourceValueSetConceptKey = sourceConcept.Key,
                                SourceSystem = sourceConcept.System,
                                SourceCode = sourceConcept.Code,
                                TargetValueSetKey = targetVs.Key,
                                TargetValueSetConceptKey = null,
                                TargetSystem = null,
                                TargetCode = null,

                                ValueSetOutcomeKey = vsOutcomeKeys[comparisonIndex],

                                TotalSourceCount = 1,
                                TotalTargetCount = 0,

                                IsRenamed = false,
                                IsUnmapped = true,
                                IsIdentical = false,
                                IsEquivalent = false,
                                IsBroaderThanTarget = false,
                                CodeLiteralsMatch = false,
                                CodeTreatedAsEscapeValve = false,

                                IsNarrowerThanTarget = false,
                                FullyMapsToThisTarget = false,
                                FullyMapsAcrossAllTargets = false,
                                ConceptDomainRelationship = conceptComparison.Relationship,
                                ValueDomainRelationship = CMR.Equivalent,

                                OutcomeAction = null,

                                Comments = 
                                    $"The FHIR {sourcePackage.ShortName} ValueSet `{sourceVs.UnversionedUrl}|{sourceVs.Version}`" +
                                    $" Concept `{sourceConcept.System}#{sourceConcept.Code}` has no mapping to" +
                                    $" FHIR {targetPackage.ShortName} ValueSet `{targetVs.UnversionedUrl}|{targetVs.Version}`.",
                            };

                            vsConceptOutcomesToAdd.Add(noMapOutcome);
                            continue;
                        }

                        // resolve the target concept
                        DbValueSetConcept? targetConcept = DbValueSetConcept.SelectSingle(
                            _db.DbConnection,
                            Key: conceptComparison.TargetConceptKey);

                        if (targetConcept is null)
                        {
                            throw new Exception(
                                $"ValueSet concept comparison for source concept `{sourceConcept.System}#{sourceConcept.Code}` " +
                                $"has invalid target concept key {conceptComparison.TargetConceptKey}!");
                        }

                        // check for equivalency
                        if ((conceptComparison.Relationship == CMR.Equivalent) ||
                            (conceptComparison.Relationship == CMR.SourceIsNarrowerThanTarget))
                        {
                            conceptsThatFullyMapToAnyTarget.Add(sourceConcept.Key);
                            conceptsThatFullyMapToThisTarget.Add(sourceConcept.Key);
                        }

                        DbValueSetConceptOutcome conceptOutcome = new()
                        {
                            Key = DbValueSetConceptOutcome.GetIndex(),
                            SourceFhirPackageKey = sourcePackage.Key,
                            TargetFhirPackageKey = targetPackage.Key,
                            SourceValueSetKey = sourceVs.Key,
                            SourceValueSetConceptKey = sourceConcept.Key,
                            SourceSystem = sourceConcept.System,
                            SourceCode = sourceConcept.Code,
                            TargetValueSetKey = targetVs.Key,
                            TargetValueSetConceptKey = conceptComparison.TargetConceptKey,
                            TargetSystem = targetConcept.System,
                            TargetCode = targetConcept.Code,

                            ValueSetOutcomeKey = vsOutcomeKeys[comparisonIndex],

                            TotalSourceCount = 1,
                            TotalTargetCount = conceptComparisons.Where(cc => cc.TargetConceptKey is not null).Count(),

                            //IsRenamed = !multipleContentTargets &&
                            //    ((conceptComparison.Relationship == CMR.Equivalent) || (conceptComparison.Relationship == CMR.SourceIsNarrowerThanTarget)) &&
                            //    (conceptComparison.CodeLiteralsMatch != true),
                            IsRenamed = (sourceConcept.Code != targetConcept.Code),

                            IsUnmapped = false,
                            IsIdentical = conceptComparison.CodeLiteralsAreIdentical == true,
                            IsEquivalent = conceptComparison.Relationship == CMR.Equivalent,
                            IsBroaderThanTarget = conceptComparison.Relationship == CMR.SourceIsBroaderThanTarget,
                            IsNarrowerThanTarget = conceptComparison.Relationship == CMR.SourceIsNarrowerThanTarget,
                            CodeLiteralsMatch = (sourceConcept.Code == targetConcept.Code),
                            CodeTreatedAsEscapeValve = false,

                            FullyMapsToThisTarget = (conceptComparison.Relationship == CMR.Equivalent) || (conceptComparison.Relationship == CMR.SourceIsNarrowerThanTarget),
                            FullyMapsAcrossAllTargets = conceptComparisons.All(cc => cc.Relationship != CMR.SourceIsBroaderThanTarget),
                            ConceptDomainRelationship = conceptComparison.Relationship,
                            ValueDomainRelationship = CMR.Equivalent,

                            OutcomeAction = null,

                            Comments = 
                                $"The FHIR {sourcePackage.ShortName} ValueSet `{sourceVs.UnversionedUrl}|{sourceVs.Version}`" +
                                $" Concept `{sourceConcept.System}#{sourceConcept.Code}` maps to" +
                                $" FHIR {targetPackage.ShortName} ValueSet `{targetVs.UnversionedUrl}|{targetVs.Version}`" +
                                $" Concept `{targetConcept.System}#{targetConcept.Code}`" +
                                $" with relationship `{conceptComparison.Relationship.GetLiteral()}`.",
                        };

                        vsConceptOutcomesToAdd.Add(conceptOutcome);
                    }
                }

                // generate our value set elementOutcome
                DbValueSetOutcome outcome = new()
                {
                    Key = vsOutcomeKeys[comparisonIndex],
                    SourceFhirPackageKey = sourcePackage.Key,
                    TargetFhirPackageKey = targetPackage.Key,
                    SourceValueSetKey = sourceVs.Key,
                    SourceValueSetUnversionedUrl = sourceVs.UnversionedUrl,
                    SourceValueSetVersion = sourceVs.Version,
                    TargetValueSetKey = targetVs?.Key,
                    TargetValueSetUnversionedUrl = targetVs?.UnversionedUrl,
                    TargetValueSetVersion = targetVs?.Version,

                    PotentialGenResourceType = "ValueSet",
                    PotentialGenLongId = idLong,
                    PotentialGenShortId = idShort,
                    PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/ValueSet/{idLong}",
                    OutcomeAction = null,

                    TotalSourceCount = 1,
                    TotalTargetCount = vsComparisons.Count,

                    //IsRenamed = !multipleTargets &&
                    //    ((vsComparison.Relationship == CMR.Equivalent) || (vsComparison.Relationship == CMR.SourceIsNarrowerThanTarget)) &&
                    //    (sourceVs.IdLong != targetVs?.IdLong),
                    IsRenamed = (sourceVs.Id != targetVs?.Id),

                    IsUnmapped = false,
                    IsIdentical = vsComparison.IsIdentical == true,
                    IsEquivalent = vsComparison.Relationship == CMR.Equivalent,
                    IsBroaderThanTarget = vsComparison.Relationship == CMR.SourceIsBroaderThanTarget,
                    IsNarrowerThanTarget = vsComparison.Relationship == CMR.SourceIsNarrowerThanTarget,
                    FullyMapsToThisTarget = conceptsThatFullyMapToThisTarget.Count == sourceVsConcepts.Count,
                    FullyMapsAcrossAllTargets = false,      // need to finish processing to update this value
                    ConceptDomainRelationship = vsComparison.Relationship,
                    ValueDomainRelationship = CMR.Equivalent,

                    Comments = vsComparison.UserMessage ?? vsComparison.TechnicalMessage ?? string.Empty,
                };

                vsOutcomesToAdd.Add(outcome);
                vsOutcomesForThisSource.Add(outcome);
            }

            // determine if fully mapped - default is false, so only update if true
            if (conceptsThatFullyMapToAnyTarget.Count == sourceVsConcepts.Count)
            {
                // iterate over our elementOutcomes for this source to update the fully-mapped status
                foreach (DbValueSetOutcome vsOutcome in vsOutcomesForThisSource)
                {
                    vsOutcome.FullyMapsAcrossAllTargets = true;
                }
            }
        }

        // update our elementOutcomes with action codes
        updateValueSetOutcomeActions(vsOutcomesToAdd);
        updateValueSetConceptOutcomeActions(vsConceptOutcomesToAdd, vsOutcomesToAdd, sourcePackage, targetPackage);


        // insert our elementOutcomes into the database
        vsOutcomesToAdd.Insert(_db.DbConnection, insertPrimaryKey: true);
        _logger.LogInformation(
            $"Inserted {vsOutcomesToAdd.Count} ValueSet outcomes for source package {sourcePackage.ShortName} to target package {targetPackage.ShortName}.");

        vsConceptOutcomesToAdd.Insert(_db.DbConnection, insertPrimaryKey: true);
        _logger.LogInformation(
            $"Inserted {vsConceptOutcomesToAdd.Count} ValueSet Concept outcomes for source package {sourcePackage.ShortName} to target package {targetPackage.ShortName}.");
#endif
    }

    private void generateOutcomesSd(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        FhirPackageComparisonPair? packagePair)
    {
#if false
        List<DbStructureOutcome> sdOutcomesToAdd = [];
        List<DbElementOutcome> elementOutcomesToAdd = [];

        // get the 'Basic' structure for the target package
        DbStructureDefinition? targetBasicSd = DbStructureDefinition.SelectSingle(
            _db!.DbConnection,
            FhirPackageKey: targetPackage.Key,
            Id: "Basic");

        if (targetBasicSd is null)
        {
            throw new Exception(
                $"Target FHIR package {targetPackage.ShortName} does not have a 'Basic' StructureDefinition!");
        }

        // get the list of all structures for the source package
        List<DbStructureDefinition> sourceStructures = DbStructureDefinition.SelectList(
            _db!.DbConnection,
            FhirPackageKey: sourcePackage.Key);

        // iterate over each source structure
        foreach (DbStructureDefinition sourceSd in sourceStructures)
        {
            // check for any comparisons for this structure against the target package
            List<DbStructureComparison> sdComparisons = DbStructureComparison.SelectList(
                _db!.DbConnection,
                SourceStructureKey: sourceSd.Key,
                TargetFhirPackageKey: targetPackage.Key);

            (string idLong, string idShort) = GenerateArtifactId(
                sourcePackage.ShortName,
                sourceSd.Id,
                targetPackage.ShortName);

            // shortcut if there are no comparisons
            if (sdComparisons.Count == 0)
            {
                DbStructureOutcome noMapOutcome = new()
                {
                    Key = DbStructureOutcome.GetIndex(),
                    SourceFhirPackageKey = sourcePackage.Key,
                    TargetFhirPackageKey = targetPackage.Key,
                    SourceStructureKey = sourceSd.Key,
                    SourceStructureName = sourceSd.Name,
                    SourceArtifactClass = sourceSd.ArtifactClass,
                    TargetStructureKey = null,
                    TargetStructureName = null,

                    PotentialGenResourceType = "StructureDefinition",
                    PotentialGenLongId = idLong,
                    PotentialGenShortId = idShort,
                    PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/StructureDefinition/{idLong}",
                    OutcomeAction = null,

                    TotalSourceCount = 1,
                    TotalTargetCount = 0,

                    IsRenamed = false,
                    IsUnmapped = true,
                    IsIdentical = false,
                    IsEquivalent = false,
                    IsBroaderThanTarget = false,
                    IsNarrowerThanTarget = false,
                    FullyMapsToThisTarget = false,
                    FullyMapsAcrossAllTargets = false,
                    ConceptDomainRelationship = null,
                    ValueDomainRelationship = null,

                    Comments = 
                        $"The FHIR {sourcePackage.ShortName} StructureDefinition ({sourceSd.ArtifactClass.ToString()})" +
                        $" `{sourceSd.UnversionedUrl}|{sourceSd.Version}` (`{sourceSd.Id}`) has no mapping to FHIR {targetPackage.ShortName}.",
                };

                // track to add to the database
                sdOutcomesToAdd.Add(noMapOutcome);
                continue;
            }

            // get the elements for this structure - sort by resource order so that ancestor elements come first
            List<DbElement> sourceElements = DbElement.SelectList(
                _db.DbConnection,
                StructureKey: sourceSd.Key,
                orderByProperties: [ nameof(DbElement.ResourceFieldOrder) ]);

            int[] sdOutcomeKeys = new int[sdComparisons.Count];
            for (int i = 0; i < sdComparisons.Count; i++)
            {
                sdOutcomeKeys[i] = DbStructureOutcome.GetIndex();
            }

            HashSet<int> elementsThatFullyMapToAnyTarget = [];
            List<DbStructureOutcome> sdOutcomesForThisSource = [];

            bool multipleTargets = sdComparisons.Count(c => c.TargetContentKey is not null) > 1;

            // iterate over each comparison for this structure
            foreach ((DbStructureComparison sdComparison, int comparisonIndex) in sdComparisons.Select((c, i) => (c, i)))
            {
                HashSet<int> elementsThatFullyMapToThisTarget = [];

                DbStructureDefinition? targetSd = DbStructureDefinition.SelectSingle(
                    _db.DbConnection,
                    Key: sdComparison.TargetStructureKey);

                if ((targetSd is null) ||
                    (sdComparison.TargetStructureKey is null))
                {
                    throw new Exception(
                        $"Structure comparison for source StructureDefinition {sourceSd.Id} " +
                        $"has invalid target Structure key {sdComparison.TargetStructureKey}!");
                }

                // generated ids need to have target artifacts if there are multiple targets
                if (multipleTargets)
                {
                    (idLong, idShort) = GenerateArtifactId(
                        sourcePackage.ShortName,
                        sourceSd.Id,
                        targetPackage.ShortName,
                        targetSd.Id);
                }

                // iterate over the elements in this structure
                foreach (DbElement sourceElement in sourceElements)
                {
                    (string elementIdLong, string elementIdShort) = generateExtensionId(
                        sourcePackage.ShortName,
                        sourceElement.Path);

                    // check for element comparisons for this source element
                    List<DbElementComparison> elementComparisons = DbElementComparison.SelectList(
                        _db.DbConnection,
                        StructureComparisonKey: sdComparison.Key,
                        SourceElementKey: sourceElement.Key);

                    // shortcut if there are no element comparisons
                    if (elementComparisons.Count == 0)
                    {
                        DbElementOutcome noMapOutcome = new()
                        {
                            Key = DbElementOutcome.GetIndex(),
                            SourceFhirPackageKey = sourcePackage.Key,
                            TargetFhirPackageKey = targetPackage.Key,
                            SourceStructureKey = sourceSd.Key,
                            SourceElementKey = sourceElement.Key,
                            SourceElementId = sourceElement.Id,
                            SourceResourceOrder = sourceElement.ResourceFieldOrder,
                            SourceComponentOrder = sourceElement.ComponentFieldOrder,

                            TargetStructureKey = targetSd.Key,
                            TargetElementKey = null,
                            TargetElementId = null,
                            TargetResourceOrder = null,
                            TargetComponentOrder = null,

                            ExtensionSubstitutionKey = null,
                            RelatedAncestorOutcomeKey = null,

                            StructureOutcomeKey = sdOutcomeKeys[comparisonIndex],

                            PotentialGenResourceType = "StructureDefinition",
                            PotentialGenLongId = elementIdLong,
                            PotentialGenShortId = elementIdShort,
                            PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/StructureDefinition/{elementIdLong}",
                            OutcomeAction = null,

                            TotalSourceCount = 1,
                            TotalTargetCount = 0,

                            IsRenamed = false,
                            IsUnmapped = true,
                            IsIdentical = false,
                            IsEquivalent = false,
                            IsBroaderThanTarget = false,
                            IsNarrowerThanTarget = false,
                            FullyMapsToThisTarget = false,
                            FullyMapsAcrossAllTargets = false,
                            ConceptDomainRelationship = null,
                            ValueDomainRelationship = null,

                            Comments = 
                                $"The FHIR {sourcePackage.ShortName} {(sourceElement.ResourceFieldOrder == 0 ? "root " : string.Empty)}element" +
                                $" `{sourceElement.Id}` has no mapping to FHIR {targetPackage.ShortName}.",
                        };

                        elementOutcomesToAdd.Add(noMapOutcome);
                        continue;
                    }

                    bool multipleContentTargets = elementComparisons.Count(c => c.TargetContentKey is not null) > 1;

                    // iterate over the element comparisons to build element elementOutcomes
                    foreach (DbElementComparison elementComparison in elementComparisons)
                    {
                        // can have comparison that is unmapped
                        if (elementComparison.TargetElementKey is null)
                        {
                            DbElementOutcome noMapOutcome = new()
                            {
                                Key = DbElementOutcome.GetIndex(),
                                SourceFhirPackageKey = sourcePackage.Key,
                                TargetFhirPackageKey = targetPackage.Key,
                                SourceStructureKey = sourceSd.Key,
                                SourceElementKey = sourceElement.Key,
                                SourceElementId = sourceElement.Id,
                                SourceResourceOrder = sourceElement.ResourceFieldOrder,
                                SourceComponentOrder = sourceElement.ComponentFieldOrder,

                                TargetStructureKey = targetSd.Key,
                                TargetElementKey = null,
                                TargetElementId = null,
                                TargetResourceOrder = null,
                                TargetComponentOrder = null,

                                StructureOutcomeKey = sdOutcomeKeys[comparisonIndex],

                                PotentialGenResourceType = "StructureDefinition",
                                PotentialGenLongId = elementIdLong,
                                PotentialGenShortId = elementIdShort,
                                PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/StructureDefinition/{elementIdLong}",
                                OutcomeAction = null,

                                ExtensionSubstitutionKey = null,
                                RelatedAncestorOutcomeKey = null,

                                TotalSourceCount = 1,
                                TotalTargetCount = 0,

                                IsRenamed = false,
                                IsUnmapped = true,
                                IsIdentical = false,
                                IsEquivalent = false,
                                IsBroaderThanTarget = false,
                                IsNarrowerThanTarget = false,
                                FullyMapsToThisTarget = false,
                                FullyMapsAcrossAllTargets = false,
                                ConceptDomainRelationship = elementComparison.ConceptDomainRelationship,
                                ValueDomainRelationship = elementComparison.ValueDomainRelationship,

                                Comments =
                                    $"The FHIR {sourcePackage.ShortName} {(sourceElement.ResourceFieldOrder == 0 ? "root " : string.Empty)}element" +
                                    $" `{sourceElement.Id}` has no mapping to FHIR {targetPackage.ShortName}.",
                            };

                            elementOutcomesToAdd.Add(noMapOutcome);
                            continue;
                        }

                        // resolve the target element
                        DbElement? targetElement = DbElement.SelectSingle(
                            _db.DbConnection,
                            Key: elementComparison.TargetElementKey);

                        if (targetElement is null)
                        {
                            throw new Exception(
                                $"Element comparison for source element `{sourceElement.Id}` " +
                                $"has invalid target element key {elementComparison.TargetElementKey}!");
                        }

                        // check for equivalency
                        if ((elementComparison.Relationship == CMR.Equivalent) ||
                            (elementComparison.Relationship == CMR.SourceIsNarrowerThanTarget))
                        {
                            elementsThatFullyMapToAnyTarget.Add(sourceElement.Key);
                            elementsThatFullyMapToThisTarget.Add(sourceElement.Key);
                        }

                        DbElementOutcome elementOutcome = new()
                        {
                            Key = DbElementOutcome.GetIndex(),
                            SourceFhirPackageKey = sourcePackage.Key,
                            TargetFhirPackageKey = targetPackage.Key,
                            SourceStructureKey = sourceSd.Key,
                            SourceElementKey = sourceElement.Key,
                            SourceElementId = sourceElement.Id,
                            SourceResourceOrder = sourceElement.ResourceFieldOrder,
                            SourceComponentOrder = sourceElement.ComponentFieldOrder,

                            TargetStructureKey = targetSd.Key,
                            TargetElementKey = elementComparison.TargetElementKey,
                            TargetElementId = targetElement.Id,
                            TargetResourceOrder = targetElement.ResourceFieldOrder,
                            TargetComponentOrder = targetElement.ComponentFieldOrder,

                            StructureOutcomeKey = sdOutcomeKeys[comparisonIndex],

                            PotentialGenResourceType = "StructureDefinition",
                            PotentialGenLongId = elementIdLong,
                            PotentialGenShortId = elementIdShort,
                            PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/StructureDefinition/{elementIdLong}",
                            OutcomeAction = null,

                            ExtensionSubstitutionKey = null,
                            RelatedAncestorOutcomeKey = null,

                            TotalSourceCount = 1,
                            TotalTargetCount = elementComparisons.Where(ec => ec.TargetElementKey is not null).Count(),

                            IsRenamed = !multipleContentTargets &&
                                ((elementComparison.Relationship == CMR.Equivalent) || (elementComparison.Relationship == CMR.SourceIsNarrowerThanTarget)) &&
                                (sourceElement.Name != targetElement.Name),
                            IsUnmapped = false,
                            IsIdentical = elementComparison.IsIdentical == true,
                            IsEquivalent = elementComparison.Relationship == CMR.Equivalent,
                            IsBroaderThanTarget = elementComparison.Relationship == CMR.SourceIsBroaderThanTarget,
                            IsNarrowerThanTarget = elementComparison.Relationship == CMR.SourceIsNarrowerThanTarget,

                            FullyMapsToThisTarget = (elementComparison.Relationship == CMR.Equivalent) || (elementComparison.Relationship == CMR.SourceIsNarrowerThanTarget),
                            FullyMapsAcrossAllTargets = elementComparisons.All(ec => ec.Relationship != CMR.SourceIsBroaderThanTarget),

                            ConceptDomainRelationship = elementComparison.ConceptDomainRelationship,
                            ValueDomainRelationship = elementComparison.ValueDomainRelationship,

                            Comments = 
                                $"The FHIR {sourcePackage.ShortName} {(sourceElement.ResourceFieldOrder == 0 ? "root " : string.Empty)}element" +
                                $" `{sourceElement.Id}` maps to FHIR {targetPackage.ShortName} element `{targetElement.Id}`" +
                                $" with relationship `{elementComparison.Relationship.GetLiteral()}`.",
                        };

                        elementOutcomesToAdd.Add(elementOutcome);
                    }
                }

                // generate our structure elementOutcome
                DbStructureOutcome outcome = new()
                {
                    Key = sdOutcomeKeys[comparisonIndex],
                    SourceFhirPackageKey = sourcePackage.Key,
                    TargetFhirPackageKey = targetPackage.Key,
                    SourceStructureKey = sourceSd.Key,
                    SourceStructureName = sourceSd.Name,
                    SourceArtifactClass = sourceSd.ArtifactClass,
                    TargetStructureKey = targetSd?.Key,
                    TargetStructureName = targetSd?.Name,

                    PotentialGenResourceType = "StructureDefinition",
                    PotentialGenLongId = idLong,
                    PotentialGenShortId = idShort,
                    PotentialGenUrl = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/StructureDefinition/{idLong}",
                    OutcomeAction = null,

                    TotalSourceCount = 1,
                    TotalTargetCount = sdComparisons.Count,

                    IsRenamed = !multipleTargets &&
                        ((sdComparison.Relationship == CMR.Equivalent) || (sdComparison.Relationship == CMR.SourceIsNarrowerThanTarget)) &&
                        (sourceSd.Id != targetSd?.Id),
                    IsUnmapped = false,
                    IsIdentical = sdComparison.IsIdentical == true,
                    IsEquivalent = sdComparison.Relationship == CMR.Equivalent,
                    IsBroaderThanTarget = sdComparison.Relationship == CMR.SourceIsBroaderThanTarget,
                    IsNarrowerThanTarget = sdComparison.Relationship == CMR.SourceIsNarrowerThanTarget,
                    FullyMapsToThisTarget = elementsThatFullyMapToThisTarget.Count == sourceElements.Count,
                    FullyMapsAcrossAllTargets = false,      // need to finish processing to update this value

                    ConceptDomainRelationship = sdComparison.ConceptDomainRelationship,
                    ValueDomainRelationship = sdComparison.ValueDomainRelationship,

                    Comments = sdComparison.UserMessage ?? sdComparison.TechnicalMessage ?? string.Empty,
                };

                sdOutcomesToAdd.Add(outcome);
                sdOutcomesForThisSource.Add(outcome);
            }

            // determine if fully mapped - default is false, so only update if true
            if (elementsThatFullyMapToAnyTarget.Count == sourceElements.Count)
            {
                // iterate over our elementOutcomes for this source to update the fully-mapped status
                foreach (DbStructureOutcome sdOutcome in sdOutcomesForThisSource)
                {
                    sdOutcome.FullyMapsAcrossAllTargets = true;
                }
            }
        }

        // update our elementOutcomes with action codes
        updateStructureOutcomeActions(sdOutcomesToAdd, targetBasicSd, sourcePackage, targetPackage);
        updateElementOutcomeActions(elementOutcomesToAdd, sdOutcomesToAdd, targetBasicSd, sourcePackage, targetPackage);

        // insert our elementOutcomes into the database
        sdOutcomesToAdd.Insert(_db.DbConnection, insertPrimaryKey: true);
        _logger.LogInformation(
            $"Inserted {sdOutcomesToAdd.Count} Structure outcomes for source package {sourcePackage.ShortName} to target package {targetPackage.ShortName}.");

        elementOutcomesToAdd.Insert(_db.DbConnection, insertPrimaryKey: true);
        _logger.LogInformation(
            $"Inserted {elementOutcomesToAdd.Count} Element outcomes for source package {sourcePackage.ShortName} to target package {targetPackage.ShortName}.");
#endif
    }


    private void writeXverOutcomes(
        List<PackageXverSupport> packageSupports,
        Dictionary<(int, string), List<List<XverOutcome>>> xverOutcomes,
        List<XverPackageIndexInfo> indexInfos,
        string outputDir)
    {
        HashSet<string> createdDirs = [];

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        string fhirDir = Path.Combine(outputDir, "fhir");
        if (!Directory.Exists(fhirDir))
        {
            Directory.CreateDirectory(fhirDir);
        }

        string docsDir = Path.Combine(outputDir, "docs");
        if (!Directory.Exists(docsDir))
        {
            Directory.CreateDirectory(docsDir);
        }

        ILookup<(int sourcePackageKey, int targetPackageKey), XverPackageIndexInfo> indexInfoLookup =
            indexInfos.ToLookup(ii => (ii.SourcePackageSupport.PackageIndex, ii.TargetPackageSupport.PackageIndex));

        Dictionary<string, (string sourceVersion, string targetVersion)> packageVersions = [];

        // iterate over each structure in each source package
        foreach (((int sourcePackageIndex, string sourceStructureName), List<List<XverOutcome>> structureOutcomesByTarget) in xverOutcomes)
        {
            // iterate across each of our targets
            foreach ((List<XverOutcome> outcomes, int targetPackageIndex) in structureOutcomesByTarget.Select((ol, i) => (ol, i)))
            {
                if (sourcePackageIndex == targetPackageIndex)
                {
                    continue;
                }

                XverPackageIndexInfo? indexInfo = indexInfoLookup[(sourcePackageIndex, targetPackageIndex)].FirstOrDefault();

                if (indexInfo is null)
                {
                    _logger.LogWarning(
                        "No XVer index info found for source package index {SourcePackageIndex} and target package index {TargetPackageIndex}.",
                        sourcePackageIndex,
                        targetPackageIndex);
                    continue;
                }

                DbFhirPackage sourcePackage = packageSupports[sourcePackageIndex].Package;
                DbFhirPackage targetPackage = packageSupports[targetPackageIndex].Package;

                string packageId = getPackageId(sourcePackage, targetPackage);
                string htmlDir;
                string mdDir;

                if (createdDirs.Contains(packageId))
                {
                    if (_config.XverExportForPublisher)
                    {
                        htmlDir = string.Empty;
                        mdDir = Path.Combine(fhirDir, packageId, "input", "pagecontent");
                    }
                    else
                    {
                        htmlDir = Path.Combine(fhirDir, packageId, "package", "doc");
                        mdDir = Path.Combine(docsDir, packageId);
                    }
                }
                else
                {
                    packageVersions.Add(packageId, (sourcePackage.PackageVersion, targetPackage.PackageVersion));

                    if (_config.XverExportForPublisher)
                    {
                        htmlDir = string.Empty;
                        mdDir = Path.Combine(fhirDir, packageId, "input", "pagecontent");
                    }
                    else
                    {
                        htmlDir = Path.Combine(fhirDir, packageId);
                        if (!Directory.Exists(htmlDir))
                        {
                            Directory.CreateDirectory(htmlDir);
                        }

                        htmlDir = Path.Combine(htmlDir, "package");
                        if (!Directory.Exists(htmlDir))
                        {
                            Directory.CreateDirectory(htmlDir);
                        }

                        htmlDir = Path.Combine(htmlDir, "doc");
                        if (!Directory.Exists(htmlDir))
                        {
                            Directory.CreateDirectory(htmlDir);
                        }

                        mdDir = Path.Combine(docsDir, packageId);
                        if (!Directory.Exists(mdDir))
                        {
                            Directory.CreateDirectory(mdDir);
                        }
                    }

                    createdDirs.Add(packageId);
                }

                if (_config.XverExportForPublisher)
                {
                    string sourceBaseUrl = sourcePackage.DefinitionFhirSequence.ToWebUrlRoot();
                    string targetBaseUrl = targetPackage.DefinitionFhirSequence.ToWebUrlRoot();

                    // create a filename for this structure's md file
                    string mdFilename = $"Lookup-{sourcePackage.ShortName}-{sourceStructureName}-{targetPackage.ShortName}.md";

                    indexInfo.ResourceLookupFiles.Add(new()
                    {
                        FileName = mdFilename,
                        FileNameWithoutExtension = mdFilename[..^3],
                        IsPageContentFile = true,
                        Name = sourceStructureName,
                        Id = sourceStructureName,
                        Url = string.Empty,
                        ResourceType = "StructureDefinition",
                        Version = _crossDefinitionVersion,
                        Description = $"Lookup for FHIR {sourcePackage.ShortName} {sourceStructureName} for use in FHIR {targetPackage.ShortName}"
                    });

                    // open our files
                    using ExportStreamWriter mdWriter = createMarkdownWriter(Path.Combine(mdDir, mdFilename), false, false);

                    // write a header
                    mdWriter.WriteLine(
                        $"### Lookup for [FHIR {sourcePackage.ShortName}]({sourceBaseUrl})" +
                        $" [{sourceStructureName}]({sourceBaseUrl}{sourceStructureName}.html)" +
                        $" for use in [FHIR {targetPackage.ShortName}]({targetBaseUrl})");

                    mdWriter.WriteLine();
                    mdWriter.WriteLine($"| Source Element (FHIR {sourcePackage.ShortName}) | Usage | Target |");
                    mdWriter.WriteLine("| -------------- | ----- | ------ |");

                    // iterate over the elements of this structure in element order
                    foreach (XverOutcome outcome in outcomes.OrderBy(xo => xo.SourceElementFieldOrder))
                    {
                        string target = outcome.OutcomeCode switch
                        {
                            XverOutcomeCodes.UseElementSameName => $"[{outcome.TargetElementId}]({targetBaseUrl}{outcome.TargetElementId!.Split('.')[0]}.html#resource)",
                            XverOutcomeCodes.UseElementRenamed => $"[{outcome.TargetElementId}]({targetBaseUrl}{outcome.TargetElementId!.Split('.')[0]}.html#resource)",
                            XverOutcomeCodes.UseExtension => outcome.ReplacementExtensionUrl != null
                                ? $"[{outcome.ReplacementExtensionUrl}]({outcome.ReplacementExtensionUrl})"
                                : $"[{outcome.TargetExtensionUrl}](StructureDefinition-{outcome.TargetExtensionId}.html)",
                            XverOutcomeCodes.UseExtensionFromAncestor => "-",
                            XverOutcomeCodes.UseBasicElement => $"[{outcome.TargetElementId}]({targetBaseUrl}{outcome.TargetElementId!.Split('.')[0]}.html#resource)",
                            XverOutcomeCodes.UseOneOf => publisherOneOfText(outcome, targetBaseUrl),
                            _ => "-"
                        };

                        //string target = elementOutcome.ReplacementExtensionUrl ?? elementOutcome.TargetElementId ?? elementOutcome.TargetExtensionUrl ?? "-";
                        mdWriter.WriteLine(
                            $"| [{outcome.SourceElementId}]({sourceBaseUrl}{sourceStructureName}.html#resource) " +
                            $"| `{outcome.OutcomeCode}` " +
                            $"| {target} " +
                            $"|");
                    }

                    mdWriter.Close();
                }
                else
                {
                    // create a filename for this structure's md file
                    string mdFilename = $"Lookup-{sourcePackage.ShortName}-{sourceStructureName}-{targetPackage.ShortName}.md";
                    string htmlFilename = $"Lookup-{sourcePackage.ShortName}-{sourceStructureName}-{targetPackage.ShortName}.html";

                    // open our files
                    using ExportStreamWriter mdWriter = createMarkdownWriter(Path.Combine(mdDir, mdFilename), false, false);
                    using ExportStreamWriter htmlWriter = createHtmlWriter(Path.Combine(htmlDir, htmlFilename), false, false);

                    // write a header
                    mdWriter.WriteLine($"### Lookup for FHIR {sourcePackage.ShortName} {sourceStructureName} for use in FHIR {targetPackage.ShortName}");
                    htmlWriter.WriteLine($"<h2>Lookup for FHIR {sourcePackage.ShortName} {sourceStructureName} for use in FHIR {targetPackage.ShortName}</h2>");

                    mdWriter.WriteLine();
                    mdWriter.WriteLine("| Source Element | Usage | Target |");
                    mdWriter.WriteLine("| -------------- | ----- | ------ |");

                    htmlWriter.WriteLine();
                    htmlWriter.WriteLine("<table border=\"1\">");
                    htmlWriter.WriteLine("<tr><th>Source Element</th><th>Usage</th><th>Target</th></tr>");

                    // iterate over the elements of this structure in element order
                    foreach (XverOutcome outcome in outcomes.OrderBy(xo => xo.SourceElementFieldOrder))
                    {
                        string target = outcome.ReplacementExtensionUrl ?? outcome.TargetElementId ?? outcome.TargetExtensionUrl ?? "-";
                        mdWriter.WriteLine($"| {outcome.SourceElementId} | {outcome.OutcomeCode} | {target} |");
                        htmlWriter.WriteLine($"<tr><td>{outcome.SourceElementId}</td><td>{outcome.OutcomeCode}</td><td>{target}</td></tr>");
                    }

                    htmlWriter.WriteLine("</table>");

                    mdWriter.Close();
                    htmlWriter.Close();
                }
            }
        }

        // if we are exporting for the publisher, we need to create a single comparisonIndex file per package
        if (_config.XverExportForPublisher)
        {
            // iterate across each of our targets
            foreach (XverPackageIndexInfo indexInfo in indexInfos)
            {
                if (indexInfo.ResourceLookupFiles.Count == 0)
                {
                    continue; // no files for this package, skip it
                }

                string packageId = indexInfo.PackageId;
                string sourceVersion = indexInfo.SourcePackageSupport.Package.PackageVersion;
                string targetVersion = indexInfo.TargetPackageSupport.Package.PackageVersion;

                // create the comparisonIndex file
                using ExportStreamWriter indexWriter = createMarkdownWriter(Path.Combine(fhirDir, packageId, "input", "pagecontent", "lookup.md"), false, false);
                indexWriter.WriteLine($"### FHIR {packageId} Cross-Version Artifact Lookup");
                indexWriter.WriteLine();
                indexWriter.WriteLine("The following table links to documentation for the source version of FHIR, for implementers to understand if there is an extension for the element they are trying to use.");
                indexWriter.WriteLine($"These are structures defined in FHIR {sourceVersion} (the source package), with applicable usage as mapped into FHIR {targetVersion} (the target package).");
                indexWriter.WriteLine();
                indexWriter.WriteLine($"| {sourceVersion} Structure | Lookup File |");
                indexWriter.WriteLine("| --------- | ----------- |");

                foreach (XVerIgFileRecord fileRec in indexInfo.ResourceLookupFiles)
                {
                    indexWriter.WriteLine($"| {fileRec.Name} | [{fileRec.FileNameWithoutExtension}]({fileRec.FileNameWithoutExtension}.html) |");
                }

                indexWriter.Close();
            }


            //foreach ((string packageId, List<(string structureName, string filename)> mdFiles) in packageMdList)
            //{
            //    if (mdFiles.Count == 0)
            //    {
            //        continue; // no files for this package, skip it
            //    }

            //    (string sourceVersion, string targetVersion) = packageVersions[packageId];

            //    // create the comparisonIndex file
            //    using ExportStreamWriter indexWriter = createMarkdownWriter(Contents.Combine(fhirDir, packageId, "input", "pagecontent", "lookup.md"), false, false);
            //    indexWriter.WriteLine($"### FHIR {packageId} Cross-Version Artifact Lookup");
            //    indexWriter.WriteLine();
            //    indexWriter.WriteLine("The following table links to documentation for the source version of FHIR, for implementers to understand if there is an extension for the element they are trying to use.");
            //    indexWriter.WriteLine($"These are structures defined in FHIR {sourceVersion} (the source package), with applicable usage as mapped into FHIR {targetVersion} (the target package).");
            //    indexWriter.WriteLine();
            //    indexWriter.WriteLine($"| {sourceVersion} Structure | Lookup File |");
            //    indexWriter.WriteLine("| --------- | ----------- |");

            //    foreach ((string structureName, string filename) in mdFiles.OrderBy(x => x.structureName))
            //    {
            //        indexWriter.WriteLine($"| {structureName} | [{filename}]({filename}.html) |");
            //    }

            //    indexWriter.Close();
            //}
        }

        return;

        string publisherOneOfText(XverOutcome outcome, string targetBaseUrl)
        {
            string[] oneOfElements = outcome.TargetElementId?.Split(',') ?? [];

            return string.Join("<br />", oneOfElements.Select(e => $"[{e}]({targetBaseUrl}{e.Split('.')[0]}.html#resource)")) +
                   (outcome.TargetExtensionUrl != null ? $"<br/>[{outcome.TargetExtensionUrl}]({outcome.TargetExtensionUrl})" : "");
        }
    }
}
