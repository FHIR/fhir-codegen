// <copyright file="XVerStructureCatalog.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Text.RegularExpressions;
using Fhir.CodeGen.Common.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;

namespace Fhir.CodeGen.Comparison.Models;

internal sealed class XVerStructureCatalog : IDisposable
{
    private readonly IDbConnection? _db;
    private readonly XVerOperandEvidence? _evidence;
    private bool _disposed;
    private readonly Dictionary<int, DbStructureDefinition> _structures;
    private readonly Dictionary<int, DbFhirPackage> _packages;
    private readonly Dictionary<int, DbElement> _elements;
    private readonly Dictionary<int, DbStructureDefinitionFacts> _facts;
    private ILookup<int, DbElement> _elementsByOwner;
    private ILookup<int, DbElementType> _typesByElement;
    private Dictionary<(int, int, string), DbElement> _ownedElements;
    private readonly Dictionary<int, IReadOnlyList<DbStructureDefinition>> _ancestors = [];
    private readonly Dictionary<int, StructureDefinition> _payloads = [];
    private readonly Dictionary<int, Dictionary<string, ElementDefinition>> _payloadElements = [];
    private readonly Dictionary<int, DbElement> _roots = [];
    private readonly HashSet<string> _exclusions;
    private readonly List<string> _diagnostics = [];

    internal XVerStructureCatalog(IDbConnection db, HashSet<string>? exclusions = null, string? elementTableName = null)
        : this(db, DbStructureDefinition.SelectList(db), DbFhirPackage.SelectList(db),
            DbElement.SelectList(db, dbTableName: elementTableName), DbStructureDefinitionFacts.SelectList(db),
            DbElementType.SelectList(db), exclusions ?? [], null)
    {
    }

    internal XVerStructureCatalog(XVerOperandEvidence evidence)
        : this(null, evidence.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName),
            evidence.Rows<DbFhirPackage>(DbFhirPackage.DefaultTableName),
            evidence.Rows<DbElement>(evidence.Options.Kind is XVerPreparationKind.Comparison or XVerPreparationKind.Outcomes or XVerPreparationKind.Export
                ? DbElement.DefaultTableName : evidence.Options.ElementTableName),
            evidence.Rows<DbStructureDefinitionFacts>(DbStructureDefinitionFacts.DefaultTableName),
            evidence.Rows<DbElementType>(DbElementType.DefaultTableName), [.. evidence.Options.Exclusions], evidence)
    {
    }

    private XVerStructureCatalog(
        IDbConnection? db,
        IEnumerable<DbStructureDefinition> structures,
        IEnumerable<DbFhirPackage> packages,
        IEnumerable<DbElement> elements,
        IEnumerable<DbStructureDefinitionFacts> facts,
        IEnumerable<DbElementType> types,
        HashSet<string> exclusions,
        XVerOperandEvidence? evidence)
    {
        _db = db;
        _evidence = evidence;
        _structures = structures.ToDictionary(row => row.Key);
        _packages = packages.ToDictionary(row => row.Key);
        _elements = elements.ToDictionary(row => row.Key);
        _facts = facts.ToDictionary(row => row.StructureKey);
        _exclusions = exclusions;
        foreach (IGrouping<(int, string, string), DbStructureDefinition> group in _structures.Values
            .GroupBy(row => (row.FhirPackageKey, row.UnversionedUrl, row.Version)))
        {
            if (group.Count() != 1)
            {
                throw new InvalidOperationException($"Duplicate definition identity in package {group.Key.Item1}: {group.Key.Item2}|{group.Key.Item3}.");
            }
        }

        foreach (DbStructureDefinitionFacts row in _facts.Values)
        {
            if (!_structures.TryGetValue(row.StructureKey, out DbStructureDefinition? owner) ||
                owner.FhirPackageKey != row.FhirPackageKey)
            {
                throw new InvalidOperationException($"Definition facts {row.Key} have an invalid owner {row.FhirPackageKey}/{row.StructureKey}.");
            }
        }

        foreach (IGrouping<(int, int, string), DbElement> group in _elements.Values
            .GroupBy(row => (row.FhirPackageKey, row.StructureKey, row.Id)))
        {
            if (group.Count() != 1)
            {
                throw new InvalidOperationException($"Duplicate owned element {group.Key}.");
            }
        }

        _elementsByOwner = _elements.Values.ToLookup(row => row.StructureKey);
        _typesByElement = types.ToLookup(row => row.ElementKey);
        _ownedElements = _elements.Values.ToDictionary(row => (row.FhirPackageKey, row.StructureKey, row.Id));
    }

    internal IReadOnlyList<string> Diagnostics
    {
        get
        {
            RequireActive();
            return _diagnostics;
        }
    }

    internal DbElementMapping? GetElementMapping(int key)
    {
        RequireActive();
        return _evidence is null ? DbElementMapping.SelectSingle(_db!, Key: key) :
            _evidence.Rows<DbElementMapping>(DbElementMapping.DefaultTableName).SingleOrDefault(row => row.Key == key);
    }

    internal bool HasValueSet(int packageKey, string canonical)
    {
        RequireActive();
        UnsafeCodeCanonical identity = UnsafeCodeCanonical.Parse(canonical);
        return identity.Url is not null && identity.Version is not null &&
            (_evidence is null
                ? DbValueSet.SelectList(_db!, FhirPackageKey: packageKey, UnversionedUrl: identity.Url, Version: identity.Version).Count
                : _evidence.Rows<DbValueSet>(DbValueSet.DefaultTableName).Count(row => row.FhirPackageKey == packageKey &&
                    row.UnversionedUrl == identity.Url && row.Version == identity.Version)) == 1;
    }

    internal IReadOnlyList<DbStructureDefinition> GetDefinitions(int fhirPackageKey) =>
        [.. ActiveStructures().Where(row => row.FhirPackageKey == fhirPackageKey).OrderBy(row => row.Key)];

    internal IReadOnlyList<DbStructureDefinition> GetProcessedStructures(int fhirPackageKey) =>
        [.. GetDefinitions(fhirPackageKey).Where(IsProcessed)];

    internal DbStructureDefinition GetStructure(int key)
    {
        RequireActive();
        return _structures.TryGetValue(key, out DbStructureDefinition? owner)
            ? owner : throw new InvalidOperationException($"Definition catalog owner {key} is missing.");
    }

    internal bool IsProcessed(int key) => IsProcessed(GetStructure(key));

    internal bool IsQuantityFamily(DbStructureDefinition owner)
    {
        DbStructureDefinition? quantity = ResolveNativeType(owner.FhirPackageKey, "Quantity", required: false);
        return quantity is not null && (owner.Key == quantity.Key || GetAncestors(owner).Any(ancestor => ancestor.Key == quantity.Key));
    }

    internal IReadOnlySet<int> GetReferencedClosure(int packageKey)
    {
        HashSet<int> owners = [.. GetProcessedStructures(packageKey).Select(owner => owner.Key)];
        Queue<int> pending = new(owners);
        while (pending.TryDequeue(out int key))
        {
            DbStructureDefinition owner = GetStructure(key);
            foreach (DbStructureDefinition ancestor in GetAncestors(owner))
            {
                if (owners.Add(ancestor.Key))
                {
                    pending.Enqueue(ancestor.Key);
                }
            }

            foreach (DbElementType type in GetOwnedElements(owner).SelectMany(GetTypeUses))
            {
                foreach ((string? canonical, int? existingKey) in new[]
                {
                    (type.TypeProfile, type.TypeProfileStructureKey), (type.TargetProfile, type.TargetProfileStructureKey),
                })
                {
                    if (canonical is null)
                    {
                        continue;
                    }

                    DbStructureDefinition? referenced = ResolveDefinition(packageKey, canonical, existingKey, required: false);
                    if (referenced is not null && owners.Add(referenced.Key))
                    {
                        pending.Enqueue(referenced.Key);
                    }
                }
            }
        }

        return owners;
    }

    internal bool IsProcessed(DbStructureDefinition owner)
    {
        RequireActive();
        if (_exclusions.Contains(owner.UnversionedUrl) || owner.Message == "Manually excluded")
        {
            return false;
        }

        if (IsNative(owner))
        {
            return true;
        }

        if (owner.ArtifactClass != FhirArtifactClassEnum.Profile)
        {
            return false;
        }

        DbStructureDefinitionFacts facts = RequireFacts(owner);
        if (!string.Equals(facts.Type, "Quantity", StringComparison.Ordinal))
        {
            return false;
        }

        DbStructureDefinition quantity = ResolveNativeType(owner.FhirPackageKey, "Quantity",
            context: owner.VersionedUrl + " Quantity eligibility")!;
        if (quantity.ArtifactClass != FhirArtifactClassEnum.ComplexType || RequireFacts(quantity).Type != "Quantity")
        {
            throw Error(owner, "Quantity eligibility requires the actual native complex-type Quantity definition");
        }
        if (!GetAncestors(owner).Any(ancestor => ancestor.Key == quantity.Key))
        {
            throw Error(owner, "declared Quantity profile ancestry does not reach the native package Quantity");
        }

        return true;
    }

    internal DbStructureDefinition? ResolveDefinition(
        int fhirPackageKey, string? canonical, int? existingKey = null, bool required = true, string? context = null) =>
        ResolveDefinition(ActiveStructures(), fhirPackageKey, canonical, existingKey, required, context);

    internal static DbStructureDefinition? ResolveDefinition(
        IEnumerable<DbStructureDefinition> structures, int fhirPackageKey, string? canonical,
        int? existingKey = null, bool required = true, string? context = null)
    {
        UnsafeCodeCanonical identity = UnsafeCodeCanonical.Parse(canonical);
        DbStructureDefinition[] matches = string.IsNullOrEmpty(identity.Url) || identity.Version == ""
            ? [] : [.. structures.Where(row => row.FhirPackageKey == fhirPackageKey &&
                string.Equals(row.UnversionedUrl, identity.Url, StringComparison.Ordinal) &&
                (identity.Version is null || string.Equals(row.Version, identity.Version, StringComparison.Ordinal)))];
        if (existingKey is int key && (matches.Length != 1 || matches[0].Key != key))
        {
            throw new InvalidOperationException($"Definition foreign key {key} contradicts package {fhirPackageKey} canonical '{canonical}' at {context}.");
        }

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (!required)
        {
            return null;
        }

        throw new InvalidOperationException($"Definition '{canonical}' in package {fhirPackageKey} is {(matches.Length == 0 ? "missing or malformed" : "ambiguous")} at {context}.");
    }

    internal DbStructureDefinition? ResolveNativeType(
        int fhirPackageKey, string? name, int? existingKey = null, bool required = true, string? context = null) =>
        ResolveNativeType(ActiveStructures(), fhirPackageKey, name, existingKey, required, context);

    internal static DbStructureDefinition? ResolveNativeType(
        IEnumerable<DbStructureDefinition> structures, int fhirPackageKey, string? name,
        int? existingKey = null, bool required = true, string? context = null)
    {
        DbStructureDefinition[] matches = [.. structures.Where(row => row.FhirPackageKey == fhirPackageKey &&
            IsNative(row) && !string.IsNullOrEmpty(name) &&
            (string.Equals(row.Id, name, StringComparison.Ordinal) || string.Equals(row.Name, name, StringComparison.Ordinal)))];
        if (existingKey is int key && (matches.Length != 1 || matches[0].Key != key))
        {
            throw new InvalidOperationException($"Native type foreign key {key} contradicts package {fhirPackageKey} type '{name}' at {context}.");
        }

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (!required)
        {
            return null;
        }

        throw new InvalidOperationException($"Native type '{name}' in package {fhirPackageKey} is {(matches.Length == 0 ? "missing" : "ambiguous")} at {context}.");
    }

    internal DbElement ResolveRoot(DbStructureDefinition owner)
    {
        RequireActive();
        if (_roots.TryGetValue(owner.Key, out DbElement? cached))
        {
            return cached;
        }
        if (RequireFacts(owner).SnapshotAvailability != DefinitionSnapshotAvailability.Available)
        {
            throw Error(owner, "effective root requires an available snapshot");
        }

        DbElement[] roots = [.. _elementsByOwner[owner.Key].Where(row => row.FhirPackageKey == owner.FhirPackageKey &&
            row.StructureKey == owner.Key && row.ResourceFieldOrder == 0)];
        if (roots.Length != 1 || roots[0].ParentElementKey is not null ||
            !Regex.IsMatch(roots[0].Path, "^[A-Za-z][A-Za-z0-9]*$") ||
            !(roots[0].Id == roots[0].Path || roots[0].Id.StartsWith(roots[0].Path + ":", StringComparison.Ordinal)) ||
            roots[0].Id.Contains('.'))
        {
            throw Error(owner, "expected one owner-scoped field-order-zero root with no parent and a valid top-level Path");
        }

        foreach (DbElement element in _elementsByOwner[owner.Key].Where(row => row.Key != roots[0].Key))
        {
            int separator = element.Id.LastIndexOf('.');
            if (separator < 0 || !element.Path.StartsWith(roots[0].Path + ".", StringComparison.Ordinal) ||
                !_ownedElements.TryGetValue((owner.FhirPackageKey, owner.Key, element.Id[..separator]), out DbElement? parent) ||
                element.ParentElementKey != parent.Key)
            {
                throw Error(owner, $"missing or inconsistent exact parent at '{element.Id}'");
            }
        }

        _roots.Add(owner.Key, roots[0]);
        return roots[0];
    }

    internal DbElement? ResolveOwnedElement(
        DbStructureDefinition owner, string elementId, int? existingKey = null, bool required = true)
    {
        RequireActive();
        _ownedElements.TryGetValue((owner.FhirPackageKey, owner.Key, elementId), out DbElement? match);
        if (existingKey is int key && match?.Key != key)
        {
            throw Error(owner, $"element foreign key {key} contradicts exact owned Id '{elementId}'");
        }

        if (match is not null)
        {
            return match;
        }

        if (!required)
        {
            return null;
        }

        throw Error(owner, $"element '{elementId}' is missing or ambiguous");
    }

    internal IReadOnlyList<DbStructureDefinition> GetAncestors(DbStructureDefinition owner)
    {
        RequireActive();
        if (_ancestors.TryGetValue(owner.Key, out IReadOnlyList<DbStructureDefinition>? cached))
        {
            return cached;
        }

        List<DbStructureDefinition> ancestors = [];
        HashSet<int> visited = [owner.Key];
        DbStructureDefinition current = owner;
        while (!string.IsNullOrEmpty(RequireFacts(current).BaseDefinition))
        {
            string canonical = RequireFacts(current).BaseDefinition!;
            current = ResolveDefinition(owner.FhirPackageKey, canonical, context: owner.VersionedUrl + " BaseDefinition")!;
            if (!visited.Add(current.Key))
            {
                throw Error(owner, $"cyclic BaseDefinition ancestry at '{canonical}'");
            }

            ancestors.Add(current);
        }

        _ancestors.Add(owner.Key, ancestors);
        return ancestors;
    }

    internal DbElement? ResolveBaseElement(DbElement element, bool required = true)
    {
        DbStructureDefinition owner = RequireOwner(element);
        string inheritedPath = element.BasePath ?? element.Path;
        string relativeId = element.Id.Contains('.') ? element.Id[element.Id.IndexOf('.')..] : "";
        foreach (DbStructureDefinition ancestor in GetAncestors(owner))
        {
            DbElement? inherited = FindInheritedElement(owner, ancestor, inheritedPath, relativeId, element.Id);
            if (inherited is not null)
            {
                return ValidateBase(element, inherited, owner);
            }
        }

        if (element.BasePath is not null && element.BasePath != element.Path && element.Id.Contains('.'))
        {
            string parentId = element.Id[..element.Id.LastIndexOf('.')];
            DbElement parent = ResolveOwnedElement(owner, parentId, element.ParentElementKey)!;
            if (element.ParentElementKey != parent.Key || !element.Path.StartsWith(parent.Path + ".", StringComparison.Ordinal))
            {
                throw Error(owner, $"missing or inconsistent exact parent at '{element.Id}'");
            }

            string relativePath = element.Path[parent.Path.Length..];
            relativeId = element.Id[parent.Id.Length..];
            Dictionary<int, DbElement> inheritedElements = [];
            foreach (DbElementType type in _typesByElement[parent.Key])
            {
                ValidateTypeKeys(type);
                DbStructureDefinition datatype = ResolveTypeUse(type);
                foreach (DbStructureDefinition context in new[] { datatype }.Concat(GetAncestors(datatype)))
                {
                    DbElement root = ResolveRoot(context);
                    if (inheritedPath != root.Path + relativePath)
                    {
                        continue;
                    }

                    DbElement? inherited = FindInheritedElement(owner, context, inheritedPath, relativeId, element.Id);
                    if (inherited is not null)
                    {
                        inheritedElements.TryAdd(inherited.Key, inherited);
                        break;
                    }
                }
            }

            if (inheritedElements.Count > 1)
            {
                throw Error(owner, $"ambiguous parent datatype BasePath '{inheritedPath}' at '{element.Id}'");
            }
            if (inheritedElements.Count == 1)
            {
                return ValidateBase(element, inheritedElements.Values.Single(), owner);
            }
        }

        if (element.BaseElementKey is not null || element.BaseStructureKey is not null ||
            (required && element.BasePath is not null && element.BasePath != element.Path))
        {
            throw Error(owner, $"missing inherited BasePath '{inheritedPath}' at '{element.Id}'");
        }

        return null;
    }

    private DbElement? FindInheritedElement(
        DbStructureDefinition owner, DbStructureDefinition ancestor, string inheritedPath, string relativeId, string elementId)
    {
        if (RequireFacts(ancestor).SnapshotAvailability != DefinitionSnapshotAvailability.Available)
        {
            throw Error(owner, $"base correspondence at '{elementId}' requires the effective snapshot of '{ancestor.VersionedUrl}'");
        }

        DbElement[] matches = [.. _elementsByOwner[ancestor.Key].Where(row => row.FhirPackageKey == owner.FhirPackageKey &&
            row.StructureKey == ancestor.Key && row.Path == inheritedPath)];
        // Exact relative slice identity takes precedence over the unsliced inherited path.
        DbElement[] exact = [.. matches.Where(row => (row.Id.Contains('.') ? row.Id[row.Id.IndexOf('.')..] : "") == relativeId)];
        if (exact.Length == 1)
        {
            return exact[0];
        }

        DbElement[] unsliced = [.. matches.Where(row => row.SliceName is null &&
            !(row.Id.Contains('.') && row.Id[row.Id.IndexOf('.')..].Contains(':')))];
        if (exact.Length > 1 || unsliced.Length > 1)
        {
            throw Error(owner, $"ambiguous inherited BasePath '{inheritedPath}' at '{elementId}'");
        }

        return unsliced.SingleOrDefault();
    }

    internal void ResolveImportLinks(IDbConnection db, bool validateProcessedRoots)
    {
        RequireActive();
        if (_evidence is not null)
        {
            throw new InvalidOperationException("A captured definition catalog cannot write import links.");
        }
        HashSet<int> processed = [];
        foreach (DbStructureDefinition owner in _structures.Values)
        {
            if (validateProcessedRoots ? IsProcessed(owner) :
                IsNative(owner) && !_exclusions.Contains(owner.UnversionedUrl) && owner.Message != "Manually excluded")
            {
                processed.Add(owner.Key);
                if (validateProcessedRoots)
                {
                    ResolveRoot(owner);
                }
            }
        }

        foreach (DbElement element in _elements.Values)
        {
            DbStructureDefinition owner = RequireOwner(element);
            try
            {
                DbElement? inherited = ResolveBaseElement(element, required: processed.Contains(owner.Key));
                element.BaseElementKey = inherited?.Key;
                element.BaseStructureKey = inherited?.StructureKey;
                element.IsInherited = inherited is not null;
            }
            catch (InvalidOperationException exception) when (!processed.Contains(owner.Key) &&
                element.BaseElementKey is null && element.BaseStructureKey is null)
            {
                _diagnostics.Add(exception.Message);
            }

            if (!element.DefinedAsContentReference)
            {
                continue;
            }

            try
            {
                DbElement source = ResolveContentReference(element, []);
                element.ContentReferenceSourceKey = source.Key;
                element.ContentReferenceSourceId = source.Id;
                source.UsedAsContentReference = true;
                element.FullCollatedTypeLiteral = source.Id;
            }
            catch (InvalidOperationException exception) when (!processed.Contains(owner.Key) &&
                element.ContentReferenceSourceKey is null)
            {
                _diagnostics.Add(exception.Message);
            }
        }

        DbElement.Update(db, _elements.Values);
        foreach (DbElementType type in DbElementType.SelectList(db))
        {
            ValidateTypeKeys(type);
            try
            {
                ResolveNativeType(type.FhirPackageKey, type.TypeName, type.TypeStructureKey,
                    context: $"element type {type.Key} owner {type.StructureKey}");
                if (type.TypeProfile is not null || type.TypeProfileStructureKey is not null)
                {
                    ResolveTypeUse(type);
                }

                if (type.TargetProfile is not null || type.TargetProfileStructureKey is not null)
                {
                    ResolveTypeUse(type, targetProfile: true);
                }
            }
            catch (InvalidOperationException exception)
            {
                _diagnostics.Add(exception.Message);
            }
        }
    }

    internal void ValidateStoredLinks(IEnumerable<DbElementType> types)
    {
        RequireActive();
        foreach (DbElement element in _elements.Values)
        {
            if (element.BaseElementKey is not null || element.BaseStructureKey is not null)
            {
                ResolveBaseElement(element);
            }

            if (element.ContentReferenceSourceKey is not null)
            {
                DbElement source = ResolveContentReference(element, []);
                if (element.ContentReferenceSourceId != source.Id)
                {
                    throw Error(RequireOwner(element), $"contentReference source Id contradicts its key at '{element.Id}'");
                }
            }
        }

        foreach (DbElementType type in types)
        {
            ValidateTypeKeys(type);
        }
    }

    internal DbStructureDefinition ResolveTypeUse(DbElementType type, bool targetProfile = false)
    {
        string context = TypeUseContext(type);
        string? canonical = targetProfile ? type.TargetProfile : type.TypeProfile;
        int? key = targetProfile ? type.TargetProfileStructureKey : type.TypeProfileStructureKey;
        if (canonical is not null || key is not null)
        {
            return ResolveDefinition(type.FhirPackageKey, canonical, key, context: context)!;
        }

        return ResolveNativeType(type.FhirPackageKey, type.TypeName, type.TypeStructureKey,
            context: context)!;
    }

    internal DbElement ResolveContentReference(DbElement element)
    {
        DbElement source = ResolveContentReference(element, []);
        while (source.DefinedAsContentReference)
        {
            source = ResolveContentReference(source, []);
        }
        return source;
    }

    private DbElement ResolveContentReference(DbElement element, HashSet<int> visited)
    {
        DbStructureDefinition owner = RequireOwner(element);
        if (!visited.Add(element.Key))
        {
            throw Error(owner, $"cyclic contentReference at '{element.Id}'");
        }

        StructureDefinition definition = ReadPayload(owner);
        IEnumerable<ElementDefinition> projection = RequireFacts(owner).SnapshotAvailability == DefinitionSnapshotAvailability.Available
            ? definition.Snapshot.Element : definition.Differential?.Element ?? [];
        string? reference = projection.SingleOrDefault(row => row.ElementId == element.Id)?.ContentReference;
        int separator = reference?.IndexOf('#') ?? -1;
        if (separator < 0 || separator == reference!.Length - 1)
        {
            throw Error(owner, $"missing or malformed contentReference '{reference}' at '{element.Id}'");
        }

        DbStructureDefinition referencedOwner = separator == 0 ? owner :
            ResolveDefinition(owner.FhirPackageKey, reference[..separator], context: $"{owner.VersionedUrl}#{element.Id}")!;
        ResolveRoot(referencedOwner);
        DbElement source = ResolveOwnedElement(referencedOwner, reference[(separator + 1)..],
            element.ContentReferenceSourceKey)!;
        if (source.DefinedAsContentReference)
        {
            ResolveContentReference(source, visited);
        }

        return source;
    }

    internal StructureDefinition ReadPayload(DbStructureDefinition owner)
    {
        RequireActive();
        if (!_structures.TryGetValue(owner.Key, out DbStructureDefinition? recorded) ||
            recorded.FhirPackageKey != owner.FhirPackageKey || recorded.UnversionedUrl != owner.UnversionedUrl ||
            recorded.Version != owner.Version)
        {
            throw Error(owner, "payload request contradicts the catalog owner identity");
        }
        if (!_payloads.TryGetValue(owner.Key, out StructureDefinition? definition))
        {
            DbStructureDefinitionFacts facts = RequireFacts(owner);
            definition = StructureDefinitionFactsStore.ReadDefinition(facts);
            if (definition.Url != owner.UnversionedUrl || (definition.Version ?? "") != owner.Version ||
                definition.Type != facts.Type || definition.BaseDefinition != facts.BaseDefinition ||
                definition.Kind?.GetLiteral() != facts.Kind || definition.Derivation?.GetLiteral() != facts.Derivation)
            {
                throw Error(owner, "normalized payload contradicts canonical/version or declared type/kind/derivation/base facts");
            }
            _payloads.Add(owner.Key, definition);
        }

        return definition;
    }

    internal DbStructureDefinition GetOwner(DbElement element) => RequireOwner(element);

    internal string? GetDeclaredType(DbStructureDefinition owner) => RequireFacts(owner).Type;

    internal void ReleasePayloads()
    {
        _payloadElements.Clear();
        _payloads.Clear();
    }

    internal void SelectCapturedElementView()
    {
        RequireActive();
        if (_evidence is null || _evidence.Options.ElementTableName == DbElement.DefaultTableName)
        {
            return;
        }
        List<DbElement> elements = _evidence.Rows<DbElement>(_evidence.Options.ElementTableName);
        foreach (IGrouping<(int, int, string), DbElement> group in elements.GroupBy(row => (row.FhirPackageKey, row.StructureKey, row.Id)))
        {
            if (group.Count() != 1)
            {
                throw new InvalidOperationException($"Duplicate owned element {group.Key}.");
            }
        }
        _elements.Clear();
        foreach (DbElement element in elements)
        {
            _elements.Add(element.Key, element);
        }
        _elementsByOwner = _elements.Values.ToLookup(row => row.StructureKey);
        _ownedElements = _elements.Values.ToDictionary(row => (row.FhirPackageKey, row.StructureKey, row.Id));
        _roots.Clear();
        ReleasePayloads();
    }

    private IEnumerable<DbStructureDefinition> ActiveStructures()
    {
        RequireActive();
        return _structures.Values;
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _evidence?.RequireActive();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReleasePayloads();
        _roots.Clear();
        _ancestors.Clear();
        _structures.Clear();
        _packages.Clear();
        _elements.Clear();
        _facts.Clear();
        _ownedElements.Clear();
        _diagnostics.Clear();
        DbElement[] noElements = [];
        DbElementType[] noTypes = [];
        _elementsByOwner = noElements.ToLookup(row => row.StructureKey);
        _typesByElement = noTypes.ToLookup(row => row.ElementKey);
    }

    internal DbFhirPackage GetPackage(int key)
    {
        RequireActive();
        return _packages.TryGetValue(key, out DbFhirPackage? package)
            ? package : throw new InvalidOperationException($"Definition catalog package {key} is missing.");
    }

    internal IReadOnlyList<DbElement> GetOwnedElements(DbStructureDefinition owner)
    {
        RequireActive();
        return [.. _elementsByOwner[owner.Key].Where(row => row.FhirPackageKey == owner.FhirPackageKey)
            .OrderBy(row => row.ResourceFieldOrder)];
    }

    internal IReadOnlyList<DbElement> GetOwnedChildren(DbElement parent) =>
        [.. GetOwnedElements(RequireOwner(parent)).Where(row => row.ParentElementKey == parent.Key)];

    internal IReadOnlyList<DbElementType> GetTypeUses(DbElement element)
    {
        RequireOwner(element);
        DbElementType[] types = [.. _typesByElement[element.Key]];
        foreach (DbElementType type in types)
        {
            ValidateTypeKeys(type);
        }
        return types;
    }

    internal ElementDefinition ReadElement(DbElement element)
    {
        DbStructureDefinition owner = RequireOwner(element);
        ResolveOwnedElement(owner, element.Id, element.Key);
        ResolveRoot(owner);
        if (!_payloadElements.TryGetValue(owner.Key, out Dictionary<string, ElementDefinition>? elements))
        {
            elements = [];
            foreach (ElementDefinition row in ReadPayload(owner).Snapshot.Element)
            {
                if (row.ElementId is null || !elements.TryAdd(row.ElementId, row))
                {
                    throw Error(owner, "normalized snapshot has missing/duplicate exact element identities");
                }
            }
            IReadOnlyList<DbElement> owned = GetOwnedElements(owner);
            if (elements.Count != owned.Count || owned.Any(row =>
                !elements.TryGetValue(row.Id, out ElementDefinition? payload) || payload.Path != row.Path))
            {
                throw Error(owner, "normalized snapshot and owned relational projection are incomplete or inconsistent");
            }
            _payloadElements.Add(owner.Key, elements);
        }
        if (!elements.TryGetValue(element.Id, out ElementDefinition? match) || match.Path != element.Path)
        {
            throw Error(owner, $"normalized snapshot contradicts the exact owned element '{element.Id}'");
        }
        return match;
    }

    private void ValidateTypeKeys(DbElementType type)
    {
        string context = TypeUseContext(type);
        if (type.TypeStructureKey is not null)
        {
            ResolveNativeType(type.FhirPackageKey, type.TypeName, type.TypeStructureKey, context: context);
        }

        if (type.TypeProfileStructureKey is not null)
        {
            ResolveDefinition(type.FhirPackageKey, type.TypeProfile, type.TypeProfileStructureKey, context: context);
        }

        if (type.TargetProfileStructureKey is not null)
        {
            ResolveDefinition(type.FhirPackageKey, type.TargetProfile, type.TargetProfileStructureKey, context: context);
        }
    }

    private string TypeUseContext(DbElementType type)
    {
        if (!_elements.TryGetValue(type.ElementKey, out DbElement? element) ||
            element.StructureKey != type.StructureKey || element.FhirPackageKey != type.FhirPackageKey)
        {
            throw new InvalidOperationException($"Element type {type.Key} has invalid owner {type.FhirPackageKey}/{type.StructureKey}, element {type.ElementKey}.");
        }

        return $"{RequireOwner(element).VersionedUrl}#{element.Id} (element type {type.Key})";
    }

    private DbStructureDefinitionFacts RequireFacts(DbStructureDefinition owner)
    {
        RequireActive();
        return _facts.TryGetValue(owner.Key, out DbStructureDefinitionFacts? facts) && facts.FhirPackageKey == owner.FhirPackageKey
            ? facts : throw Error(owner, "missing normalized definition facts");
    }

    private DbStructureDefinition RequireOwner(DbElement element)
    {
        RequireActive();
        return _structures.TryGetValue(element.StructureKey, out DbStructureDefinition? owner) && owner.FhirPackageKey == element.FhirPackageKey
            ? owner : throw new InvalidOperationException($"Element {element.Key} '{element.Id}' has invalid owner {element.FhirPackageKey}/{element.StructureKey}.");
    }

    private static DbElement ValidateBase(DbElement element, DbElement inherited, DbStructureDefinition owner)
    {
        if ((element.BaseElementKey is int key && key != inherited.Key) ||
            (element.BaseStructureKey is int structureKey && structureKey != inherited.StructureKey))
        {
            throw Error(owner, $"base foreign keys contradict BaseDefinition ancestry at '{element.Id}'");
        }

        return inherited;
    }

    private static bool IsNative(DbStructureDefinition row) => row.ArtifactClass is
        FhirArtifactClassEnum.PrimitiveType or FhirArtifactClassEnum.ComplexType or FhirArtifactClassEnum.Resource;

    private static InvalidOperationException Error(DbStructureDefinition owner, string fact) =>
        new($"Definition catalog package {owner.FhirPackageKey}, owner {owner.Key} '{owner.UnversionedUrl}|{owner.Version}': {fact}.");
}
