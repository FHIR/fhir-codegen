// <copyright file="DefinitionCollection.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Xml.Linq;
using Fhir.CodeGen.Lib.Packaging;
using Fhir.Metrics;
using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Terminology;
using Microsoft.Extensions.Logging;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.TerminologyService;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;

using Fhir.CodeGen.Common.Polyfill;
using static Fhir.CodeGen.Lib.FhirExtensions.StructureDefinitionExtensions;
using Hl7.Fhir.Utility;

namespace Fhir.CodeGen.Lib.Models;

internal static partial class DefinitionCollectionLogMessages
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to generate snapshot for {url} ({name}): {details}")]
    internal static partial void LogSnapshotFailed(this ILogger logger, string url, string name, string details);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve bindings in {sdId} for extension {url}, definition: {bindingType}:{expression}")]
    internal static partial void LogElementBindingExtUnhandledType(this ILogger logger, string sdId, string url, string? bindingType, string expression);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error expanding {url}: {exMessage}: {innerMessage}")]
    internal static partial void LogExpandError(this ILogger logger, string url, string exMessage, string? innerMessage = null);

}

/// <summary>A FHIR package and its contents.</summary>
public partial class DefinitionCollection
{
    /// <summary>Gets or initializes the logger.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Gets or sets the name.</summary>
    public required string Name { get; set; }

    ///// <summary>Gets or sets the transmit servers.</summary>
    //public string[] TxServers { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the FHIR version.</summary>
    public FHIRVersion? FhirVersion { get; set; } = null;

    /// <summary>Gets or sets the FHIR version literal.</summary>
    public string FhirVersionLiteral { get; set; } = string.Empty;

    public FhirReleases.FhirSequenceCodes FhirSequence { get; set; } = FhirReleases.FhirSequenceCodes.Unknown;

    /// <summary>Gets or sets the identifier of the main package.</summary>
    public string MainPackageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the main package canonical.</summary>
    public string MainPackageCanonical { get; set; } = string.Empty;

    /// <summary>Gets or sets the main package version.</summary>
    public string MainPackageVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the index.</summary>
    public Dictionary<PackageIdentity, CodeGenPackageManifest> Manifests { get; set; } = [];

    /// <summary>Gets or sets the contents.</summary>
    public Dictionary<PackageIdentity, CodeGenPackageIndex> ContentListings { get; set; } = [];

    //private readonly Dictionary<ElementDefinition, StructureDefinition> _elementSdLookup = new();

    private readonly Dictionary<string, StructureDefinition> _primitiveTypesByName = [];
    private readonly Dictionary<string, StructureDefinition> _complexTypesByName = [];
    private readonly Dictionary<string, StructureDefinition> _resourcesByName = [];
    private readonly Dictionary<string, StructureDefinition> _interfacesByName = [];
    private readonly Dictionary<string, StructureDefinition> _logicalModelsByUrl = [];
    private readonly Dictionary<string, StructureDefinition> _extensionsByUrl = [];
    private readonly Dictionary<string, Dictionary<string, StructureDefinition>> _extensionsByPath = [];
    private readonly Dictionary<string, StructureDefinition> _profilesByUrl = [];
    private readonly Dictionary<string, Dictionary<string, StructureDefinition>> _profilesByBaseType = [];

    private readonly Dictionary<string, OperationDefinition> _systemOperations = [];
    private readonly Dictionary<string, OperationDefinition> _operationsByUrl = [];
    private readonly Dictionary<string, Dictionary<string, OperationDefinition>> _typeOperationsByType = [];
    private readonly Dictionary<string, Dictionary<string, OperationDefinition>> _instanceOperationsByType = [];

    private readonly Dictionary<string, SearchParameter> _globalSearchParameters = [];
    private readonly Dictionary<string, FhirQueryParameter> _searchResultParameters = [];
    private readonly Dictionary<string, FhirQueryParameter> _allInteractionParameters = [];
    private readonly Dictionary<string, SearchParameter> _searchParamsByUrl = [];
    private readonly Dictionary<string, Dictionary<string, SearchParameter>> _searchParamsByBase = [];

    private readonly Dictionary<string, CodeSystem> _codeSystemsByUrl = [];
    private readonly Dictionary<string, ValueSet> _valueSetsByVersionedUrl = [];
    private readonly Dictionary<string, string[]> _valueSetVersions = [];
    private readonly Dictionary<string, string> _valueSetUrlsById = [];

    private readonly Dictionary<string, ConceptMap> _conceptMapsByUrl = [];
    private readonly Dictionary<string, List<ConceptMap>> _conceptMapsBySourceUrl = [];
    private readonly Dictionary<string, List<ConceptMap>> _conceptMapsByTargetUrl = [];
    private readonly Dictionary<string, StructureMap> _structureMapsByUrl = [];

    private readonly Dictionary<string, List<StructureElementCollection>> _coreBindingEdsByPathByValueSet = [];
    private readonly Dictionary<string, List<StructureElementCollection>> _extendedBindingEdsByPathByValueSet = [];

    private readonly Dictionary<string, ImplementationGuide> _implementationGuidesByUrl = [];
    private readonly Dictionary<string, CapabilityStatement> _capabilityStatementsByUrl = [];
    private readonly Dictionary<string, CompartmentDefinition> _compartmentsByUrl = [];

    internal readonly Dictionary<string, string> _parentElementsAndType = [];

    private readonly List<string> _errors = [];

    /// <summary>(Immutable) all resources.</summary>
    private readonly Dictionary<string, Resource> _allResources = [];

    /// <summary>(Immutable) The canonical resources.</summary>
    private readonly Dictionary<string, Dictionary<string, IConformanceResource>> _canonicalResources = [];

    /// <summary>(Immutable) The local transmit.</summary>
    private readonly CodeGenTerminologyService _localTx;

    private readonly static HashSet<string> _notActuallyLimitedExpansions = [
        "http://hl7.org/fhir/ValueSet/age-units",
        "http://hl7.org/fhir/ValueSet/units-of-time",
        "http://hl7.org/fhir/ValueSet/event-timing",
        "http://hl7.org/fhir/ValueSet/timezones",
        "http://hl7.org/fhir/ValueSet/languages",
        "http://hl7.org/fhir/ValueSet/currencies",
    ];

    private static readonly HashSet<string> _valueSetsWithIncorrectExpansions = [
        "http://hl7.org/fhir/ValueSet/security-labels|4.0.1"
    ];

    static DefinitionCollection()
    {
        Hl7.Fhir.Model.ViewDefinition.RegisterInFhir();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DefinitionCollection"/> class.
    /// </summary>
    public DefinitionCollection(
        ConfigRoot? rootConfig = null)
    {
        CodeGenValueSetExpanderSettings valueSetExpanderSettings = new()
        {
            Definitions = this,
            IncludeDesignations = false,
            //IncludeNotSelectable = rootConfig?.ExpandIncludeNotSelectable,
            MaxExpansionSize = rootConfig?.MaxExpansionSize ?? ConfigRoot.DefaultMaxExpansionSize,
            //ActiveOnly = rootConfig?.ExpandActiveOnly,
        };

        _localTx = new CodeGenTerminologyService(valueSetExpanderSettings);
    }

    public void AddManifest(PackageIdentity identity, CodeGenPackageManifest manifest)
    {
        Manifests[identity] = manifest;
    }

    public void AddContentListing(PackageIdentity identity, CodeGenPackageIndex index)
    {
        if (string.IsNullOrEmpty(identity.Version))
        {
            throw new Exception($"Cannot track contents for a package with no version!");
        }

        ContentListings[identity] = index;
    }

    public CodeGenPackageManifest? GetManifest(string packageId, string version)
    {
        if (Manifests.TryGetValue(new PackageIdentity(packageId, version), out CodeGenPackageManifest? pm))
        {
            return pm;
        }

        foreach (KeyValuePair<PackageIdentity, CodeGenPackageManifest> kvp in Manifests)
        {
            if (!kvp.Key.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PackageVersionMatcher.Satisfies(kvp.Key.Version, version))
            {
                return kvp.Value;
            }

            if (PackageVersionMatcher.Satisfies(kvp.Value.Version, version))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    public CodeGenPackageManifest? GetManifest(PackageIdentity identity) => GetManifest(identity.Id, identity.Version);

    public bool TryGetManifest(string packageId, string version, [NotNullWhen(true)] out CodeGenPackageManifest? pm)
    {
        pm = GetManifest(packageId, version);
        return pm is not null;
    }

    public bool TryGetManifest(PackageIdentity identity, [NotNullWhen(true)] out CodeGenPackageManifest? pm)
    {
        pm = GetManifest(identity);
        return pm is not null;
    }

    public string Key => $"{MainPackageId}@{MainPackageVersion}";

    /// <summary>Query if 'path' has child elements.</summary>
    /// <param name="path">Dot-notation path to the element.</param>
    /// <returns>True if the path contains child elements, false if not.</returns>
    public bool HasChildElements(string path) => _parentElementsAndType.ContainsKey(path);

    /// <summary>Query if 'path' is backbone element.</summary>
    /// <param name="path">Dot-notation path to the element.</param>
    /// <returns>True if backbone element, false if not.</returns>
    public bool IsBackboneElement(string path) =>
        _parentElementsAndType.TryGetValue(path, out string? type) &&
        ((type == "BackboneElement") || (type == "Element"));

    public async System.Threading.Tasks.Task<bool> TryGenerateMissingSnapshots()
    {
        bool success = true;
        Hl7.Fhir.Specification.Snapshot.SnapshotGenerator snapshotGenerator = new(this);

        await CreateSnapshots(FhirArtifactClassEnum.ComplexType, _complexTypesByName.Values);
        await CreateSnapshots(FhirArtifactClassEnum.Resource, _resourcesByName.Values);
        await CreateSnapshots(FhirArtifactClassEnum.Interface, _interfacesByName.Values);
        await CreateSnapshots(FhirArtifactClassEnum.Extension, _extensionsByUrl.Values);
        await CreateSnapshots(FhirArtifactClassEnum.Profile, _profilesByUrl.Values);

        return success;

        async System.Threading.Tasks.Task CreateSnapshots(
            FhirArtifactClassEnum artifactClass,
            IEnumerable<StructureDefinition> sds)
        {
            foreach (StructureDefinition sd in sds)
            {
                try
                {
                    if (sd.Snapshot == null)
                    {
                        // create a new snapshot
                        sd.Snapshot = new StructureDefinition.SnapshotComponent();
                    }

                    // a valid snapshot will always have at least the root element
                    if (sd.Snapshot.Element.Count == 0)
                    {
                        sd.Snapshot.Element = await snapshotGenerator.GenerateAsync(sd);

                        if (sd.Snapshot.Element.Count == 0)
                        {
                            success = false;
                            Logger.LogSnapshotFailed(sd.Url, sd.Name, "Generation resulted in no elements.");
                            continue;
                        }
                    }

                    // if we are in DSTU2, we need to check some of the generated snapshot properties
                    if ((FhirSequence == FhirReleases.FhirSequenceCodes.DSTU2) &&
                        (sd.Differential?.Element.Count > 0) &&
                        (sd.Snapshot?.Element.Count > 0))
                    {
                        // make a lookup of the differential
                        ILookup<string, ElementDefinition> diffElementLookup = sd.Differential.Element.ToLookup(e => e.ElementId);

                        foreach (ElementDefinition ed in sd.Snapshot.Element)
                        {
                            if (diffElementLookup.Contains(ed.ElementId))
                            {
                                // get the differential element
                                ElementDefinition ded = diffElementLookup[ed.ElementId].First();

                                // check for incorrect minium value
                                if ((ded.Min != null) && (ed.Min != ded.Min))
                                {
                                    ed.Min = ded.Min;
                                }

                                // check for incorrect maximum value
                                if ((ded.Max != null) && (ed.Max != ded.Max))
                                {
                                    ed.Max = ded.Max;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Logger.LogSnapshotFailed(sd.Url, sd.Name, ex.InnerException == null ? ex.Message : ex.Message + " - " + ex.InnerException.Message);
                    continue;
                }

                // reprocess the elements for this structure so that we include the snapshot
                ProcessElements(artifactClass, sd, FhirSequence);
            }
        }
    }

    public bool TryReconcileProfileSnapshots()
    {
        HashSet<string> resolvedProfileUrls = [];

        // iterate over the profiles
        foreach (StructureDefinition sd in _profilesByUrl.Values)
        {
            if (resolvedProfileUrls.Contains(sd.Url))
            {
                continue;
            }

            reconcileProfile(sd);
        }

        return true;

        void reconcileProfile(StructureDefinition profileSd)
        {
            string baseDefinition = profileSd.BaseDefinition
                ?? profileSd.Snapshot?.Element.FirstOrDefault()?.ElementId
                ?? profileSd.Differential?.Element.FirstOrDefault()?.ElementId
                ?? throw new Exception($"Cannot deterime base definition for profile {profileSd.Id} ({profileSd.Url}|{profileSd.Version})");

            // resolve the parent definition for this profile
            if (!TryGetStructure(baseDefinition, out StructureDefinition? parentSd))
            {
                Console.WriteLine($"Failed to resolve parent ({baseDefinition}) for profile: {profileSd.Url}");
                return;
            }

            if ((parentSd.cgArtifactClass() == FhirArtifactClassEnum.Profile) &&
                !resolvedProfileUrls.Contains(parentSd.Url))
            {
                reconcileProfile(parentSd);
            }

            IEnumerable<ElementDefinition> profileElements = profileSd.cgElements();
            ILookup<string, ElementDefinition> parentElementLookup = parentSd.cgElements().ToLookup(e => e.ElementId);

            // iterate over the elements in the profile
            foreach (ElementDefinition profileEd in profileElements)
            {
                // check to see if this element is in the parent
                if (!parentElementLookup.Contains(profileEd.ElementId))
                {
                    continue;
                }

                // get the parent element
                ElementDefinition parentEd = parentElementLookup[profileEd.ElementId].First();

                // check for missing binding info
                if ((profileEd.Binding != null) &&
                    (profileEd.Binding.ValueSet == null) &&
                    (parentEd.Binding?.ValueSet != null))
                {
                    profileEd.Binding.ValueSet = parentEd.Binding.ValueSet;
                }
            }

            resolvedProfileUrls.Add(profileSd.Url);
        }
    }

    /// <summary>Attempts to update an element within a structure, based on the field orders provided or pulled from the element.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="destinationSd">     Destination SD.</param>
    /// <param name="ed">                The <see cref="ElementDefinition"/> to add the missing ID to.</param>
    /// <param name="previousFieldOrder">(Optional) The previous field order.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    internal bool TryUpdateElement(
        StructureDefinition destinationSd,
        ElementDefinition ed,
        int? previousFieldOrder = null,
        int? previousComponentFieldOrder = null)
    {
        // lookup the structure locally because the parameter may be a copy or read only
        if ((!_complexTypesByName.TryGetValue(destinationSd.Name, out StructureDefinition? sd)) &&
            (!_resourcesByName.TryGetValue(destinationSd.Name, out sd)) &&
            (!_profilesByUrl.TryGetValue(destinationSd.Url, out sd)) &&
            (!_interfacesByName.TryGetValue(destinationSd.Url, out sd)))
        {
            throw new Exception($"Failed to find StructureDefinition id: {destinationSd.Id}, name: {destinationSd.Name}, url: {destinationSd.Url}");
        }

        int sdFieldOrder = previousFieldOrder ?? ed.cgFieldOrder();                             // ed.GetIntegerExtension(CommonDefinitions.ExtUrlEdFieldOrder) ?? -1;
        int componentFieldOrder = previousComponentFieldOrder ?? ed.cgComponentFieldOrder();    // ed.GetIntegerExtension(CommonDefinitions.ExtUrlEdComponentFieldOrder) ?? -1;

        if ((sdFieldOrder == -1) || (componentFieldOrder == -1))
        {
            throw new Exception("ElementDefinitions must be annotated with field order!");
        }

        // check for adding to snapshot
        if ((sd.Snapshot != null) && (sd.Snapshot.Element.Count != 0))
        {
            // find the element to insert after
            int matchIndex = sd.Snapshot.Element.FindIndex(
                e => (e.cgFieldOrder() == sdFieldOrder) && (e.cgComponentFieldOrder() == componentFieldOrder));
            sd.Snapshot.Element[matchIndex] = ed;
        }

        // check for adding to differential
        if ((sd.Differential != null) && (sd.Differential.Element.Count != 0))
        {
            // find the element to insert after
            int matchIndex = sd.Differential.Element.FindIndex(
                e => (e.cgFieldOrder() == sdFieldOrder) && (e.cgComponentFieldOrder() == componentFieldOrder));
            sd.Differential.Element[matchIndex] = ed;
        }

        return true;
    }

    /// <summary>Attempts to insert element, must be annotated with field orders.</summary>
    /// <param name="destinationSd">           Destination SD.</param>
    /// <param name="ed">                      The <see cref="ElementDefinition"/> to add the
    ///  missing ID to.</param>
    /// <param name="increaseSubsequentOrders">True to increase subsequent orders.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    internal bool TryInsertElement(
        StructureDefinition destinationSd,
        ElementDefinition ed,
        bool increaseSubsequentOrders)
    {
        // lookup the structure locally because the parameter may be a copy or read only
        if ((!_complexTypesByName.TryGetValue(destinationSd.Name, out StructureDefinition? sd)) &&
            (!_resourcesByName.TryGetValue(destinationSd.Name, out sd)) &&
            (!_profilesByUrl.TryGetValue(destinationSd.Url, out sd)) &&
            (!_interfacesByName.TryGetValue(destinationSd.Url, out sd)))
        {
            throw new Exception($"Failed to find StructureDefinition id: {destinationSd.Id}, name: {destinationSd.Name}, url: {destinationSd.Url}");
        }

        int sdFieldOrder = ed.cgFieldOrder();                   // ed.GetIntegerExtension(CommonDefinitions.ExtUrlEdFieldOrder) ?? -1;
        int componentFieldOrder = ed.cgComponentFieldOrder();   // ed.GetIntegerExtension(CommonDefinitions.ExtUrlEdComponentFieldOrder) ?? -1;

        if ((sdFieldOrder == -1) || (componentFieldOrder == -1))
        {
            throw new Exception("ElementDefinitions to insert must be annotated with field order!");
        }

        int lastDot = ed.Path.LastIndexOf('.');
        string parentPath = lastDot == -1 ? ed.Path : ed.Path.Substring(0, lastDot);

        //// add to lookup dict
        //_elementSdLookup.Add($"{ed.Path}|{ed.ElementId}", sd);

        // check for a value set binding
        CheckElementBindings(sd.cgArtifactClass(), sd, ed);

        // check for adding to snapshot
        if ((sd.Snapshot != null) && (sd.Snapshot.Element.Count != 0))
        {
            // find the element to insert after
            int matchIndex = sd.Snapshot.Element.FindIndex(e => e.cgFieldOrder() == sdFieldOrder);

            if (matchIndex == -1)
            {
                // add to the end
                sd.Snapshot.Element.Add(ed);
            }
            else
            {
                sd.Snapshot.Element.Insert(matchIndex, ed);

                if (increaseSubsequentOrders)
                {
                    // increase the order of all subsequent elements
                    foreach (ElementDefinition e in sd.Snapshot.Element.Skip(matchIndex + 1))
                    {
                        int order = e.cgFieldOrder() + 1;

                        int componentOrder = e.cgComponentFieldOrder();
                        int currentLastDot = e.Path.LastIndexOf('.');
                        string currentParentPath = currentLastDot == -1 ? e.Path : e.Path.Substring(0, currentLastDot);

                        if (currentParentPath == parentPath)
                        {
                            componentOrder++;
                        }

                        e.cgSetFieldOrder(order, componentOrder);
                    }
                }
            }
        }

        // check for adding to differential
        if ((sd.Differential != null) && (sd.Differential.Element.Count != 0))
        {
            // find the element to insert after
            int matchIndex = sd.Differential.Element.FindIndex(e => e.cgFieldOrder() == sdFieldOrder);

            if (matchIndex == -1)
            {
                // add to the end
                sd.Differential.Element.Add(ed);
            }
            else
            {
                sd.Differential.Element.Insert(matchIndex, ed);

                if (increaseSubsequentOrders)
                {
                    // increase the order of all subsequent elements
                    foreach (ElementDefinition e in sd.Differential.Element.Skip(matchIndex + 1))
                    {
                        int order = e.cgFieldOrder() + 1;
                        int componentOrder = e.cgComponentFieldOrder();

                        int currentLastDot = e.Path.LastIndexOf('.');
                        string currentParentPath = currentLastDot == -1 ? e.Path : e.Path.Substring(0, currentLastDot);

                        if (currentParentPath == parentPath)
                        {
                            componentOrder++;
                        }

                        e.cgSetFieldOrder(order, componentOrder);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Processes elements in a structure definition.</summary>
    /// <remarks>Adds field orders, indexes paths that contain child elements, etc.</remarks>
    /// <param name="artifactClass">The artifact class.</param>
    /// <param name="sd">           The structure definition.</param>
    /// <param name="fhirVersion">  The FHIR version.</param>
    private void ProcessElements(FhirArtifactClassEnum artifactClass, StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion)
    {
        Dictionary<string, int> allFieldOrders = [];
        Dictionary<string, Dictionary<string, int>> componentOrdersByIdByParent = [];

        List<string> idByDepth = [];

        HashSet<string> multiTypePaths = [];
        Dictionary<string, string> singleTypePaths = [];

        StructureProcessingInfo processingInfo = sd.cgGetProcessingInfo() ?? new()
        {
            ArtifactClass = artifactClass,
            HasProcessedSnapshot = false,
            HasProcessedDifferential = false,
        };

        // check for a binding on the root element of the differential that does not appear on the snapshot
        if ((sd.Snapshot?.Element.Count > 0) &&
            (sd.Differential?.Element.Count > 0) &&
            (sd.Snapshot.Element[0].Path == sd.Differential.Element[0].Path) &&
            (sd.Snapshot.Element[0].Binding == null) &&
            (sd.Differential.Element[0].Binding != null))
        {
            sd.Snapshot.Element[0].Binding = (ElementDefinition.ElementDefinitionBindingComponent)sd.Differential.Element[0].Binding.DeepCopy();
        }

        IEnumerable<ElementDefinition> elements = processingInfo.HasProcessedSnapshot
            ? []
            : sd.Snapshot?.Element ?? [];

        // process each element in the snapshot
        foreach (ElementDefinition ed in elements)
        {
            if (allFieldOrders.ContainsKey(ed.ElementId))
            {
                // this is an unprocessable error in the definition, skip this element
                continue;
            }

            // coerce 'bare' types in versions earlier than R5
            switch (fhirVersion)
            {
                case FhirReleases.FhirSequenceCodes.DSTU2:
                case FhirReleases.FhirSequenceCodes.STU3:
                case FhirReleases.FhirSequenceCodes.R4:
                case FhirReleases.FhirSequenceCodes.R4B:
                    {
                        if (((ed.ElementId == "Element.id") || (ed.Base?.Path == "Element.id") ||
                             (ed.ElementId == "Resource.id") || (ed.Base?.Path == "Resource.id")) &&
                            (ed.Type.Count == 1))
                        {
                            ed.Type[0] = new ElementDefinition.TypeRefComponent()
                            {
                                Code = "http://hl7.org/fhirpath/System.String",
                                Extension = [new Extension
                                {
                                    Url = CommonDefinitions.ExtUrlFhirType,
                                    Value = new FhirUrl("id"),
                                }],
                            };
                        }
                        else if (((ed.ElementId == "Extension.url") || (ed.Base?.Path == "Extension.url")) &&
                                 (ed.Type.Count == 1))
                        {
                            ed.Type[0] = new ElementDefinition.TypeRefComponent()
                            {
                                Code = "http://hl7.org/fhirpath/System.String",
                                Extension = [new Extension
                                {
                                    Url = CommonDefinitions.ExtUrlFhirType,
                                    Value = new FhirUrl("uri"),
                                }],
                            };
                        }
                    }
                    break;
            }

            int lastDot = ed.Path.LastIndexOf('.');
            string parentPath = lastDot == -1 ? ed.Path : ed.Path.Substring(0, lastDot);
            int componentFieldOrder = 0;

            if (lastDot != -1)
            {
                if (!componentOrdersByIdByParent.TryGetValue(parentPath, out Dictionary<string, int>? coById))
                {
                    coById = [];
                    componentOrdersByIdByParent.Add(parentPath, coById);
                }

                if (!coById.TryGetValue(ed.ElementId, out componentFieldOrder))
                {
                    componentFieldOrder = coById.Count;
                    coById.Add(ed.ElementId, componentFieldOrder);
                }
            }

            int fo = allFieldOrders.Count;
            allFieldOrders.Add(ed.ElementId, fo);

            ed.cgSetFieldOrder(fo, componentFieldOrder);

            if (lastDot != -1)
            {
                if (parentPath.Contains('.') &&
                    !_parentElementsAndType.ContainsKey(parentPath))
                {
                    if (singleTypePaths.TryGetValue(parentPath, out string? parentType))
                    {
                        _parentElementsAndType.Add(parentPath, parentType);
                    }
                    else
                    {
                        _parentElementsAndType.Add(parentPath, string.Empty);
                    }
                }
            }

            // check for being a slice
            if (!string.IsNullOrEmpty(ed.SliceName))
            {
                if (!_pathsWithSlices.TryGetValue(ed.Path, out KeyValuePair<string, StructureDefinition>[]? slices))
                {
                    slices = [new(ed.SliceName, sd)];
                    _pathsWithSlices[ed.Path] = slices;
                }
                else
                {
                    if (!slices.Any(sliceDef => (sliceDef.Key == ed.SliceName) && (sliceDef.Value.Id == sd.Id)))
                    {
                        _pathsWithSlices[ed.Path] = [.. slices, new(ed.SliceName, sd)];
                    }
                }
            }

            // check for a value set binding
            CheckElementBindings(artifactClass, sd, ed);

            // check for a single type and add to the path types dictionary
            if (ed.Type.Count == 1)
            {
                ElementDefinition.TypeRefComponent tr = ed.Type.First();
                string? elementType = tr.Code;
                if (elementType == null)
                {
                    string elementShortName = ed.Path.Split('.').Last();
                    switch (elementShortName)
                    {
                        case "id":
                            tr.Code = "http://hl7.org/fhirpath/System.String";
                            tr.SetExtension(CommonDefinitions.ExtUrlFhirType, new FhirUrl("id"));
                            break;

                        case "url":
                            tr.Code = "http://hl7.org/fhirpath/System.String";
                            tr.SetExtension(CommonDefinitions.ExtUrlFhirType, new FhirUrl("uri"));
                            break;
                    }
                }

                // check for existing multi-type path
                if (multiTypePaths.Contains(ed.Path))
                {
                    // do nothing
                }
                // check to see if there is an existing type for this path
                else if (singleTypePaths.TryGetValue(ed.Path, out string? existingType))
                {
                    // if the existing type is not the same as the current type, it is a polymorphic type
                    if (existingType != ed.Type.First().Code)
                    {
                        // remove from the set
                        singleTypePaths.Remove(ed.Path);
                        multiTypePaths.Add(ed.Path);
                    }
                }
                else
                {
                    // add this type
                    singleTypePaths[ed.Path] = ed.Type.First().Code;
                }
            }
            else
            {
                // this is a multi-type path
                _ = multiTypePaths.Add(ed.Path);
            }
        }

        // check to see if we processed a snapshot
        if (allFieldOrders.Count != 0)
        {
            processingInfo = processingInfo with { HasProcessedSnapshot = true };

            // if we just processed the snapshot, process the differential even if it has been processed before
            elements = sd.Differential?.Element ?? Enumerable.Empty<ElementDefinition>();
        }
        else
        {
            // only process the differential if we have one that has not been processed
            elements = processingInfo.HasProcessedDifferential
                ? []
                : sd.Differential?.Element ?? [];
        }

        // process each element in the differential
        foreach (ElementDefinition ed in elements)
        {
            // coerce 'bare' types in versions earlier than R5
            switch (fhirVersion)
            {
                case FhirReleases.FhirSequenceCodes.DSTU2:
                case FhirReleases.FhirSequenceCodes.STU3:
                case FhirReleases.FhirSequenceCodes.R4:
                case FhirReleases.FhirSequenceCodes.R4B:
                    {
                        if (((ed.ElementId == "Element.id") || (ed.Base?.Path == "Element.id") ||
                             (ed.ElementId == "Resource.id") || (ed.Base?.Path == "Resource.id")) &&
                            (ed.Type.Count == 1))
                        {
                            ed.Type[0] = new ElementDefinition.TypeRefComponent()
                            {
                                Code = "http://hl7.org/fhirpath/System.String",
                                Extension = [new Extension
                                {
                                    Url = CommonDefinitions.ExtUrlFhirType,
                                    Value = new FhirUrl("id"),
                                }],
                            };
                        }
                        else if (((ed.ElementId == "Extension.url") || (ed.Base?.Path == "Extension.url")) &&
                                 (ed.Type.Count == 1))
                        {
                            ed.Type[0] = new ElementDefinition.TypeRefComponent()
                            {
                                Code = "http://hl7.org/fhirpath/System.String",
                                Extension = [new Extension
                                {
                                    Url = CommonDefinitions.ExtUrlFhirType,
                                    Value = new FhirUrl("uri"),
                                }],
                            };
                        }
                    }
                    break;
            }

            int lastDot = ed.Path.LastIndexOf('.');
            string parentPath = lastDot == -1 ? ed.Path : ed.Path.Substring(0, lastDot);
            int componentFieldOrder = 0;

            if (lastDot != -1)
            {
                if (!componentOrdersByIdByParent.TryGetValue(parentPath, out Dictionary<string, int>? coById))
                {
                    coById = [];
                    componentOrdersByIdByParent.Add(parentPath, coById);
                }

                if (!coById.TryGetValue(ed.ElementId, out componentFieldOrder))
                {
                    componentFieldOrder = coById.Count;
                    coById.Add(ed.ElementId, componentFieldOrder);
                }
            }

            // check if this element NOT has already been processed in the snapshot
            if (!allFieldOrders.TryGetValue(ed.ElementId, out int fo))
            {
                fo = allFieldOrders.Count;
                allFieldOrders.Add(ed.ElementId, fo);

                // check for being a child element to promote a parent to backbone
                if (lastDot != -1)
                {
                    if (parentPath.Contains('.') &&
                        !_parentElementsAndType.ContainsKey(parentPath))
                    {
                        if (singleTypePaths.TryGetValue(parentPath, out string? parentType))
                        {
                            _parentElementsAndType.Add(parentPath, parentType);
                        }
                        else
                        {
                            _parentElementsAndType.Add(parentPath, string.Empty);
                        }
                    }
                }

                // check for being a slice - only need to test if this element has not been processed already
                if (!string.IsNullOrEmpty(ed.SliceName))
                {
                    if (!_pathsWithSlices.TryGetValue(ed.Path, out KeyValuePair<string, StructureDefinition>[]? slices))
                    {
                        slices = [new(ed.SliceName, sd)];
                        _pathsWithSlices[ed.Path] = slices;
                    }
                    else
                    {
                        if (!slices.Any(sliceDef => (sliceDef.Key == ed.SliceName) && (sliceDef.Value.Id == sd.Id)))
                        {
                            _pathsWithSlices[ed.Path] = [.. slices, new(ed.SliceName, sd)];
                        }
                    }
                }

                // check for a value set binding
                CheckElementBindings(artifactClass, sd, ed);

                // check for a single type and add to the path types dictionary
                if (ed.Type.Count == 1)
                {
                    // check for existing multi-type path
                    if (multiTypePaths.Contains(ed.Path))
                    {
                        // do nothing
                    }
                    // check to see if there is an existing type for this path
                    else if (singleTypePaths.TryGetValue(ed.Path, out string? existingType))
                    {
                        // if the existing type is not the same as the current type, it is a polymorphic type
                        if (existingType != ed.Type.First().Code)
                        {
                            // remove from the set
                            singleTypePaths.Remove(ed.Path);
                            multiTypePaths.Add(ed.Path);
                        }
                    }
                    else
                    {
                        // add this type
                        singleTypePaths[ed.Path] = ed.Type.First().Code;
                    }
                }
                else
                {
                    // this is a multi-type path
                    _ = multiTypePaths.Add(ed.Path);
                }
            }

            ed.cgSetFieldOrder(fo, componentFieldOrder);
        }

        // check to see if we processed a differential
        if (sd.Differential?.Element.Count > 0)
        {
            processingInfo = processingInfo with { HasProcessedDifferential = true };
        }

        // update our processing info
        sd.cgSetProcessingInfo(processingInfo);
    }

    /// <summary>
    /// Retrieves the concept definition for a given code in a specified code system.
    /// </summary>
    /// <param name="system">The URL of the code system.</param>
    /// <param name="code">The code to retrieve the definition for.</param>
    /// <returns>The concept definition for the specified code, or an empty string if not found.</returns>
    public string ConceptDefinition(string system, string code, string defaultValue = "")
    {
        // check to see if we have the code system
        if (!_codeSystemsByUrl.TryGetValue(system, out CodeSystem? cs))
        {
            return defaultValue;
        }

        if (cs.Concept == null)
        {
            return defaultValue;
        }

        return searchConceptRecursive(cs.Concept!) ?? defaultValue;

        string? searchConceptRecursive(List<CodeSystem.ConceptDefinitionComponent> conceptDefinitions)
        {
            string? val = null;

            foreach (CodeSystem.ConceptDefinitionComponent c in conceptDefinitions)
            {
                if (c.Code == code)
                {
                    return c.Definition;
                }

                if (c.Concept == null)
                {
                    continue;
                }

                val = searchConceptRecursive(c.Concept);
                if (val != null)
                {
                    return val;
                }
            }

            return null;
        }
    }

    /// <summary>Attempts to get value set.</summary>
    /// <param name="unversionedUrl">URL of the unversioned.</param>
    /// <param name="version">       The version.</param>
    /// <param name="vs">            [out] The vs.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    public bool TryGetValueSet(string unversionedUrl, string version, [NotNullWhen(true)] out ValueSet? vs)
    {
        return _valueSetsByVersionedUrl.TryGetValue(unversionedUrl + "|" + version, out vs);
    }

    public bool TryGetValueSet(string url, [NotNullWhen(true)] out ValueSet? vs)
    {
        if (_valueSetsByVersionedUrl.TryGetValue(url, out vs))
        {
            return true;
        }

        if (_valueSetVersions.TryGetValue(url, out string[]? vsVersions) &&
            (vsVersions != null) &&
            (vsVersions.Length > 0))
        {
            return _valueSetsByVersionedUrl.TryGetValue(url + "|" + vsVersions.Max(), out vs);
        }

        vs = null;
        return false;
    }

    /// <summary>
    /// Returns the versioned URL for a given value set URL.
    /// </summary>
    /// <param name="vsUrl">The URL of the value set.</param>
    /// <returns>The versioned URL of the value set.</returns>
    public string VersionedUrlForVs(string vsUrl)
    {
        int lastPipe = vsUrl.LastIndexOf('|');

        if ((lastPipe != -1) ||
            (!_valueSetVersions.TryGetValue(vsUrl, out string[]? vsVersions)) ||
            (vsVersions == null) ||
            (vsVersions.Length == 0))
        {
            return vsUrl;
        }

        return vsUrl + "|" + vsVersions!.Max();
    }

    /// <summary>Unversioned URL for vs.</summary>
    /// <param name="vsUrl">The URL of the value set.</param>
    /// <returns>A string.</returns>
    public string UnversionedUrlForVs(string vsUrl)
    {
        int lastPipe = vsUrl.LastIndexOf('|');

        if (lastPipe == -1)
        {
            return vsUrl;
        }

        // strip the pipe and version
        return vsUrl.Substring(0, lastPipe);
    }

    /// <summary>Check element bindings.</summary>
    /// <param name="artifactClass">The artifact class.</param>
    /// <param name="ed">           The ed.</param>
    private void CheckElementBindings(FhirArtifactClassEnum artifactClass, StructureDefinition sd, ElementDefinition ed)
    {
        if (ed.Binding == null)
        {
            return;
        }

        List<ElementDefinition.AdditionalComponent> unboundAdditionalRequired = ed.Binding.Additional
            .Where(ac =>
                (ac.Usage.Count == 0) &&
                ((ac.Purpose == ElementDefinition.AdditionalBindingPurposeVS.Maximum) || (ac.Purpose == ElementDefinition.AdditionalBindingPurposeVS.Required)))
            .ToList();

        // check for the scenario of a preferred binding but a max additional binding
        if ((ed.Binding.Strength != BindingStrength.Required) &&
            (unboundAdditionalRequired.Count != 0))
        {
            // add the preferred binding as an additional binding
            ed.Binding.Additional.Add(new ElementDefinition.AdditionalComponent
            {
                Purpose = ElementDefinition.AdditionalBindingPurposeVS.Preferred,
                ValueSet = ed.Binding.ValueSet,
                Documentation = ed.Binding.Description,
            });

            // move the max additional binding to the preferred binding
            ed.Binding.Strength = BindingStrength.Required;
            ed.Binding.ValueSet = unboundAdditionalRequired[0].ValueSet;
            ed.Binding.Description = unboundAdditionalRequired[0].Documentation;

            // remove the max additional binding
            ed.Binding.Additional.Remove(unboundAdditionalRequired[0]);
        }

        // check for a value set binding
        if (!string.IsNullOrEmpty(ed.Binding.ValueSet))
        {
            string url = VersionedUrlForVs(ed.Binding.ValueSet);

            // need to pick the right dictionary based on artifact class
            switch (artifactClass)
            {
                // these artifacts are always core
                case FhirArtifactClassEnum.PrimitiveType:
                case FhirArtifactClassEnum.ComplexType:
                case FhirArtifactClassEnum.Resource:
                case FhirArtifactClassEnum.Compartment:
                    {
                        if (!_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
                        {
                            bindings = [];
                            _coreBindingEdsByPathByValueSet[url] = bindings;
                        }

                        // search for the structure element collection that contains the structure definition
                        StructureElementCollection? matchingElementCollection = bindings.FirstOrDefault(e => e.Structure.Url == sd.Url);
                        if (matchingElementCollection == null)
                        {
                            matchingElementCollection = new StructureElementCollection { Structure = sd, Elements = [] };
                            bindings.Add(matchingElementCollection);
                        }

                        matchingElementCollection.Elements.Add(ed);
                    }
                    break;

                // extensions need to be processed based on their contexts, even though the element does not have them
                case FhirArtifactClassEnum.Extension:
                    {
                        NestIntoExtension(url, sd);
                    }
                    break;

                // these artifacts are always extended
                case FhirArtifactClassEnum.Operation:
                case FhirArtifactClassEnum.SearchParameter:
                case FhirArtifactClassEnum.Profile:
                case FhirArtifactClassEnum.LogicalModel:
                case FhirArtifactClassEnum.ConceptMap:
                case FhirArtifactClassEnum.NamingSystem:
                case FhirArtifactClassEnum.StructureMap:
                    {
                        if (!_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
                        {
                            bindings = [];
                            _extendedBindingEdsByPathByValueSet[url] = bindings;
                        }

                        // search for the structure element collection that contains the structure definition
                        StructureElementCollection? matchingElementCollection = bindings.FirstOrDefault(e => e.Structure.Url == sd.Url);
                        if (matchingElementCollection == null)
                        {
                            matchingElementCollection = new StructureElementCollection { Structure = sd, Elements = [] };
                            bindings.Add(matchingElementCollection);
                        }

                        matchingElementCollection.Elements.Add(ed);

                    }
                    break;

                // these are untracked because they should never happen
                case FhirArtifactClassEnum.CodeSystem:
                case FhirArtifactClassEnum.ValueSet:
                case FhirArtifactClassEnum.ImplementationGuide:
                case FhirArtifactClassEnum.CapabilityStatement:
                case FhirArtifactClassEnum.Unknown:
                    break;
                default:
                    break;
            }
        }

        return;

        /// <summary>
        /// Nest the current structure definition into an extension with the specified URL.
        /// </summary>
        /// <param name="url">The URL of the extension.</param>
        /// <param name="currentSd">The current structure definition to nest.</param>
        void NestIntoExtension(string url, StructureDefinition currentSd)
        {
            foreach (StructureDefinition.ContextComponent cc in currentSd.Context ?? Enumerable.Empty<StructureDefinition.ContextComponent>())
            {
                switch (cc.Type)
                {
                    case StructureDefinition.ExtensionContextType.Element:
                        {
                            if (!_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
                            {
                                bindings = [];
                                _extendedBindingEdsByPathByValueSet[url] = bindings;
                            }

                            //bindings[cc.Expression] = bindings.TryGetValue(cc.Expression, out ElementDefinition[]? ar) ? ar.Append(ed).ToArray() : new[] { ed };

                            //if (!bindings.TryGetValue(cc.Expression, out List<StructureElementCollection>? pathElementCollections))
                            //{
                            //    pathElementCollections = new();
                            //    bindings.Add(cc.Expression, pathElementCollections);
                            //}

                            // search for the structure element collection that contains the structure definition
                            StructureElementCollection? matchingElementCollection = bindings.FirstOrDefault(e => e.Structure.Url == currentSd.Url);
                            if (matchingElementCollection == null)
                            {
                                matchingElementCollection = new StructureElementCollection { Structure = currentSd, Elements = [] };
                                bindings.Add(matchingElementCollection);
                            }

                            matchingElementCollection.Elements.Add(ed);

                        }
                        break;

                    case StructureDefinition.ExtensionContextType.Extension:
                        {
                            if (_extensionsByUrl.TryGetValue(cc.Expression, out StructureDefinition? extSd))
                            {
                                NestIntoExtension(url, extSd);
                            }
                        }
                        break;

                    case StructureDefinition.ExtensionContextType.Fhirpath:
                    default:
                        {
                            Logger.LogElementBindingExtUnhandledType(currentSd.Id, url, cc.Type?.ToLiteral(), cc.Expression);
                        }
                        continue;
                }

            }
        }
    }

    /// <summary>Processes parameters in an operation definition.</summary>
    /// <remarks>Adds field orders, etc.</remarks>
    /// <param name="op">The operation.</param>
    private void ProcessParameters(OperationDefinition op)
    {
        Dictionary<string, int> inFieldOrder = [];
        Dictionary<string, int> outFieldOrder = [];

        // annotate each parameter with a field order extension
        foreach (OperationDefinition.ParameterComponent pc in op.Parameter ?? Enumerable.Empty<OperationDefinition.ParameterComponent>())
        {
            int fo;
            if (pc.Use == OperationParameterUse.Out)
            {
                fo = outFieldOrder.Count + 1;
                outFieldOrder.Add(pc.Name, fo);
            }
            else
            {
                if (inFieldOrder.ContainsKey(pc.Name))
                {
                    Console.WriteLine($"Operation: {op.Id} ({op.Url}) defines the parameter {pc.Name} more than once!!!");
                    continue;
                }
                fo = inFieldOrder.Count + 1;
                inFieldOrder.Add(pc.Name, fo);
            }

            pc.cgSetFieldOrder(fo);
        }
    }

    /// <summary>Track resource.</summary>
    /// <param name="r">A Resource to process.</param>
    private void TrackResource(Resource r)
    {
        if (r is IConformanceResource canonical)
        {
            string fullUrl = canonical.Url;

            string canonicalUrl;
            string version;

            int index = fullUrl.LastIndexOf('|');

            if (index != -1)
            {
                canonicalUrl = fullUrl[..index];
                version = fullUrl[(index + 1)..];
            }
            else
            {
                canonicalUrl = fullUrl;
                IEnumerable<ElementValue> vElement = r.NamedChildren.Where(e => e.ElementName == "version");

                if (vElement.Any())
                {
                    version = vElement.First().Value.ToString() ?? FhirSequence.ToLongVersion();
                }
                else
                {
                    version = FhirSequence.ToLongVersion();
                }
            }

            if (!_canonicalResources.TryGetValue(canonicalUrl, out Dictionary<string, IConformanceResource>? versions))
            {
                versions = [];
                _canonicalResources.Add(canonicalUrl, versions);
            }

            versions[version] = canonical;
        }

        string url;

        IEnumerable<ElementValue> uElement = r.NamedChildren.Where(e => e.ElementName == "url");

        if (uElement.Any())
        {
            url = uElement.First().Value.ToString() ?? r.Id;
        }
        else
        {
            url = r.Id;
        }

        // there is nothing we can really do about collisions, so just use the most recent
        _allResources[url] = r;
    }

    /// <summary>Gets URL of the code systems by.</summary>
    public IReadOnlyDictionary<string, CodeSystem> CodeSystemsByUrl => _codeSystemsByUrl;

    /// <summary>Adds a code system.</summary>
    /// <param name="codeSystem">The code system.</param>
    public void AddCodeSystem(CodeSystem codeSystem, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_codeSystemsByUrl.TryGetValue(codeSystem.Url, out CodeSystem? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(codeSystem, packageId, packageVersion);

        _codeSystemsByUrl[codeSystem.Url] = codeSystem;
        TrackResource(codeSystem);
    }

    public IReadOnlyDictionary<string, ConceptMap> ConceptMapsByUrl => _conceptMapsByUrl;
    public void AddConceptMap(ConceptMap cm, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_conceptMapsByUrl.TryGetValue(cm.Url, out ConceptMap? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(cm, packageId, packageVersion);

        _conceptMapsByUrl[cm.Url] = cm;
        TrackResource(cm);

        if (cm.cgSourceScope() is string sourceScope)
        {
            _conceptMapsBySourceUrl.AddToValue(sourceScope, cm);
        }

        if (cm.cgTargetScope() is string targetScope)
        {
            _conceptMapsByTargetUrl.AddToValue(targetScope, cm);
        }
    }

    /// <summary>
    /// Retrieves a list of ConceptMaps for a given source URL.
    /// </summary>
    /// <param name="src">The source URL.</param>
    /// <returns>A list of ConceptMaps.</returns>
    public List<ConceptMap> ConceptMapsForSource(string src)
    {
        int index = src.LastIndexOf('|');

        if (index == -1)
        {
            return _conceptMapsBySourceUrl.TryGetValue(src, out List<ConceptMap>? srcMaps) ? srcMaps : [];
        }

        string uvUrl = src[0..index];

        bool hasUnversionedMaps = _conceptMapsBySourceUrl.TryGetValue(uvUrl, out List<ConceptMap>? uvMaps);
        bool hasVersionedMaps = _conceptMapsBySourceUrl.TryGetValue(src, out List<ConceptMap>? vMaps);

        if (hasUnversionedMaps && hasVersionedMaps)
        {
            return uvMaps!.Union(vMaps!).ToList();
        }

        if (hasVersionedMaps)
        {
            return vMaps!;
        }

        if (hasUnversionedMaps)
        {
            return uvMaps!;
        }

        return [];
    }


    /// <summary>
    /// Retrieves a list of ConceptMaps for a given target URL.
    /// </summary>
    /// <param name="target">The target URL.</param>
    /// <returns>A list of ConceptMaps.</returns>
    public List<ConceptMap> ConceptMapsForTarget(string target)
    {
        int index = target.LastIndexOf('|');

        if (index == -1)
        {
            return _conceptMapsByTargetUrl.TryGetValue(target, out List<ConceptMap>? tgtMaps) ? tgtMaps : [];
        }

        string uvUrl = target[0..index];

        bool hasUnversionedMaps = _conceptMapsByTargetUrl.TryGetValue(uvUrl, out List<ConceptMap>? uvMaps);
        bool hasVersionedMaps = _conceptMapsByTargetUrl.TryGetValue(target, out List<ConceptMap>? vMaps);

        if (hasUnversionedMaps && hasVersionedMaps)
        {
            return uvMaps!.Union(vMaps!).ToList();
        }

        if (hasVersionedMaps)
        {
            return vMaps!;
        }

        if (hasUnversionedMaps)
        {
            return uvMaps!;
        }

        return [];
    }

    public IReadOnlyDictionary<string, StructureMap> StructureMapsByUrl => _structureMapsByUrl;
    public void AddStructureMap(StructureMap sm, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_structureMapsByUrl.TryGetValue(sm.Url, out StructureMap? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(sm, packageId, packageVersion);

        _structureMapsByUrl[sm.Url] = sm;
        TrackResource(sm);
    }

    /// <summary>Gets the value set versions.</summary>
    public IReadOnlyDictionary<string, string[]> ValueSetVersions => _valueSetVersions;

    /// <summary>Gets URL of the value sets by.</summary>
    public IReadOnlyDictionary<string, ValueSet> ValueSetsByVersionedUrl => _valueSetsByVersionedUrl;

    /// <summary>Versions for value set.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>A string[].</returns>
    public string[] VersionsForValueSet(string valueSetUrl)
    {
        string url = UnversionedUrlForVs(valueSetUrl);

        _valueSetVersions.TryGetValue(url, out string[]? versions);

        return versions ?? [];
    }

    /// <summary>
    /// Returns the external value sets that are bound in the DefinitionCollection.
    /// </summary>
    /// <returns>An enumerable collection of the external value sets.</returns>
    public IEnumerable<string> BoundExternalValueSets()
    {
        IEnumerable<string> core = _coreBindingEdsByPathByValueSet.Keys.Except(_valueSetsByVersionedUrl.Keys);
        core = core.Except(_valueSetVersions.Keys);

        IEnumerable<string> ext = _extendedBindingEdsByPathByValueSet.Keys.Except(_valueSetsByVersionedUrl.Keys);
        ext = ext.Except(_valueSetVersions.Keys);

        return core.Union(ext);
    }

    /// <summary>Structures and elements that contain bindings to the specified value set URL.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    public IEnumerable<StructureElementCollection> CoreBindingsForVs(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        if (_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
        {
            return bindings;
        }

        return [];
    }

    public IEnumerable<StructureElementCollection> AllBindingsForVs(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        if (!_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? core))
        {
            core = [];
        }

        if (!_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? extended))
        {
            extended = [];
        }

        return core.Union(extended);
    }

    /// <summary>Strongest core binding.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>A BindingStrength?</returns>
    public BindingStrength? StrongestCoreBinding(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        if (_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings) &&
            (bindings != null))
        {
            return bindings
                .SelectMany(ec => ec.Elements.Select(ed => ed.Binding.Strength))
                .OrderBy(s => s, BindingStrengthComparer.Instance)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>Core binding strength by type.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,BindingStrength&gt;</returns>
    public IReadOnlyDictionary<string, BindingStrength> CoreBindingStrengthByType(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        Dictionary<string, BindingStrength> bindingStrengthByType = [];

        if (!_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
        {
            return bindingStrengthByType;
        }

        // traverse existing bindings to find the strongest for each type
        foreach (StructureElementCollection ec in bindings)
        {
            foreach (ElementDefinition ed in ec.Elements)
            {
                foreach (ElementDefinition.TypeRefComponent tr in ed.Type)
                {
                    if (bindingStrengthByType.TryGetValue(tr.Code, out BindingStrength bs) &&
                        BindingStrengthComparer.Instance.Compare(bs, ed.Binding.Strength!) <= 0)
                    {
                        continue;
                    }

                    bindingStrengthByType[tr.Code] = (BindingStrength)ed.Binding.Strength!;
                }
            }
        }

        return bindingStrengthByType;
    }

    /// <summary>Extended bindings for vs.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>
    /// An enumerator that allows foreach to be used to process extended bindings for vs in this
    /// collection.
    /// </returns>
    public IEnumerable<StructureElementCollection> ExtendedBindingsForVs(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        if (_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
        {
            return bindings;

            //List<StructureElementCollection> filtered = new();

            //foreach (StructureElementCollection ec in bindings)
            //{
            //    List<ElementDefinition> elements = ec.Elements.Where(ed => !ed.cgIsInherited(ec.Structure)).ToList();

            //    if (elements.Any())
            //    {
            //        filtered.Add(new StructureElementCollection { Structure = ec.Structure, Elements = elements });
            //    }
            //}

            //return filtered;
        }

        return [];
    }

    /// <summary>Strongest extended binding.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>A BindingStrength?</returns>
    public BindingStrength? StrongestExtendedBinding(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        if (_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings) &&
            (bindings != null))
        {
            return bindings
                .SelectMany(ec => ec.Elements.Select(ed => ed.Binding.Strength))
                .OrderBy(s => s, BindingStrengthComparer.Instance)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>Extended binding strength by type.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,BindingStrength&gt;</returns>
    public IReadOnlyDictionary<string, BindingStrength> ExtendedBindingStrengthByType(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        Dictionary<string, BindingStrength> bindingStrengthByType = [];

        if (!_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? bindings))
        {
            return bindingStrengthByType;
        }

        // traverse existing bindings to find the strongest for each type
        foreach (StructureElementCollection ec in bindings)
        {
            foreach (ElementDefinition ed in ec.Elements)
            {
                foreach (ElementDefinition.TypeRefComponent tr in ed.Type)
                {
                    if (bindingStrengthByType.TryGetValue(tr.Code, out BindingStrength bs) &&
                        BindingStrengthComparer.Instance.Compare(bs, ed.Binding.Strength!) <= 0)
                    {
                        continue;
                    }

                    bindingStrengthByType[tr.Code] = (BindingStrength)ed.Binding.Strength!;
                }
            }
        }

        return bindingStrengthByType;
    }

    /// <summary>Bindings for vs.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,ElementDefinition&gt;</returns>
    public IEnumerable<StructureElementCollection> BindingsForVs(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        if (!_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? core))
        {
            core = [];
        }

        if (!_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? extended))
        {
            extended = [];
        }

        return core.Union(extended);
    }

    /// <summary>Strongest binding.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>A BindingStrength?</returns>
    public BindingStrength? StrongestBinding(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        BindingStrength? cbs = null;
        BindingStrength? ebs = null;

        if (_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? core))
        {
            cbs = core
                .SelectMany(ec => ec.Elements.Select(ed => ed.Binding.Strength))
                .OrderBy(s => s, BindingStrengthComparer.Instance)
                .FirstOrDefault();
        }

        if (_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? extended))
        {
            ebs = extended
                .SelectMany(ec => ec.Elements.Select(ed => ed.Binding.Strength))
                .OrderBy(s => s, BindingStrengthComparer.Instance)
                .FirstOrDefault();
        }

        return BindingStrengthComparer.Instance.Compare(cbs, ebs) > 0 ? cbs : ebs;
    }

    /// <summary>Strongest binding.</summary>
    /// <param name="bindings">The bindings.</param>
    /// <returns>A BindingStrength?</returns>
    public BindingStrength? StrongestBinding(IEnumerable<StructureElementCollection> bindings)
    {
        // we want descending order so that strongest is first
        return bindings
            .SelectMany(ec => ec.Elements.Select(ed => ed.Binding.Strength))
            .OrderByDescending(s => s, BindingStrengthComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>Binding strength by type.</summary>
    /// <param name="valueSetUrl">URL of the value set.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,BindingStrength&gt;</returns>
    public IReadOnlyDictionary<string, BindingStrength> BindingStrengthByType(string valueSetUrl)
    {
        string url = VersionedUrlForVs(valueSetUrl);

        Dictionary<string, BindingStrength> bindingStrengthByType = [];

        if (!_coreBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? core))
        {
            core = [];
        }

        if (!_extendedBindingEdsByPathByValueSet.TryGetValue(url, out List<StructureElementCollection>? extended))
        {
            extended = [];
        }

        // note that we do not have to worry about structure collisions between the two since no structure can be in both
        IEnumerable<StructureElementCollection> all = core.Union(extended);

        if (!all.Any())
        {
            return bindingStrengthByType;
        }

        // traverse existing bindings to find the strongest for each type
        foreach (ElementDefinition ed in all.SelectMany(ec => ec.Elements))
        {
            foreach (ElementDefinition.TypeRefComponent tr in ed.Type)
            {
                if (bindingStrengthByType.TryGetValue(tr.Code, out BindingStrength bs) &&
                    BindingStrengthComparer.Instance.Compare(bs, ed.Binding.Strength!) <= 0)
                {
                    continue;
                }

                bindingStrengthByType[tr.Code] = (BindingStrength)ed.Binding.Strength!;
            }
        }

        return bindingStrengthByType;
    }

    /// <summary>Binding strength by type.</summary>
    /// <param name="bindings">The bindings.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,BindingStrength&gt;</returns>
    public IReadOnlyDictionary<string, BindingStrength> BindingStrengthByType(IEnumerable<StructureElementCollection> bindings)
    {
        Dictionary<string, BindingStrength> bindingStrengthByType = [];

        if (!bindings.Any())
        {
            return bindingStrengthByType;
        }

        // traverse existing bindings to find the strongest for each type
        foreach (ElementDefinition ed in bindings.SelectMany(ec => ec.Elements))
        {
            foreach (ElementDefinition.TypeRefComponent tr in ed.Type)
            {
                if (bindingStrengthByType.TryGetValue(tr.Code, out BindingStrength bs) &&
                    BindingStrengthComparer.Instance.Compare(bs, ed.Binding.Strength!) <= 0)
                {
                    continue;
                }

                bindingStrengthByType[tr.Code] = (BindingStrength)ed.Binding.Strength!;
            }
        }

        return bindingStrengthByType;
    }

    /// <summary>
    /// Adds a value set to the definition collection.
    /// </summary>
    /// <param name="valueSet">The value set to be added.</param>
    public void AddValueSet(ValueSet valueSet, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // DSTU2 has embedded CodeSystems
        if ((fhirVersion == FhirReleases.FhirSequenceCodes.DSTU2) &&
            (valueSet.Contained.Count != 0))
        {
            foreach (Resource contained in valueSet.Contained)
            {
                if (contained is CodeSystem cs)
                {
                    // use the same id
                    if (string.IsNullOrEmpty(cs.Id))
                    {
                        cs.Id = valueSet.Id;
                    }

                    AddCodeSystem(cs, packageId, packageVersion);

                    // use all values from the code system
                    valueSet.Compose ??= new();

                    if (valueSet.Compose.Include == null)
                    {
                        valueSet.Compose.Include = [];

                    }

                    // add everything from this CodeSystem if it is not referenced at all in the ValueSet
                    if ((valueSet.Compose.Include.Count == 0) ||
                        !valueSet.Compose.Include.Any(ci => ci.System == cs.Url))
                    {
                        valueSet.Compose.Include.Add(new ValueSet.ConceptSetComponent
                        {
                            SystemElement = new FhirUri(cs.Url),
                        });
                    }

                    //if (cs.Concept.Count != 0)
                    //{
                    //    // recursively add concepts
                    //    addCsConceptsToVs(valueSet, new FhirUri(cs.Url), cs.Concept);
                    //}
                    //else
                    //{
                    //    valueSet.Compose.Include.Add(new ValueSet.ConceptSetComponent
                    //    {
                    //        SystemElement = new FhirUri(cs.Url),
                    //    });
                    //}
                }
            }
        }

        string vsVersion = valueSet.Version ?? packageVersion;
        string vsUrl = valueSet.Url;

        int index = vsUrl.LastIndexOf('|');

        if (index == -1)
        {
            vsUrl = $"{vsUrl}|{vsVersion}";
        }
        // check for URLs that include pipes
        else if (!vsUrl.EndsWith($"|{vsVersion}", StringComparison.Ordinal))
        {
            // if the version is not at the end, we need to add it
            vsUrl = $"{vsUrl}|{vsVersion}";
        }

        // check to see if this resource already exists
        if (_valueSetsByVersionedUrl.TryGetValue(vsUrl, out ValueSet? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        string unversioned = UnversionedUrlForVs(valueSet.Url);

        // TODO: they actually meant to use the pipe character in the URL...
        //// check the compse.include.system for using a canonical URL where it should not
        //foreach (ValueSet.ConceptSetComponent csc in valueSet.Compose?.Include ?? [])
        //{
        //    int index = csc.System?.IndexOf('|') ?? -1;
        //    if (index == -1)
        //    {
        //        continue;
        //    }

        //    // we only know that HL7 terminologies do not use vertical pipes
        //    if (csc.System!.StartsWith("http://hl7.org", StringComparison.Ordinal) ||
        //        csc.System!.StartsWith("http://terminology.hl7.org", StringComparison.Ordinal))
        //    {
        //        Console.WriteLine($"ValueSet {valueSet.Id} ({valueSet.Url}) has a compose.include.system with a version, fixing...");
        //        csc.Version = csc.System[(index + 1)..];
        //        csc.System = csc.System[0..index];
        //    }
        //}

        // check for value sets that are incorrectly flagged as limited expansions
        if (_notActuallyLimitedExpansions.Contains(vsUrl) ||
            _notActuallyLimitedExpansions.Contains(unversioned))
        {
            for (int i = 0; i < (valueSet.Expansion?.Parameter?.Count ?? 0); i++)
            {
                if (valueSet.Expansion!.Parameter[i].Name == "limitedExpansion")
                {
                    valueSet.Expansion.Parameter[i].Value = new FhirBoolean(false);
                    break;
                }
            }
        }

        // check for value sets that we know have incorrect expansions
        if (_valueSetsWithIncorrectExpansions.Contains(vsUrl) ||
            _valueSetsWithIncorrectExpansions.Contains(unversioned))
        {
            valueSet.Expansion = null;
        }

        if (_valueSetVersions.TryGetValue(unversioned, out string[]? versions))
        {
            if (!versions.Contains(valueSet.Version))
            {
                _valueSetVersions[unversioned] = [.. versions, vsVersion];
            }
        }
        else
        {
            _valueSetVersions[unversioned] = [vsVersion];
        }

        if (!_valueSetUrlsById.ContainsKey(valueSet.Id))
        {
            _valueSetUrlsById[valueSet.Id] = vsUrl;
        }

        /* ginoc: 2024.07.01 - Issues with published expansions
         * - R4, R4B, R5: Units of Time expansion only contains the Chinese translation instead of default values
         */
        if ((unversioned == "http://hl7.org/fhir/ValueSet/units-of-time") &&
            (valueSet.Expansion?.Contains.Any() ?? false) &&
            !valueSet.Expansion.Contains.First().Display[0].IsAsciiLetter())
        {
            foreach (ValueSet.ContainsComponent cc in valueSet.Expansion.Contains)
            {
                if (!cc.Display[0].IsAsciiLetter())
                {
                    switch (cc.Code)
                    {
                        case "s":
                            cc.Display = "second";
                            break;

                        case "min":
                            cc.Display = "minute";
                            break;

                        case "h":
                            cc.Display = "hour";
                            break;

                        case "d":
                            cc.Display = "day";
                            break;

                        case "wk":
                            cc.Display = "week";
                            break;

                        case "mo":
                            cc.Display = "month";
                            break;

                        case "a":
                            cc.Display = "year";
                            break;
                    }
                }
            }
        }

        if (_valueSetsByVersionedUrl.TryGetValue(vsUrl, out ValueSet? existing) && (existing != null))
        {
            // sort out unexpanded vs expanded vs multiple expansions
            if ((valueSet.Expansion is not null) && (existing.Expansion is null))
            {
                existing.Expansion = new();

                // copy the expansion into the existing
                valueSet.Expansion.CopyTo(existing.Expansion);
            }
            else if ((valueSet.Expansion is null) && (existing.Expansion is not null))
            {
                // TODO: Bug in 5.x Firely SDK does not copy the *resource* correctly - just use the one with the expansion for now
                //valueSet.Expansion = new();

                //// copy the existing expansion into the new record
                //existing.Expansion.CopyTo(valueSet.Expansion);

                //// copy the new record into the existing to keep the most recent in all dictionaries
                //valueSet.CopyTo(existing);
            }
            else if ((valueSet.Expansion is not null) && (existing.Expansion is not null))
            {
                // merge the expansion values
                existing.Expansion.Contains = existing.Expansion.Contains.Union(valueSet.Expansion.Contains).ToList();

                // merge the parameters
                existing.Expansion.Parameter = existing.Expansion.Parameter.Union(valueSet.Expansion.Parameter).ToList();

                // update the total
                existing.Expansion.Total = existing.Expansion.Contains.Count;
            }
            else
            {
                // keep the more recent if possible
                DateTimeOffset existingUpdated = existing.Meta?.LastUpdated?.UtcDateTime ?? DateTimeOffset.MinValue;
                DateTimeOffset incomingUpdated = valueSet.Meta?.LastUpdated?.UtcDateTime ?? DateTimeOffset.MinValue;

                if (existingUpdated < incomingUpdated)
                {
                    // TODO: Bug in 5.x Firely SDK does not copy the *resource* correctly - just use the one with the expansion for now
                    _valueSetsByVersionedUrl[vsUrl] = valueSet;
                    //// copy the new record into the existing to keep the most recent in all dictionaries
                    //valueSet.CopyTo(existing);
                }
            }

            // done
            return;
        }

        // add the package source
        AddPackageSource(valueSet, packageId, packageVersion);

        _valueSetsByVersionedUrl[vsUrl] = valueSet;
        TrackResource(valueSet);

        return;

        void addCsConceptsToVs(ValueSet vs, FhirUri system, List<CodeSystem.ConceptDefinitionComponent> concepts)
        {
            if (concepts.Count == 0)
            {
                return;
            }

            vs.Compose.Include.Add(new ValueSet.ConceptSetComponent
            {
                SystemElement = system,
                Concept = concepts
                .Select(csc => new ValueSet.ConceptReferenceComponent()
                {
                    Code = csc.Code,
                    Display = csc.Display,
                }).ToList()
            });

            foreach (CodeSystem.ConceptDefinitionComponent concept in concepts)
            {
                if (concept.Concept.Count != 0)
                {
                    addCsConceptsToVs(vs, system, concept.Concept);
                }
            }
        }
    }

    ///// <summary>Interface to check if a resource has a 'URL' element.</summary>
    //private interface IHasUrl
    //{
    //    string Url { get; set; }
    //}

    /// <summary>
    /// Adds a resource to the definition collection based on its type.
    /// </summary>
    /// <param name="r">The resource to add.</param>
    /// <param name="fhirVersion">The FHIR version of the resource.</param>
    /// <param name="canonicalSource">The canonical source of the resource.</param>
    public void AddResource(object r, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion, string canonicalSource)
    {
        // This was an issue with Azure FHIR server and I believe has been corrected
        //// check for canonical URLs that are listed as "[base]"
        //if (r is IHasUrl withUrl)
        //{
        //    if (withUrl.Url.StartsWith("[base]", StringComparison.Ordinal))
        //    {
        //        withUrl.Url = canonicalSource;
        //    }
        //}

        // process the resource according to its type
        switch (r)
        {
            case CodeSystem cs:
                AddCodeSystem(cs, packageId, packageVersion);
                break;

            case ValueSet vs:
                AddValueSet(vs, fhirVersion, packageId, packageVersion);
                break;

            case StructureDefinition sd:
                AddStructureDefinition(sd, fhirVersion, packageId, packageVersion);
                break;

            case CapabilityStatement caps:
                AddCapabilityStatement(caps, packageId, packageVersion, canonicalSource);
                break;

            case SearchParameter sp:
                AddSearchParameter(sp, packageId, packageVersion);
                break;

            case OperationDefinition op:
                AddOperation(op, packageId, packageVersion);
                break;

            case ImplementationGuide ig:
                AddImplementationGuide(ig, packageId, packageVersion);
                break;

            case CompartmentDefinition cd:
                AddCompartment(cd, packageId, packageVersion);
                break;

            case ConceptMap cm:
                AddConceptMap(cm, packageId, packageVersion);
                break;

            default:
                // ignore everything else
                break;
        }
    }

    /// <summary>
    /// Adds a <see cref="StructureDefinition"/> to the collection in the correct artifact class set.
    /// </summary>
    /// <param name="sd">The <see cref="StructureDefinition"/> to add.</param>
    /// <param name="fhirVersion">The <see cref="FhirReleases.FhirSequenceCodes"/> representing the FHIR version.</param>
    public void AddStructureDefinition(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        switch (sd.cgArtifactClass())
        {
            case FhirArtifactClassEnum.PrimitiveType:
                AddPrimitiveType(sd, fhirVersion, packageId, packageVersion);
                break;

            case FhirArtifactClassEnum.LogicalModel:
                AddLogicalModel(sd, fhirVersion, packageId, packageVersion);
                break;

            case FhirArtifactClassEnum.Extension:
                AddExtension(sd, fhirVersion, packageId, packageVersion);
                break;

            case FhirArtifactClassEnum.Profile:
                AddProfile(sd, fhirVersion, packageId, packageVersion);
                break;

            case FhirArtifactClassEnum.ComplexType:
                AddComplexType(sd, fhirVersion, packageId, packageVersion);
                break;

            case FhirArtifactClassEnum.Resource:
                AddResourceDefinition(sd, fhirVersion, packageId, packageVersion);
                break;

            case FhirArtifactClassEnum.Interface:
                AddInterface(sd, fhirVersion, packageId, packageVersion);
                break;
        }
    }

    public bool TryReconcileElementInheritance()
    {
        // only need to process DSTU2 and STU3
        if (FhirSequence != FhirReleases.FhirSequenceCodes.DSTU2 &&
            FhirSequence != FhirReleases.FhirSequenceCodes.STU3)
        {
            return true;
        }

        // iterate over the structures we are interested in
        foreach (IEnumerable<StructureDefinition> structures in (IEnumerable<StructureDefinition>[])[ _primitiveTypesByName.Values, _complexTypesByName.Values, _resourcesByName.Values ])
        {
            Parallel.ForEach(structures, (sd, cancellationToken) =>
            {
                // check for a base type
                if (sd.BaseDefinition == null)
                {
                    return;
                }

                List<StructureDefinition> parentSds = [];
                StructureDefinition currentSd = sd;
                while (currentSd.BaseDefinition != null)
                {
                    if (!TryResolveByCanonicalUri(currentSd.BaseDefinition, out Resource? parentResource) ||
                        !(parentResource is StructureDefinition parentSd))
                    {
                        break;
                    }
                    parentSds.Add(parentSd);
                    currentSd = parentSd;
                }

                if (parentSds.Count == 0)
                {
                    return;
                }

                if (sd.Snapshot?.Element.Any() ?? false)
                {
                    addBaseTypes(sd, parentSds, true);
                }
                if (sd.Differential?.Element.Any() ?? false)
                {
                    addBaseTypes(sd, parentSds, false);
                }
            });
        }

        return true;

        void addBaseTypes(StructureDefinition sd, List<StructureDefinition> parentSds, bool processSnapshot)
        {
            string currentPrefix = sd.Id + ".";
            int currentPrefixLen = currentPrefix.Length;

            IEnumerable<ElementDefinition> currentElements =
                (processSnapshot
                    ? sd.Snapshot?.Element.Where(ed => ed.Path.Length > currentPrefixLen)
                    : sd.Differential?.Element.Where(ed => ed.Path.Length > currentPrefixLen))
                ?? [];

            Dictionary<string, ElementDefinition> inheritanceLookup = [];

            // process structures starting from the nearest ancestor (will overwrite as we move up)
            foreach (StructureDefinition parentSd in parentSds)
            {
                string parentPrefix = parentSd.Id + ".";
                int parentPrefixLen = parentPrefix.Length;

                IEnumerable<ElementDefinition> elements =
                    (processSnapshot
                        ? parentSd.Snapshot?.Element.Where(ed => ed.Path.Length > parentPrefixLen)
                        : parentSd.Differential?.Element.Where(ed => ed.Path.Length > parentPrefixLen))
                    ?? [];

                foreach (ElementDefinition ed in elements)
                {
                    string modifiedPath = currentPrefix + ed.Path[parentPrefixLen..];
                    inheritanceLookup[modifiedPath] = ed;
                }
            }

            foreach (ElementDefinition ed in currentElements)
            {
                if (ed.Base != null)
                {
                    continue;
                }

                if (inheritanceLookup.TryGetValue(ed.Path, out ElementDefinition? inherited))
                {
                    ed.Base = new ElementDefinition.BaseComponent()
                    {
                        Path = inherited.Path,
                        Min = ed.Min,
                        Max = ed.Max,
                    };

                    // update the types so they are correct
                    if (ed.Type.Count == inherited.Type.Count)
                    {
                        ed.Type = inherited.Type.Select(tr => (ElementDefinition.TypeRefComponent)tr.DeepCopy()).ToList();
                    }
                }
            }
        }
    }

    public IReadOnlyDictionary<string, StructureDefinition> GetStructureIndexDict(FhirArtifactClassEnum artifactClass) => artifactClass switch
    {
        FhirArtifactClassEnum.PrimitiveType => _primitiveTypesByName,
        FhirArtifactClassEnum.LogicalModel => _logicalModelsByUrl,
        FhirArtifactClassEnum.Extension => _extensionsByUrl,
        FhirArtifactClassEnum.Profile => _profilesByUrl,
        FhirArtifactClassEnum.ComplexType => _complexTypesByName,
        FhirArtifactClassEnum.Resource => _resourcesByName,
        FhirArtifactClassEnum.Interface => _interfacesByName,
        _ => throw new ArgumentOutOfRangeException(nameof(artifactClass), artifactClass, null),
    };

    /// <summary>Gets the name of the primitive types by.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> PrimitiveTypesByName => _primitiveTypesByName;

    /// <summary>Adds a primitive type.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddPrimitiveType(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_primitiveTypesByName.TryGetValue(sd.Name, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // DSTU2 did not include the publication status extension, add it here for consistency
        if (fhirVersion == FhirReleases.FhirSequenceCodes.DSTU2)
        {
            sd.AddExtension(CommonDefinitions.ExtUrlStandardStatus, new Code("draft"));
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.PrimitiveType, sd, fhirVersion);

        // TODO(ginoc): Consider if we want to make this explicit on any definitions that do not have it
        //if (sd.FhirVersion == null)
        //{
        //    sd.FhirVersion = FhirVersion;
        //}

        _primitiveTypesByName[sd.Name] = sd;
        TrackResource(sd);
    }

    /// <summary>Gets the name of the complex types by.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> ComplexTypesByName => _complexTypesByName;

    /// <summary>Adds a complex type.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddComplexType(
        StructureDefinition sd,
        FhirReleases.FhirSequenceCodes fhirVersion,
        string packageId,
        string packageVersion)
    {
        // check to see if this resource already exists
        if (_complexTypesByName.TryGetValue(sd.Name, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        if (fhirVersion == FhirReleases.FhirSequenceCodes.DSTU2)
        {
            // DSTU2 did not include the publication status extension, add it here for consistency
            sd.AddExtension(CommonDefinitions.ExtUrlStandardStatus, new Code("draft"));

            // DSTU2 derivations of Quantity are actually profiles, even though they are defined as types
            if ((sd.Type == "Quantity") &&
                (sd.Name != "Quantity"))
            {
                AddProfile(sd, fhirVersion, packageId, packageVersion);
                return;
            }
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.ComplexType, sd, fhirVersion);

        _complexTypesByName[sd.Name] = sd;
        TrackResource(sd);
    }

    public IEnumerable<string> GetResourceParents(string resourceName)
    {
        HashSet<string> names = [];

        if (_resourcesByName.TryGetValue(resourceName, out StructureDefinition? sd))
        {
            names.Add(sd.Name);

            string bt = sd.cgBaseTypeName();

            if (!string.IsNullOrEmpty(bt) && (bt != sd.Name))
            {
                names.UnionWith(GetResourceParents(bt));
            }
        }

        return names;
    }

    private void AddPackageSource(DomainResource r, string packageId, string version)
    {
        Extension? ext = r.GetExtension(CommonDefinitions.ExtUrlPackageSource);
        if (ext != null)
        {
            return;
        }

        // TODO: should add URI now that the extension includes it
        r.cgAddPackageSource(packageId, version, null);

        if ((r is IVersionableConformanceResource vcr) && string.IsNullOrEmpty(vcr.Version))
        {
            vcr.Version = version;
        }
    }

    public bool TryGetPackageSource(DomainResource r, out string packageId, out string packageVersion)
    {
        Extension? ext = r.GetExtension(CommonDefinitions.ExtUrlPackageSource);
        if (ext == null)
        {
            packageId = string.Empty;
            packageVersion = string.Empty;
            return false;
        }

        packageId = ext.GetStringExtension("packageId");
        packageVersion = ext.GetStringExtension("version");

        return !string.IsNullOrEmpty(packageId) && !string.IsNullOrEmpty(packageVersion);
    }

    public class VersionedResourceEnumerator<T> : IEnumerator<T>
    {
        private IDictionary<string, Dictionary<string, T>> _source;
        private IEnumerator<KeyValuePair<string, Dictionary<string, T>>> _sourceEnumerator;

        public VersionedResourceEnumerator(IDictionary<string, Dictionary<string, T>> source)
        {
            _source = source;
            _sourceEnumerator = _source.GetEnumerator();
        }
        public T Current => _sourceEnumerator.Current.Value.OrderByDescending(v => v.Key).First().Value;

        object IEnumerator.Current => _sourceEnumerator.Current.Value.OrderByDescending(v => v.Key).First().Value!;

        public void Dispose()
        {
            if (_sourceEnumerator != null)
            {
                _sourceEnumerator.Dispose();
            }

            if (_source != null)
            {
                _source = null!;
            }
        }
        public bool MoveNext() => _sourceEnumerator.MoveNext();
        public void Reset() => _sourceEnumerator.Reset();
    }

    public VersionedResourceEnumerator<IConformanceResource> CanonicalEnumerator => new(_canonicalResources);

    /// <summary>Gets listing of resources, by Name.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> ResourcesByName => _resourcesByName;

    /// <summary>Adds a resource.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddResourceDefinition(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_resourcesByName.TryGetValue(sd.Name, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // DSTU2 did not include the publication status extension, add it here for consistency
        if (fhirVersion == FhirReleases.FhirSequenceCodes.DSTU2)
        {
            sd.AddExtension(CommonDefinitions.ExtUrlStandardStatus, new Code("draft"));
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.Resource, sd, fhirVersion);

        _resourcesByName[sd.Name] = sd;
        TrackResource(sd);
    }

    /// <summary>Gets the listing of interfaces, by Name.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> InterfacesByName => _interfacesByName;

    /// <summary>Adds an interface.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddInterface(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_interfacesByName.TryGetValue(sd.Name, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // DSTU2 did not include the publication status extension, add it here for consistency
        if (fhirVersion == FhirReleases.FhirSequenceCodes.DSTU2)
        {
            sd.AddExtension(CommonDefinitions.ExtUrlStandardStatus, new Code("draft"));
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.Resource, sd, fhirVersion);

        _interfacesByName[sd.Name] = sd;
        TrackResource(sd);
    }

    /// <summary>Finds all resources that claim to implement the specified interface.</summary>
    /// <param name="interfaceSd">            The interface SD.</param>
    /// <param name="includeChildInterfaces">(Optional) True to include, false to exclude the child
    ///  interfaces.</param>
    /// <returns>
    /// An enumerator that allows foreach to be used to process resources for interface in this
    /// collection.
    /// </returns>
    public IEnumerable<StructureDefinition> ResourcesForInterface(StructureDefinition interfaceSd, bool includeChildInterfaces = true)
    {
        HashSet<string> implementationUrls = [interfaceSd.Url];

        if (includeChildInterfaces)
        {
            FindChildInterfaces(interfaceSd.Url, implementationUrls);
        }

        foreach (StructureDefinition sd in _resourcesByName.Values)
        {
            if ((sd.GetExtension(CommonDefinitions.ExtUrlImplements)?.Value is FhirUri valueUri) &&
                implementationUrls.Contains(valueUri.Value))
            {
                yield return sd;
            }
        }
    }

    /// <summary>Searches for known parent interfaces.</summary>
    /// <param name="url">       URL of the resource.</param>
    /// <param name="interfaces">The interfaces.</param>
    private void FindChildInterfaces(string url, HashSet<string> interfaces)
    {
        // first, check for additional interfaces that contain this interface
        foreach (StructureDefinition sd in _interfacesByName.Values)
        {
            if ((sd.GetExtension(CommonDefinitions.ExtUrlImplements)?.Value is FhirUri valueUri) &&
                (valueUri.Value == url))
            {
                // add this interface
                interfaces.Add(sd.Url);

                // recurse
                FindChildInterfaces(sd.Url, interfaces);
            }
        }
    }

    /// <summary>Gets the parent interface.</summary>
    /// <param name="sd">The structure definition.</param>
    /// <returns>The parent interface.</returns>
    public StructureDefinition? GetParentInterface(StructureDefinition sd)
    {
        if (sd.GetExtension(CommonDefinitions.ExtUrlImplements)?.Value is FhirUri valueUri)
        {
            if (_interfacesByName.TryGetValue(valueUri.Value, out StructureDefinition? parent))
            {
                return parent;
            }

            int lastSlashIndex = valueUri.Value.LastIndexOf('/');
            if (lastSlashIndex != -1)
            {
                string parentName = valueUri.Value.Substring(lastSlashIndex + 1);
                if (_interfacesByName.TryGetValue(parentName, out parent))
                {
                    return parent;
                }
            }
        }

        return null;
    }

    /// <summary>Gets the name of the logical models by.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> LogicalModelsByUrl => _logicalModelsByUrl;

    /// <summary>Adds a logical model.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddLogicalModel(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_logicalModelsByUrl.TryGetValue(sd.Url, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.LogicalModel, sd, fhirVersion);

        _logicalModelsByUrl[sd.Url] = sd;
        TrackResource(sd);
    }

    /// <summary>Gets extensions, keyed by URL.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> ExtensionsByUrl => _extensionsByUrl;

    /// <summary>Gets extensions, keyed by URL, grouped by Path</summary>
    public IReadOnlyDictionary<string, Dictionary<string, StructureDefinition>> ExtensionsByPath => _extensionsByPath;

    /// <summary>Adds an extension.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddExtension(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_extensionsByUrl.TryGetValue(sd.Url, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.Extension, sd, fhirVersion);

        string url = sd.Url;

        // add to main tracking dictionary
        _extensionsByUrl[sd.Url] = sd;
        TrackResource(sd);

        // traverse context to add to path tracking dictionary
        foreach (StructureDefinition.ContextComponent ctx in sd.Context)
        {
            if (ctx.Type != StructureDefinition.ExtensionContextType.Element)
            {
                // throw new ArgumentException($"Invalid extension context type: {context.Type}");
                _errors.Add($"AddExtension <<< StructureDefinition {sd.Name} ({sd.Id}) unhandled context type: {ctx.Type}");
                continue;
            }

            if (string.IsNullOrEmpty(ctx.Expression))
            {
                _errors.Add($"AddExtension <<< StructureDefinition {sd.Name} ({sd.Id}) missing context expression");
                continue;
            }

            if (!_extensionsByPath.TryGetValue(ctx.Expression, out Dictionary<string, StructureDefinition>? value))
            {
                value = ([]);
                _extensionsByPath[ctx.Expression] = value;
            }

            value[url] = sd;
        }
    }

    /// <summary>Gets URL of the profiles by.</summary>
    public IReadOnlyDictionary<string, StructureDefinition> ProfilesByUrl => _profilesByUrl;

    /// <summary>Profiles for base.</summary>
    /// <param name="resourceType">Type of the resource.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,StructureDefinition&gt;</returns>
    public IReadOnlyDictionary<string, StructureDefinition> ProfilesForBase(string resourceType)
    {
        if (_profilesByBaseType.TryGetValue(resourceType, out Dictionary<string, StructureDefinition>? sdDict))
        {
            return sdDict;
        }

        return new Dictionary<string, StructureDefinition>();
    }

    /// <summary>Adds a profile.</summary>
    /// <param name="sd">The structure definition.</param>
    public void AddProfile(StructureDefinition sd, FhirReleases.FhirSequenceCodes fhirVersion, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_profilesByUrl.TryGetValue(sd.Url, out StructureDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(sd, packageId, packageVersion);

        // add field orders to elements
        ProcessElements(FhirArtifactClassEnum.Profile, sd, fhirVersion);

        _profilesByUrl[sd.Url] = sd;
        TrackResource(sd);

        if (!string.IsNullOrEmpty(sd.Type))
        {
            if (!_profilesByBaseType.TryGetValue(sd.Type, out Dictionary<string, StructureDefinition>? sdDict))
            {
                sdDict = [];
                _profilesByBaseType.Add(sd.Type, sdDict);
            }

            sdDict[sd.Url] = sd;
        }
    }

    /// <summary>Gets the global search parameters, by URL.</summary>
    public IReadOnlyDictionary<string, SearchParameter> GlobalSearchParameters => _globalSearchParameters;

    /// <summary>Gets URL of the search parameters by.</summary>
    public IReadOnlyDictionary<string, SearchParameter> SearchParametersByUrl => _searchParamsByUrl;

    /// <summary>Searches for the first parameters for base.</summary>
    /// <param name="resourceType">Type of the resource.</param>
    /// <returns>The found parameters for base.</returns>
    public IReadOnlyDictionary<string, SearchParameter> SearchParametersForBase(string resourceType)
    {
        if (_searchParamsByBase.TryGetValue(resourceType, out Dictionary<string, SearchParameter>? spDict))
        {
            return spDict;
        }

        return new Dictionary<string, SearchParameter>();
    }

    /// <summary>Adds a search parameter.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="sp">            The search parameter.</param>
    /// <param name="doNotOverwrite">(Optional) True to do not overwrite.</param>
    public void AddSearchParameter(SearchParameter sp, string packageId, string packageVersion, bool doNotOverwrite = false)
    {
        if (string.IsNullOrEmpty(sp.Url))
        {
            // best guess at a canonical URL for this
            sp.Url = string.Join("/", MainPackageCanonical, "SearchParameter", sp.Id).Replace("//", "/");
        }

        // check to see if this resource already exists
        if (_searchParamsByUrl.TryGetValue(sp.Url, out SearchParameter? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        if (doNotOverwrite && _searchParamsByUrl.ContainsKey(sp.Url))
        {
            return;
        }

        // add the package source
        AddPackageSource(sp, packageId, packageVersion);

        _searchParamsByUrl[sp.Url] = sp;
        TrackResource(sp);

        //// check for a single base with a replacement resource type
        //if (sp.BaseElement.Count == 1)
        //{
        //    Code<VersionIndependentResourceTypesAll>? baseElement = sp.BaseElement.First();
        //    if (baseElement != null)
        //    {
        //        string spBase = Hl7.Fhir.Utility.EnumUtility.GetLiteral(baseElement.Value) ?? string.Empty;

        //        if (spBase == "Resource")
        //        {
        //            Code? extCode = baseElement.GetExtensionValue<Code>(CommonDefinitions.ExtUrlSearchParameterBaseType);

        //            string? extBase = (extCode is null)
        //                ? null
        //                : extCode.Value;

        //            VersionIndependentResourceTypesAll? replacement = extBase is null
        //                ? null
        //                : EnumUtility.ParseLiteral<VersionIndependentResourceTypesAll>(extBase);

        //            if (replacement is not null)
        //            {
        //                sp.BaseElement = [ new Code<VersionIndependentResourceTypesAll>(replacement) ];
        //            }
        //        }
        //    }
        //}

        // iterate over the bases
        foreach (Code<VersionIndependentResourceTypesAll>? baseElementCode in sp.BaseElement)
        {
            if (baseElementCode == null)
            {
                // TODO(ginoc): Check to see if this is actually possible
                throw new Exception("SearchParameter.Base == null");
                //continue;
            }

            string spBase = baseElementCode.ObjectValue?.ToString() ?? string.Empty;  // Hl7.Fhir.Utility.EnumUtility.GetLiteral(rt.Value) ?? string.Empty;

            if (string.IsNullOrEmpty(spBase))
            {
                // TODO(ginoc): Check to see if this is actually possible
                throw new Exception("SearchParameter.Base == null");
            }

            // check for a base of "Resource" and add to the global list
            if (spBase == "Resource")
            {
                _globalSearchParameters[sp.Url] = sp;
                continue;
            }

            if ((!_searchParamsByBase.TryGetValue(spBase, out Dictionary<string, SearchParameter>? spDict)) ||
                (spDict == null))
            {
                spDict = [];
                _searchParamsByBase.Add(spBase, spDict);
            }

            _ = spDict.TryAdd(sp.Url, sp);
        }
    }

    /// <summary>Gets the search result parameter.</summary>
    public IReadOnlyDictionary<string, FhirQueryParameter> SearchResultParameters => _searchResultParameters;

    /// <summary>Adds a search result parameter.</summary>
    /// <param name="sp">The search parameter.</param>
    public void AddSearchResultParameter(FhirQueryParameter sp)
    {
        _searchResultParameters[sp.Url] = sp;
    }

    /// <summary>Gets options for controlling the HTTP.</summary>
    public IReadOnlyDictionary<string, FhirQueryParameter> HttpParameters => _allInteractionParameters;

    /// <summary>Adds a HTTP query parameter.</summary>
    /// <param name="sp">The search parameter.</param>
    public void AddHttpQueryParameter(FhirQueryParameter sp)
    {
        _allInteractionParameters[sp.Url] = sp;
    }

    /// <summary>Gets URL of the operations by.</summary>
    public IReadOnlyDictionary<string, OperationDefinition> OperationsByUrl => _operationsByUrl;

    /// <summary>Type-level Operations for a resource type.</summary>
    /// <param name="resourceType">Type of the resource.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,OperationDefinition&gt;</returns>
    public IReadOnlyDictionary<string, OperationDefinition> TypeOperationsForResource(string resourceType)
    {
        if (_typeOperationsByType.TryGetValue(resourceType, out Dictionary<string, OperationDefinition>? opDict))
        {
            return opDict;
        }

        return new Dictionary<string, OperationDefinition>();
    }

    /// <summary>Type-level Operations for a resource type.</summary>
    /// <param name="resourceType">Type of the resource.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,OperationDefinition&gt;</returns>
    public IReadOnlyDictionary<string, OperationDefinition> TypeOperationsForResource(VersionIndependentResourceTypesAll resourceType) =>
        TypeOperationsForResource(Hl7.Fhir.Utility.EnumUtility.GetLiteral(resourceType) ?? string.Empty);

    /// <summary>Instance-level Operations for a resource type.</summary>
    /// <param name="resourceType">Type of the resource.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,OperationDefinition&gt;</returns>
    public IReadOnlyDictionary<string, OperationDefinition> InstanceOperationsForResource(string resourceType)
    {
        if (_instanceOperationsByType.TryGetValue(resourceType, out Dictionary<string, OperationDefinition>? opDict))
        {
            return opDict;
        }

        return new Dictionary<string, OperationDefinition>();
    }

    /// <summary>Instance-level Operations for a resource type.</summary>
    /// <param name="resourceType">Type of the resource.</param>
    /// <returns>An IReadOnlyDictionary&lt;string,OperationDefinition&gt;</returns>
    public IReadOnlyDictionary<string, OperationDefinition> InstanceOperationsForResource(VersionIndependentResourceTypesAll resourceType) =>
        InstanceOperationsForResource(Hl7.Fhir.Utility.EnumUtility.GetLiteral(resourceType) ?? string.Empty);

    /// <summary>Gets the system-level operations.</summary>
    public IReadOnlyDictionary<string, OperationDefinition> SystemOperations => _systemOperations;

    /// <summary>Adds an operation.</summary>
    /// <param name="op">The operation.</param>
    public void AddOperation(OperationDefinition op, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_operationsByUrl.TryGetValue(op.Url, out OperationDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(op, packageId, packageVersion);

        // add field orders to parameters
        ProcessParameters(op);

        _operationsByUrl[op.Url] = op;
        TrackResource(op);

        // check to see if this is a system level operation
        if (op.System == true)
        {
            _systemOperations[op.Url] = op;
        }

        // add to the correct resource dictionary
        foreach (VersionIndependentResourceTypesAll? t in op.Resource)
        {
            if (t == null)
            {
                continue;
            }

            string rt = Hl7.Fhir.Utility.EnumUtility.GetLiteral(t) ?? string.Empty;

            if ((!_typeOperationsByType.TryGetValue(rt, out Dictionary<string, OperationDefinition>? typeDict)) ||
                (typeDict == null))
            {
                typeDict = [];
                _typeOperationsByType.Add(rt, typeDict);
            }

            if (op.Type == true)
            {
                typeDict[op.Url] = op;
            }

            if ((!_instanceOperationsByType.TryGetValue(rt, out Dictionary<string, OperationDefinition>? instanceDict)) ||
                (instanceDict == null))
            {
                instanceDict = [];
                _instanceOperationsByType.Add(rt, instanceDict);
            }

            if (op.Instance == true)
            {
                instanceDict[op.Url] = op;
            }
        }
    }

    public IReadOnlyDictionary<string, CapabilityStatement> CapabilityStatementsByUrl => _capabilityStatementsByUrl;

    /// <summary>
    /// Adds a capability statement to the definition collection.
    /// </summary>
    /// <param name="cs">The capability statement to add.</param>
    /// <param name="canonicalSource">The canonical source of the capability statement.</param>
    public void AddCapabilityStatement(CapabilityStatement cs, string packageId, string packageVersion, string canonicalSource = "")
    {
        if (string.IsNullOrEmpty(cs.Url))
        {
            cs.Url = canonicalSource.EndsWith('/') ? canonicalSource + cs.Id : canonicalSource + "/" + cs.Id;
        }

        // check to see if this resource already exists
        if (_capabilityStatementsByUrl.TryGetValue(cs.Url, out CapabilityStatement? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(cs, packageId, packageVersion);

        _capabilityStatementsByUrl[cs.Url] = cs;
        TrackResource(cs);
    }

    public IReadOnlyDictionary<string, ImplementationGuide> ImplementationGuidesByUrl => _implementationGuidesByUrl;

    public void AddImplementationGuide(ImplementationGuide ig, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_implementationGuidesByUrl.TryGetValue(ig.Url, out ImplementationGuide? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(ig, packageId, packageVersion);

        _implementationGuidesByUrl[ig.Url] = ig;
        TrackResource(ig);
    }

    public IReadOnlyDictionary<string, CompartmentDefinition> CompartmentsByUrl => _compartmentsByUrl;

    public void AddCompartment(CompartmentDefinition compartmentDefinition, string packageId, string packageVersion)
    {
        // check to see if this resource already exists
        if (_compartmentsByUrl.TryGetValue(compartmentDefinition.Url, out CompartmentDefinition? prev) &&
            TryGetPackageSource(prev, out string prevPackageId, out _))
        {
            // official examples packages contain all the definitions, but we want the ones from core
            if (prevPackageId.Contains(".core") && !packageId.Contains(".core"))
            {
                return;
            }
        }

        // add the package source
        AddPackageSource(compartmentDefinition, packageId, packageVersion);

        _compartmentsByUrl[compartmentDefinition.Url] = compartmentDefinition;
        TrackResource(compartmentDefinition);
    }

    /// <summary>Attempts to resolve element tree.</summary>
    /// <param name="id">             The identifier.</param>
    /// <param name="sd">             [out] The found structure definition.</param>
    /// <param name="elementSequence">[out] The element sequence.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    public bool TryResolveElementTree(
        string path,
        [NotNullWhen(true)] out StructureDefinition? sd,
        [NotNullWhen(true)] out ElementDefinition[] elementSequence)
    {
        sd = null;

        List<ElementDefinition> sequence = [];

        if (string.IsNullOrEmpty(path))
        {
            elementSequence = [];
            return false;
        }

        string[] parts = path.Split('.');
        string structureName = parts[0];

        if (_resourcesByName.TryGetValue(structureName, out sd))
        {
            if (parts.Length == 1)
            {
                if (sd.cgRootElement() is ElementDefinition ed)
                {
                    sequence.Add(ed);
                    elementSequence = sequence.ToArray();
                    return true;
                }

                elementSequence = [];
                return false;
            }

            // iterate over the path components
            for (int i = 0; i < parts.Length; i++)
            {
                string currentPath = string.Join(".", parts.Take(i + 1));

                if (sd.cgTryGetElementByPath(currentPath, out ElementDefinition? currentEd))
                {
                    sequence.Add(currentEd);
                    continue;
                }

                elementSequence = [];
                return false;
            }

            elementSequence = sequence.ToArray();
            return elementSequence.Length != 0;
        }

        if (_complexTypesByName.TryGetValue(structureName, out sd))
        {
            if (parts.Length == 1)
            {
                if (sd.cgRootElement() is ElementDefinition ed)
                {
                    sequence.Add(ed);
                    elementSequence = sequence.ToArray();
                    return true;
                }

                elementSequence = [];
                return false;
            }

            // iterate over the path components
            for (int i = 1; i < parts.Length; i++)
            {
                string currentPath = string.Join(".", parts.Take(i + 1));

                if (sd.cgTryGetElementById(currentPath, out ElementDefinition? currentEd))
                {
                    sequence.Add(currentEd);
                    continue;
                }

                elementSequence = [];
                return false;
            }

            elementSequence = sequence.ToArray();
            return elementSequence.Length != 0;
        }

        elementSequence = [];
        return false;
    }

    /// <summary>Attempts to get structure a StructureDefinition based on the given key.</summary>
    /// <param name="key">The key (name or url, depending on type) of the structure.</param>
    /// <param name="sd">     [out] The found structure definition.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    public bool TryGetStructure(string key, [NotNullWhen(true)] out StructureDefinition? sd)
    {
        if (_resourcesByName.TryGetValue(key, out sd) ||
            _complexTypesByName.TryGetValue(key, out sd) ||
            _primitiveTypesByName.TryGetValue(key, out sd) ||
            _profilesByUrl.TryGetValue(key, out sd) ||
            _logicalModelsByUrl.TryGetValue(key, out sd))
        {
            return true;
        }

        string key2;
        if (key.Contains('/'))
        {
            key2 = key.Substring(key.LastIndexOf('/') + 1);
        }
        else
        {
            key2 = "http://hl7.org/fhir/StructureDefinition/" + key;
        }

        if (_resourcesByName.TryGetValue(key2, out sd) ||
            _complexTypesByName.TryGetValue(key2, out sd) ||
            _primitiveTypesByName.TryGetValue(key2, out sd) ||
            _profilesByUrl.TryGetValue(key2, out sd) ||
            _logicalModelsByUrl.TryGetValue(key2, out sd))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to find an element in the structure definition by its path.
    /// </summary>
    /// <param name="path">The path of the element.</param>
    /// <param name="sd">The found structure definition.</param>
    /// <param name="ed">The found element definition.</param>
    /// <returns><c>true</c> if the element is found; otherwise, <c>false</c>.</returns>
    public bool TryFindElementByPath(
        string path,
        [NotNullWhen(true)] out StructureDefinition? sd,
        [NotNullWhen(true)] out ElementDefinition? ed)
    {
        sd = null;
        ed = null;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string[] parts = path.Split('.');
        string structureName = parts[0];

        if (_resourcesByName.TryGetValue(structureName, out sd))
        {
            if (parts.Length == 1)
            {
                ed = sd.cgRootElement();
                return ed != null;
            }

            // check to see if we can find this element in this sd
            return sd.cgTryGetElementByPath(path, out ed);
        }

        if (_complexTypesByName.TryGetValue(structureName, out sd))
        {
            if (parts.Length == 1)
            {
                ed = sd.cgRootElement();
                return ed != null;
            }

            return sd.cgTryGetElementByPath(path, out ed);
        }

        return false;
    }
}
