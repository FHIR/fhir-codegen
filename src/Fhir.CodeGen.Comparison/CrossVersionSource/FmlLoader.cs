using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.MappingLanguage;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Comparison.CompareTool.CrossVersionMapCollection;

namespace Fhir.CodeGen.Comparison.CrossVersionSource;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// TODO: This is mostly pulled from CrossVersionMapCollection and needs to be refactored.
/// </remarks>
public class FmlLoader
{
    private MappingLoader _mappingLoader;
    private IDbConnection _db;
    private List<DbFhirPackage> _packages;
    private DbFhirPackage? _sourcePackage = null;
    private DbFhirPackage? _targetPackage = null;
    private string? _name = null;
    private FhirStructureMap? _fml = null;
    private string? _fmlFilename = null;
    private string? _fmlUrl;
    private int _sourceFileKey = -1;
    private string _sourceFilename = string.Empty;

    private ILoggerFactory? _loggerFactory;
    private ILogger _logger;

    private Dictionary<string, HashSet<string>> _relatedPaths;
    private HashSet<(string src, string tgt)> _simpleTargetPaths;

    public FmlLoader(
        MappingLoader mappingLoader,
        IDbConnection db,
        List<DbFhirPackage> packages,
        ILoggerFactory? loggerFactory = null)
    {
        _mappingLoader = mappingLoader;
        _db = db;
        _packages = packages;

        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<FmlLoader>()
            ?? LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<FmlLoader>();

        _relatedPaths = [];
        _simpleTargetPaths = [];
    }

    private record class FmlSymbolResolutionRecord
    {
        public required string Identifier { get; init; }
        public required string? Alias { get; init; }
        public required string? TypeIdentifier { get; init; }
        public required string ResolvedPath { get; init; }
        public required DbStructureDefinition Structure { get; init; }
        public required DbElement? Element { get; init; }
    }


    public void LoadSourceFml(string basePath)
    {
        string inputPath = Path.Combine(basePath, "input");
        if (!Directory.Exists(inputPath))
        {
            _logger.LogWarning($"Path not found: {inputPath}");
            return;
        }

        // iterate over the source packages
        for (int sourceIndex = 0; sourceIndex < _packages.Count - 1; sourceIndex++)
        {
            DbFhirPackage sourcePackage = _packages[sourceIndex];

            // iterate over the target packages
            for (int targetIndex = sourceIndex + 1; targetIndex < _packages.Count; targetIndex++)
            {
                // skip same package
                if (sourceIndex == targetIndex)
                {
                    continue;
                }

                DbFhirPackage targetPackage = _packages[targetIndex];

                string relativePath = $"{sourcePackage.ShortName}to{targetPackage.ShortName}";
                string path = Path.Combine(inputPath, relativePath);
                if (Directory.Exists(path))
                {
                    loadSourceFml(sourcePackage, targetPackage, path, inputPath);
                }

                // check the reverse direction
                relativePath = $"{targetPackage.ShortName}to{sourcePackage.ShortName}";
                path = Path.Combine(inputPath, relativePath);
                if (Directory.Exists(path))
                {
                    loadSourceFml(targetPackage, sourcePackage, path, inputPath);
                }

            }
        }
    }


    private void loadSourceFml(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        string path,
        string inputPath)
    {
        _logger.LogInformation($"Loading FML maps from: {path}");

        _sourcePackage = sourcePackage;
        _targetPackage = targetPackage;

        string sourceToTargetNoR = $"{sourcePackage.ShortName[1..]}to{targetPackage.ShortName[1..]}";
        int sourceToTargetNoRLen = sourceToTargetNoR.Length;

        FhirMappingLanguage fmlParser = new();

        // files have different naming conventions in each directory, so just process anything FML
        string[] files = Directory.GetFiles(path, $"*.fml", SearchOption.TopDirectoryOnly);
        foreach (string filename in files)
        {
            try
            {
                string fmlContent = File.ReadAllText(filename);

                if (!fmlParser.TryParse(fmlContent, out FhirStructureMap? fml))
                {
                    _logger.LogError($"Error loading {filename}: could not parse");
                    continue;
                }

                // extract the name root
                string name;

                if (fml.MetadataByPath.TryGetValue("name", out MetadataDeclaration? nameMeta))
                {
                    name = nameMeta.Literal?.ValueAsString ?? throw new Exception($"Cross-version structure maps require a metadata name property: {filename}");
                }
                else
                {
                    name = Path.GetFileNameWithoutExtension(filename);
                }

                if (name.EndsWith(sourceToTargetNoR, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^sourceToTargetNoRLen];
                }

                if (name.Equals("primitives", StringComparison.OrdinalIgnoreCase))
                {
                    // skip primitive type map - we have that information internally already, see FhirTypeMappings.cs
                    continue;
                }

                // process this file
                processFml(
                    filename,
                    name,
                    fml,
                    inputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading {filename}: {ex.Message}");
            }
        }
    }

    private void processFml(
        string filename,
        string name,
        FhirStructureMap fml,
        string inputPath)
    {
        if ((_sourcePackage is null) || (_targetPackage is null))
        {
            throw new Exception("Source and target packages must be set before processing FML");
        }

        _name = name;
        _fml = fml;
        _fmlFilename = filename;

        _fmlUrl = fml.MetadataByPath.ContainsKey("url")
            ? fml.MetadataByPath["url"]?.Literal?.ValueAsString
            : null;

        _fmlUrl ??= $"http://hl7.org/fhir/uv/xver/StructureMap/{Path.GetFileNameWithoutExtension(filename)}";

        _relatedPaths.Clear();
        _simpleTargetPaths.Clear();

        _sourceFileKey = _mappingLoader.getOrCreateMappingSourceFileKey(
            filename,
            MappingLoader.SourceFileTypeCodes.FML,
            _fmlUrl);

        _sourceFilename = Path.GetRelativePath(inputPath, filename);

        Dictionary<string, FmlSymbolResolutionRecord> structures = [];

        // process the initial structure declarations to get the aliases
        foreach ((string url, StructureDeclaration fmlStructure) in _fml.StructuresByUrl)
        {
            XVerStructureCatalog catalog = new(_db, XVerProcessor._exclusionSet);
            DbFhirPackage declarationPackage = fmlStructure.ModelMode == StructureMap.StructureMapModelMode.Source
                ? _sourcePackage : _targetPackage;
            DbStructureDefinition declaredOwner = ResolveDeclaration(catalog, declarationPackage, url, filename);
            if (!catalog.IsProcessed(declaredOwner))
            {
                return;
            }
            DbElement declaredRoot = catalog.ResolveRoot(declaredOwner);
            string fhirStructureType = declaredOwner.Id;

            // ensure the type exists in the correct package
            if (fmlStructure.ModelMode == StructureMap.StructureMapModelMode.Source)
            {
                DbStructureDefinition? sd = declaredOwner;

                if (sd is null)
                {
                    throw new Exception($"Failed to resolve FML source structure: {fhirStructureType} in {_name} ({_fmlFilename})");
                }

                DbElement? ed = declaredRoot;

                if (fmlStructure.Alias is not null)
                {
                    structures[fmlStructure.Alias] = new()
                    {
                        Identifier = fmlStructure.Alias,
                        Alias = fmlStructure.ModelModeLiteral,
                        TypeIdentifier = fhirStructureType,
                        ResolvedPath = ed.Id,
                        Structure = sd,
                        Element = ed,
                    };
                }

                structures[fmlStructure.ModelModeLiteral] = new()
                {
                    Identifier = fmlStructure.ModelModeLiteral,
                    Alias = fmlStructure.Alias,
                    TypeIdentifier = fhirStructureType,
                    ResolvedPath = ed.Id,
                    Structure = sd,
                    Element = ed,
                };
            }

            if (fmlStructure.ModelMode == StructureMap.StructureMapModelMode.Target)
            {
                DbStructureDefinition? sd = declaredOwner;

                if (sd is null)
                {
                    throw new Exception($"Failed to resolve FML target structure: {fhirStructureType} in {_name} ({_fmlFilename})");
                }

                DbElement? ed = declaredRoot;

                if (fmlStructure.Alias is not null)
                {
                    structures[fmlStructure.Alias] = new()
                    {
                        Identifier = fmlStructure.Alias,
                        Alias = fmlStructure.ModelModeLiteral,
                        TypeIdentifier = fhirStructureType,
                        ResolvedPath = ed.Id,
                        Structure = sd,
                        Element = ed,
                    };
                }

                structures[fmlStructure.ModelModeLiteral] = new()
                {
                    Identifier = fmlStructure.ModelModeLiteral,
                    Alias = fmlStructure.Alias,
                    TypeIdentifier = fhirStructureType,
                    ResolvedPath = ed.Id,
                    Structure = sd,
                    Element = ed,
                };
            }
        }

        FmlSymbolResolutionRecord? rootSource = null;
        FmlSymbolResolutionRecord? rootTarget = null;

        // process each of the groups in the FML to extract prefixPath maps
        foreach ((string groupName, GroupDeclaration group) in _fml.GroupsByName)
        {
            Dictionary<string, FmlSymbolResolutionRecord> sourceSymbols = [];
            Dictionary<string, FmlSymbolResolutionRecord> targetSymbols = [];
            List<FmlSymbolResolutionRecord> parameters = [];

            // check for groups that process structures directly (no other variables added yet)
            foreach (GroupParameter gp in group.Parameters)
            {
                // we only care about root-level groups here, which require type identifiers
                if (gp.TypeIdentifier is null)
                {
                    continue;
                }

                // at top level, all arugments need to be from structures
                switch (gp.InputMode)
                {
                    case StructureMap.StructureMapInputMode.Source:
                        {
                            if (resolveFmlSymbol(gp.Identifier, null, gp.TypeIdentifier, structures, gp.InputMode.Value)
                                is FmlSymbolResolutionRecord r)
                            {
                                r = r with
                                {
                                    Identifier = gp.Identifier,
                                };

                                sourceSymbols[gp.Identifier] = r;
                                rootSource = r;
                                //parameters.Add(r);
                            }
                        }
                        break;

                    case StructureMap.StructureMapInputMode.Target:
                        {
                            if (resolveFmlSymbol(gp.Identifier, null, gp.TypeIdentifier, structures, gp.InputMode.Value)
                                is FmlSymbolResolutionRecord r)
                            {
                                r = r with
                                {
                                    Identifier = gp.Identifier,
                                };

                                targetSymbols[gp.Identifier] = r;
                                rootTarget = r;
                                //parameters.Add(r);
                            }
                        }
                        break;
                }
            }

            if ((sourceSymbols.Count == 0) ||
                (targetSymbols.Count == 0))
            {
                continue;
            }

            processFmlGroup(
                group,
                sourceSymbols,
                targetSymbols,
                [],
                rootSource,
                rootTarget);
        }

        if ((rootSource is null) || (rootTarget is null))
        {
            throw new Exception($"No processable groups found in {_name} ({_fmlFilename})");
        }

        reconcileElementMapFmlPathsInDb(
            rootSource,
            rootTarget);
    }

    private FmlSymbolResolutionRecord? resolveFmlSymbol(
        string literal,
        string? alias,
        string? typeIdentifier,
        Dictionary<string, FmlSymbolResolutionRecord> symbolTable,
        StructureMap.StructureMapInputMode mode)
    {
        int packageKey = mode == StructureMap.StructureMapInputMode.Source
            ? _sourcePackage!.Key
            : _targetPackage!.Key;

        // check for a type identifier
        if (typeIdentifier is not null)
        {
            if (symbolTable.TryGetValue(typeIdentifier, out FmlSymbolResolutionRecord? typeRec))
            {
                return typeRec;
            }

            // try to resolve the type from the database
            XVerStructureCatalog catalog = new(_db, XVerProcessor._exclusionSet);
            DbStructureDefinition? sd = catalog.ResolveNativeType(packageKey, typeIdentifier, required: false);
            if (sd is not null)
            {
                DbElement ed = catalog.ResolveRoot(sd);

                return new()
                {
                    Identifier = literal,
                    Alias = alias,
                    TypeIdentifier = typeIdentifier,
                    ResolvedPath = ed.Id,
                    Structure = sd,
                    Element = ed,
                };
            }
        }

        // check for an alias
        if ((alias is not null) &&
            symbolTable.TryGetValue(alias, out FmlSymbolResolutionRecord? aliasRec))
        {
            return aliasRec;
        }

        string[] components = literal.Split('.');

        if (symbolTable.TryGetValue(components[0], out FmlSymbolResolutionRecord? prefixRec))
        {
            string path = string.Join('.', [prefixRec.ResolvedPath, .. components[1..]]);

            // try to resolve from the database
            DbElement? ed = DbElement.SelectSingle(
                _db,
                FhirPackageKey: packageKey,
                StructureKey: prefixRec.Structure.Key,
                Id: path);

            // if not found, check with the choice type literal appended
            ed ??= DbElement.SelectSingle(
                _db,
                FhirPackageKey: packageKey,
                StructureKey: prefixRec.Structure.Key,
                Id: path + "[x]");

            if (ed is not null)
            {
                DbStructureDefinition? sd = DbStructureDefinition.SelectSingle(
                    _db,
                    FhirPackageKey: packageKey,
                    Key: ed.StructureKey);
                if (sd is not null)
                {
                    return new()
                    {
                        Identifier = literal,
                        Alias = alias,
                        TypeIdentifier = typeIdentifier,
                        ResolvedPath = ed.Id,
                        Structure = sd,
                        Element = ed,
                    };
                }
            }
        }

        // iterate up to see if we can substitute something in the chain
        if (prefixRec is not null)
        {
            string[] testComponents = [..prefixRec.ResolvedPath.Split('.'), ..components[1..]];

            for (int splitIndex = testComponents.Length - 1; splitIndex > 0; splitIndex--)
            {
                string prefixPath = string.Join('.', testComponents[0..splitIndex]);

                // try to resolve from the database
                DbElement? prefixEd = DbElement.SelectSingle(
                    _db,
                    FhirPackageKey: packageKey,
                    StructureKey: prefixRec.Structure.Key,
                    Id: prefixPath);

                // if not found, check with the choice type literal appended
                prefixEd ??= DbElement.SelectSingle(
                    _db,
                    FhirPackageKey: packageKey,
                    StructureKey: prefixRec.Structure.Key,
                    Id: prefixPath + "[x]");

                if (prefixEd is null)
                {
                    continue;
                }

                // get the list of types for this element
                List<DbElementType> elementTypes = DbElementType.SelectList(
                    _db,
                    ElementKey: prefixEd.Key);

                foreach (DbElementType et in elementTypes)
                {
                    XVerStructureCatalog catalog = new(_db);
                    DbStructureDefinition? prefixTypeSd = catalog.ResolveTypeUse(et);
                    if (prefixTypeSd is null)
                    {
                        continue;
                    }

                    // check to see if we can resolve the rest of the element from this root
                    string postfixPath = string.Join('.', [catalog.ResolveRoot(prefixTypeSd).Id, ..testComponents[splitIndex..]]);

                    // try to resolve from the database
                    DbElement? postfixEd = DbElement.SelectSingle(
                        _db,
                        FhirPackageKey: packageKey,
                        StructureKey: prefixTypeSd.Key,
                        Id: postfixPath);

                    // if not found, check with the choice type literal appended
                    postfixEd ??= DbElement.SelectSingle(
                        _db,
                        FhirPackageKey: packageKey,
                        StructureKey: prefixTypeSd.Key,
                        Id: postfixPath + "[x]");

                    if (postfixEd is null)
                    {
                        continue;
                    }

                    // since we know it is valid, we can return something - but we do not want to nest into types since that destroys the maps
                    return new()
                    {
                        Identifier = literal,
                        Alias = alias,
                        TypeIdentifier = typeIdentifier,
                        ResolvedPath = string.Join('.', [prefixRec.ResolvedPath, .. components[1..]]),
                        Structure = prefixRec.Structure,
                        Element = null,
                    };
                }
            }
        }


        // likely nested into a datatype, can ignore for now
        return null;
    }

    private void processFmlGroup(
        GroupDeclaration group,
        Dictionary<string, FmlSymbolResolutionRecord> sourceSymbols,
        Dictionary<string, FmlSymbolResolutionRecord> targetSymbols,
        List<FmlSymbolResolutionRecord> parameters,
        FmlSymbolResolutionRecord? rootSource,
        FmlSymbolResolutionRecord? rootTarget,
        HashSet<string>? groupCallStack = null)
    {
        groupCallStack ??= [];
        if (!groupCallStack.Add(group.Name))
        {
            return;
        }

        // explicit parameters always override discovery
        if (parameters.Count > 0)
        {
            sourceSymbols.Clear();
            targetSymbols.Clear();

            // match parameters to the correct dictionary and symbol
            if (parameters.Count != group.Parameters.Count)
            {
                // if we do not have the correct number of parameters, we nested into a type - ignore for now
                // unwind the stack
                groupCallStack.Remove(group.Name);
                return;
            }

            for (int i = 0; i < parameters.Count; i++)
            {
                GroupParameter gp = group.Parameters[i];
                FmlSymbolResolutionRecord p = parameters[i];

                switch (gp.InputMode)
                {
                    case StructureMap.StructureMapInputMode.Source:
                        {
                            sourceSymbols[gp.Identifier] = p with
                            {
                                Identifier = gp.Identifier,
                                Alias = null,
                                TypeIdentifier = gp.TypeIdentifier,
                            };
                        }
                        break;

                    case StructureMap.StructureMapInputMode.Target:
                        {
                            targetSymbols[gp.Identifier] = p with
                            {
                                Identifier = gp.Identifier,
                                Alias = null,
                                TypeIdentifier = gp.TypeIdentifier,
                            };
                        }
                        break;
                }
            }
        }

        // travers each expression in the group
        foreach (GroupExpression expression in group.Expressions)
        {
            if (expression.SimpleCopyExpression is not null)
            {
                try
                {
                    processSimpleCopyExpression(
                        group,
                        expression.SimpleCopyExpression,
                        sourceSymbols,
                        targetSymbols);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($" <<< processSimpleCopyExpression for {_fmlFilename} Line: {expression.Line} caught: {ex.Message}");
                }
                continue;
            }

            if (expression.MappingExpression is not null)
            {
                try
                {
                    processComplexMappingExpression(
                        group,
                        expression.MappingExpression,
                        sourceSymbols,
                        targetSymbols,
                        groupCallStack,
                        rootSource,
                        rootTarget);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($" <<< processComplexMappingExpression for {_fmlFilename} Line: {expression.Line} caught: {ex.Message}");
                }

                continue;
            }
        }

        // unwind the stack
        groupCallStack.Remove(group.Name);
    }

    private void processComplexMappingExpression(
        GroupDeclaration group,
        FmlGroupExpression expression,
        Dictionary<string, FmlSymbolResolutionRecord> sourceSymbols,
        Dictionary<string, FmlSymbolResolutionRecord> targetSymbols,
        HashSet<string> groupCallStack,
        FmlSymbolResolutionRecord? rootSource,
        FmlSymbolResolutionRecord? rootTarget)
    {
        Dictionary<string, FmlSymbolResolutionRecord> localSourceSymbols = [];
        Dictionary<string, FmlSymbolResolutionRecord> localTargetSymbols = [];

        FmlSymbolResolutionRecord? lastResolvedSource = null;
        FmlSymbolResolutionRecord? lastResolvedTarget = null;

        foreach (FmlExpressionSource expressionSource in expression.Sources)
        {
            // check for odd expressions we do not want to handle
            if (string.IsNullOrEmpty(expressionSource.Identifier) ||
                !expressionSource.Identifier.Contains('.'))
            {
                continue;
            }

            FmlSymbolResolutionRecord? resolved = resolveFmlSymbol(
                expressionSource.Identifier,
                expressionSource.Alias,
                expressionSource.TypeIdentifier,
                sourceSymbols,
                StructureMap.StructureMapInputMode.Source);

            resolved ??= resolveFmlSymbol(
                expressionSource.Identifier,
                expressionSource.Alias,
                expressionSource.TypeIdentifier,
                localSourceSymbols,
                StructureMap.StructureMapInputMode.Source);

            if (resolved is null)
            {
                continue;
            }

            if (resolved.Alias is not null)
            {
                localSourceSymbols[resolved.Alias] = resolved;
            }
            else
            {
                localSourceSymbols[resolved.Identifier] = resolved;
            }

            lastResolvedSource = resolved;
        }

        foreach (FmlExpressionTarget expressionTarget in expression.Targets)
        {
            // check for odd expressions we do not want to handle
            if (string.IsNullOrEmpty(expressionTarget.Identifier) ||
                !expressionTarget.Identifier.Contains('.'))
            {
                continue;
            }

            FmlSymbolResolutionRecord? resolved = resolveFmlSymbol(
                expressionTarget.Identifier,
                expressionTarget.Alias,
                null,
                targetSymbols,
                StructureMap.StructureMapInputMode.Target);

            resolved ??= resolveFmlSymbol(
                expressionTarget.Identifier,
                expressionTarget.Alias,
                null,
                localTargetSymbols,
                StructureMap.StructureMapInputMode.Target);

            if (resolved is null)
            {
                continue;
            }

            if (resolved.Alias is not null)
            {
                localTargetSymbols[resolved.Alias] = resolved;
            }
            else
            {
                localTargetSymbols[resolved.Identifier] = resolved;
            }

            lastResolvedTarget = resolved;
        }

        if ((lastResolvedSource is null) ||
            (lastResolvedTarget is null))
        {
            // either the source or the target is unresolved, we can ignore for now
            return;
        }

        if (localSourceSymbols.Count == 1)
        {
            if (!_relatedPaths.TryGetValue(lastResolvedSource.ResolvedPath, out HashSet<string>? relatedTargetPaths))
            {
                relatedTargetPaths = [];
                _relatedPaths.Add(lastResolvedSource.ResolvedPath, relatedTargetPaths);
            }

            relatedTargetPaths.Add(lastResolvedTarget.ResolvedPath);
        }
        else
        {
            throw new Exception("Is this possible?");
        }

        // check for dependent expression
        if (expression.DependentExpression is not null)
        {
            // iterate over the invocations
            foreach (FmlInvocation dependentInvocation in expression.DependentExpression.Invocations)
            {
                string name = dependentInvocation.Identifier;

                if (_fml!.GroupsByName.TryGetValue(name, out GroupDeclaration? dependentGroup))
                {
                    List<FmlSymbolResolutionRecord> dependentParams = [];
                    foreach (FmlInvocationParam invocationParam in dependentInvocation.Parameters)
                    {
                        if (invocationParam.Identifier is null)
                        {
                            continue;
                        }

                        FmlSymbolResolutionRecord? resolvedParam = null;

                        if (localSourceSymbols.TryGetValue(invocationParam.Identifier, out resolvedParam))
                        {
                            dependentParams.Add(resolvedParam);
                            continue;
                        }

                        if (localTargetSymbols.TryGetValue(invocationParam.Identifier, out resolvedParam))
                        {
                            dependentParams.Add(resolvedParam);
                            continue;
                        }

                        if (sourceSymbols.TryGetValue(invocationParam.Identifier, out resolvedParam))
                        {
                            dependentParams.Add(resolvedParam);
                            continue;
                        }

                        if (targetSymbols.TryGetValue(invocationParam.Identifier, out resolvedParam))
                        {
                            dependentParams.Add(resolvedParam);
                            continue;
                        }

                        // if we failed to resolve, it is something that nested into a type - ignore for now
                        continue;
                    }

                    processFmlGroup(
                        dependentGroup,
                        [],
                        [],
                        dependentParams,
                        rootSource,
                        rootTarget,
                        groupCallStack);
                }

            }
        }

    }

    private void processSimpleCopyExpression(
        GroupDeclaration group,
        MapSimpleCopyExpression expression,
        Dictionary<string, FmlSymbolResolutionRecord> sourceSymbols,
        Dictionary<string, FmlSymbolResolutionRecord> targetSymbols)
    {
        if (resolveFmlSymbol(expression.Source, null, null, sourceSymbols, StructureMap.StructureMapInputMode.Source)
            is not FmlSymbolResolutionRecord sourceResovled)
        {
            _logger.LogInformation(
                $" <<< processSimpleCopyExpression for {_fmlFilename} Line: {expression.Line}" +
                $" Failed to resolve source `{expression.Source}` in group {group.Name}");
            return;
        }

        if (resolveFmlSymbol(expression.Target, null, null, targetSymbols, StructureMap.StructureMapInputMode.Target)
            is not FmlSymbolResolutionRecord targetResolved)
        {
            _logger.LogInformation(
                $" <<< processSimpleCopyExpression for {_fmlFilename} Line: {expression.Line}" +
                $"Failed to resolve target `{expression.Target}` in group {group.Name}");
            return;
        }

        if (!_relatedPaths.TryGetValue(sourceResovled.ResolvedPath, out HashSet<string>? relatedTargetPaths))
        {
            relatedTargetPaths = [];
            _relatedPaths.Add(sourceResovled.ResolvedPath, relatedTargetPaths);
        }

        relatedTargetPaths.Add(targetResolved.ResolvedPath);
        _simpleTargetPaths.Add((sourceResovled.ResolvedPath, targetResolved.ResolvedPath));
    }

    [Obsolete]
    private void processCrossVersionGroup(
        string sourcePrefix,
        string targetPrefix,
        GroupDeclaration group,
        Dictionary<string, Dictionary<string, FmlTargetInfo>> fmlPathLookup, IEnumerable<string>? dependentGroupCallStack = null)
    {
        string groupSourceVar = string.Empty;
        string groupTargetVar = string.Empty;

        if (sourcePrefix.Length > 2048 || targetPrefix.Length > 2048)
        {
            // A safety check on missing recursive definitions...
            _logger.LogWarning($"{_fml!.MapDirective?.Url ?? _fml.MetadataByPath["url"]?.Literal?.Value} {group.Name} Path likely in a recursive loop {sourcePrefix} -> {targetPrefix}");
            throw new ApplicationException($"Path likely in a recursive loop {sourcePrefix} -> {targetPrefix}");
        }

        // parse out the source and target names from the group
        foreach (GroupParameter gp in group.Parameters)
        {
            switch (gp.InputMode)
            {
                case StructureMap.StructureMapInputMode.Source:
                    groupSourceVar = gp.Identifier;
                    break;

                case StructureMap.StructureMapInputMode.Target:
                    groupTargetVar = gp.Identifier;
                    break;
            }
        }

        if (string.IsNullOrEmpty(groupSourceVar) || string.IsNullOrEmpty(groupTargetVar))
        {
            throw new Exception("Failed to parse group parameters");
        }

        int groupSourceVarLen = groupSourceVar.Length;
        int groupTargetVarLen = groupTargetVar.Length;

        // iterate over expressions in this group
        foreach (GroupExpression exp in group.Expressions)
        {
            if (exp.SimpleCopyExpression != null)
            {
                string sourceName;

                if (exp.SimpleCopyExpression.Source.StartsWith(groupSourceVar, StringComparison.Ordinal))
                {
                    if (exp.SimpleCopyExpression.Source.Length == groupSourceVarLen)
                    {
                        sourceName = string.Empty;
                    }
                    else
                    {
                        // add our current name prefix
                        sourceName = exp.SimpleCopyExpression.Source[(groupSourceVarLen + 1)..];
                    }
                }
                else
                {
                    sourceName = exp.SimpleCopyExpression.Source;
                }

                string targetName;

                if (exp.SimpleCopyExpression.Target == null)
                {
                    targetName = string.Empty;
                }
                else if (exp.SimpleCopyExpression.Target.StartsWith(groupTargetVar, StringComparison.Ordinal))
                {
                    if (exp.SimpleCopyExpression.Target.Length == groupTargetVarLen)
                    {
                        targetName = string.Empty;
                    }
                    else
                    {
                        targetName = exp.SimpleCopyExpression.Target[(groupTargetVarLen + 1)..];
                    }
                }
                else
                {
                    targetName = exp.SimpleCopyExpression.Target;
                }

                // add our current name prefix
                sourceName = $"{sourcePrefix}.{sourceName}";
                targetName = $"{targetPrefix}.{targetName}";

                if (!fmlPathLookup.TryGetValue(sourceName, out Dictionary<string, FmlTargetInfo>? expressionsByTarget))
                {
                    expressionsByTarget = [];
                    fmlPathLookup.Add(sourceName, expressionsByTarget);
                }

                expressionsByTarget.Add(targetName, new()
                {
                    FhirMappingExpression = exp,
                    IsSimpleCopy = true,
                });

                continue;
            }

            if (exp.MappingExpression != null)
            {
                foreach (FmlExpressionSource source in exp.MappingExpression.Sources)
                {
                    string ruleSourcePrefix = source.Identifier.Split('.')[0];

                    if (ruleSourcePrefix != groupSourceVar)
                    {
                        // skip elements that do not start with our matching variable
                        continue;
                    }

                    string sourceName;

                    if (source.Identifier.StartsWith(groupSourceVar, StringComparison.Ordinal))
                    {
                        if (source.Identifier.Length == groupSourceVarLen)
                        {
                            sourceName = string.Empty;
                        }
                        else
                        {
                            // add our current name prefix
                            sourceName = source.Identifier[(groupSourceVarLen + 1)..];
                        }
                    }
                    else
                    {
                        sourceName = source.Identifier;
                    }

                    // add our current name prefix
                    sourceName = $"{sourcePrefix}.{sourceName}";

                    if (!fmlPathLookup.TryGetValue(sourceName, out Dictionary<string, FmlTargetInfo>? expressionsByTarget))
                    {
                        expressionsByTarget = [];
                        fmlPathLookup.Add(sourceName, expressionsByTarget);
                    }

                    foreach (FmlExpressionTarget target in exp.MappingExpression.Targets)
                    {
                        string targetName;

                        if (target.Identifier == null)
                        {
                            targetName = string.Empty;
                        }
                        else if (target.Identifier.StartsWith(groupTargetVar, StringComparison.Ordinal))
                        {
                            if (target.Identifier.Length == groupTargetVarLen)
                            {
                                targetName = string.Empty;
                            }
                            else
                            {
                                targetName = target.Identifier[(groupTargetVarLen + 1)..];
                            }
                        }
                        else
                        {
                            targetName = target.Identifier;
                        }

                        // add our current name prefix
                        targetName = string.IsNullOrEmpty(targetName) ? targetPrefix : $"{targetPrefix}.{targetName}";

                        string transformName = target.Transform?.Invocation?.Identifier ?? string.Empty;

                        switch (transformName)
                        {
                            case "translate":
                                expressionsByTarget[targetName] = new()
                                {
                                    FhirMappingExpression = exp,
                                    HasTransform = target.Transform != null,
                                    TransformName = transformName,
                                    TranslateReference = ((target.Transform?.Invocation?.Parameters.Count ?? 0) > 1)
                                        ? target.Transform!.Invocation!.Parameters[1]!.Literal?.ValueAsString ?? string.Empty
                                        : string.Empty,
                                    TranslateType = ((target.Transform?.Invocation?.Parameters.Count ?? 0) > 2)
                                        ? target.Transform!.Invocation!.Parameters[2]!.Literal?.ValueAsString ?? string.Empty
                                        : string.Empty,
                                };
                                break;

                            default:
                                //if (!string.IsNullOrEmpty(transformName))
                                //{
                                //    Console.Write("");
                                //}
                                expressionsByTarget[targetName] = new()
                                {
                                    FhirMappingExpression = exp,
                                    HasTransform = target.Transform != null,
                                    TransformName = transformName,
                                    IsComplexTransform = !string.IsNullOrEmpty(transformName),
                                };
                                break;
                        }

                        // try to nest into a dependent expression if there is one
                        if (exp.MappingExpression.DependentExpression != null)
                        {
                            foreach (FmlInvocation dependentInvocation in exp.MappingExpression.DependentExpression.Invocations)
                            {
                                string fnName = dependentInvocation.Identifier;
                                if (fnName != group.Name && _fml!.GroupsByName.TryGetValue(fnName, out GroupDeclaration? dependentGroup))
                                {
                                    if (dependentGroupCallStack == null || !dependentGroupCallStack.Contains(fnName))
                                    {
                                        var newStack = dependentGroupCallStack?.Append(fnName).ToArray() ?? [fnName];
                                        processCrossVersionGroup(sourceName, targetName, dependentGroup, fmlPathLookup, newStack);
                                    }
                                }
                            }
                        }
                    }

                }

                continue;
            }
        }
    }

    private static DbStructureDefinition ResolveDeclaration(XVerStructureCatalog catalog, DbFhirPackage package, string url, string filename)
    {
        // Declaration URLs retain their FML string delimiters in the parser model.
        if (url.Length >= 2 && url[0] is '"' or '\'' && url[^1] == url[0])
        {
            url = url[1..^1];
        }
        UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(url);
        const string releaseNamespace = "http://hl7.org/fhir/";
        if (canonical.Url?.StartsWith(releaseNamespace, StringComparison.Ordinal) == true)
        {
            string[] components = canonical.Url[releaseNamespace.Length..].Split('/');
            string release = components[0];
            if (release is "1.0" or "3.0" or "4.0" or "4.3" or "5.0" or "6.0")
            {
                if (release != package.DefinitionFhirSequence.ToShortVersion() ||
                    package.PackageId != package.DefinitionFhirSequence.ToCorePackageId() ||
                    package.CanonicalUrl != "http://hl7.org/fhir" ||
                    !(components.Length == 2 || components.Length == 3 && components[1] == "StructureDefinition") ||
                    components[^1].Length == 0 || !components[^1].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.'))
                {
                    throw new InvalidOperationException($"FHIR release-qualified FML declaration '{url}' does not identify a definition in {package.PackageId}@{package.PackageVersion} at {filename}.");
                }

                string normalized = releaseNamespace + "StructureDefinition/" + components[^1] +
                    (canonical.Version is null ? string.Empty : "|" + canonical.Version);
                return catalog.ResolveDefinition(package.Key, normalized, context: filename)!;
            }
        }

        return catalog.ResolveDefinition(package.Key, url, context: filename)!;
    }

    private void reconcileElementMapFmlPathsInDb(
        FmlSymbolResolutionRecord rootSource,
        FmlSymbolResolutionRecord rootTarget)
    {
        if ((_sourcePackage is null) || (_targetPackage is null))
        {
            throw new InvalidOperationException("Source or target package not set");
        }

        Dictionary<(int, int), DbStructureMapping> addedMappings = [];

        DbRecordCache<DbStructureMapping> sdMappingCache = new();
        DbRecordCache<DbElementMapping> edMappingCache = new();

        // look for elements that target other elements
        foreach ((string sourcePath, HashSet<string> targetPaths) in _relatedPaths)
        {
            // skip items with no targets
            if (targetPaths.Count == 0)
            {
                continue;
            }

            // set the default relationship based on the number of targets
            ConceptMap.ConceptMapRelationship initialRelationship = targetPaths.Count == 1
                ? ConceptMap.ConceptMapRelationship.Equivalent
                : ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget;

            foreach (string targetPath in targetPaths)
            {
                // check for source and target paths being the same
                //if (sourcePath == targetPath)
                //{
                //    continue;
                //}

                // grab the source and target type info to check for mappings
                string sourceTypeName = sourcePath.Split('.')[0];
                string targetTypeName = targetPath.Split('.')[0];

                // we want all paths related to the root structure they started from in FML
                DbStructureDefinition sourceSd = rootSource.Structure;

                //// make sure we have a source structure
                //if (DbStructureDefinition.SelectSingle(_db, FhirPackageKey: _sourcePackage.Key, Id: sourceTypeName)
                //    is not DbStructureDefinition sourceSd)
                //{
                //    _logger.LogError(
                //        $"Could not resolve source type {sourceTypeName} for {sourcePath}" +
                //        $" in {_sourcePackage.ShortName}to{_targetPackage.ShortName}." +
                //        $" Falling back to root source: {rootSource.Structure.Id}");
                //    continue;
                //}

                // we want all paths related to the root structure they started from in FML
                DbStructureDefinition targetSd = rootTarget.Structure;

                //// make sure we have a target structure
                //if (DbStructureDefinition.SelectSingle(_db, FhirPackageKey: _targetPackage.Key, Id: targetTypeName)
                //    is not DbStructureDefinition targetSd)
                //{
                //    _logger.LogError(
                //        $"Could not resolve target structure {targetTypeName} for {targetPath}" +
                //        $" in {_sourcePackage.ShortName}to{_targetPackage.ShortName}." +
                //        $" Falling back to root target: {rootTarget.Structure.Id}");
                //    continue;
                //}

                // try to get a source element
                DbElement? sourceEd = DbElement.SelectSingle(_db, FhirPackageKey: _sourcePackage.Key, StructureKey: sourceSd.Key, Id: sourcePath);
                sourceEd ??= DbElement.SelectSingle(_db, FhirPackageKey: _sourcePackage.Key, StructureKey: sourceSd.Key, Id: sourcePath + "[x]");

                //// make sure the source element exists
                //if (sourceEd is null)
                //{
                //    _logger.LogWarning($"Note: could not resolve source path {sourcePath} for {sourceTypeName} in {_sourcePackage.ShortName}to{_targetPackage.ShortName}");
                //}

                //// skip elements that have child elements - will either be picked up by dependent groups or are not relevant
                //if (sourceEd?.ChildElementCount > 0)
                //{
                //    continue;
                //}

                // try to get a target element
                DbElement? targetEd = DbElement.SelectSingle(_db, FhirPackageKey: _targetPackage.Key, StructureKey: targetSd.Key, Id: targetPath);
                targetEd ??= DbElement.SelectSingle(_db, FhirPackageKey: _targetPackage.Key, StructureKey: targetSd.Key, Id: targetPath + "[x]");
                // make sure the target element exists
                //if (targetEd is null)
                //{
                //    _logger.LogWarning($"Note: Could not resolve target path {targetPath} for {targetTypeName} in {_sourcePackage.ShortName}to{_targetPackage.ShortName}");
                //}

                //// skip elements that have child elements - will either be picked up by dependent groups or are not relevant
                //if (targetEd?.ChildElementCount > 0)
                //{
                //    continue;
                //}

                DbStructureMapping structureMappingRec;

                // check to see if there are existing mapping records for the structures
                List<DbStructureMapping> sdMappingRecords = addedMappings.TryGetValue((sourceSd.Key, targetSd.Key), out DbStructureMapping? localMapping)
                    ? [localMapping]
                    : DbStructureMapping.SelectList(
                        _db,
                        SourceFhirPackageKey: _sourcePackage.Key,
                        SourceStructureKey: sourceSd.Key,
                        TargetFhirPackageKey: _targetPackage.Key,
                        TargetStructureKey: targetSd.Key);

                // if there are no records, we need to create one
                if (sdMappingRecords.Count == 0)
                {
                    (string idLong, _, _) = XVerProcessor.GenerateArtifactId(
                        _sourcePackage.ShortName,
                        sourceSd.Id,
                        _targetPackage.ShortName,
                        targetArtifactId: targetSd.Id);

                    // check for maps to other targets and no-maps
                    int otherStructureMapCount = DbStructureMapping.SelectCount(
                        _db,
                        SourceFhirPackageKey: _sourcePackage.Key,
                        SourceStructureKey: sourceSd.Key,
                        TargetFhirPackageKey: _targetPackage.Key);

                    // if there are *other* maps for this source structure, assume the FML is overly-ambitious and log a warning
                    if (otherStructureMapCount > 0)
                    {
                        // if we do not have a structure mapping record by this point in processing, assume the FML is overly-ambitious
                        _logger.LogWarning(
                            $"FML wants to create {idLong}, but" +
                            $" {_sourcePackage.ShortName}:{sourceSd.Name} has relationships for" +
                            $" {_targetPackage.ShortName} that do not include {targetSd.Name}.");
                        continue;
                    }

                    structureMappingRec = new()
                    {
                        Key = DbStructureMapping.GetIndex(),
                        ConceptMapSourceKey = null,
                        ConceptMapFilename = null,
                        FmlSourceKey = _sourceFileKey,
                        FmlFilename = _sourceFilename,
                        IsFallback = false,                 // there are no fallback FML files

                        SourceFhirPackageKey = _sourcePackage.Key,
                        SourceFhirSequence = _sourcePackage.DefinitionFhirSequence,
                        SourceStructureKey = sourceSd.Key,
                        SourceStructureId = sourceSd.Id,
                        SourceStructureUrl = sourceSd.UnversionedUrl,

                        TargetFhirPackageKey = _targetPackage.Key,
                        TargetFhirSequence = _targetPackage.DefinitionFhirSequence,
                        TargetStructureKey = targetSd.Key,
                        TargetStructureId = targetSd.Id,
                        TargetStructureUrl = targetSd.UnversionedUrl,

                        ExplicitNoMap = false,
                        Relationship = initialRelationship,
                        ConceptDomainRelationship = null,
                        ValueDomainRelationship = null,
                    };

                    sdMappingCache.CacheAdd(structureMappingRec);
                    sdMappingRecords.Add(structureMappingRec);
                    addedMappings.Add((sourceSd.Key, targetSd.Key), structureMappingRec);
                }
                else
                {
                    structureMappingRec = sdMappingRecords[0];

                    if (structureMappingRec.FmlSourceKey != _sourceFileKey)
                    {
                        structureMappingRec.FmlSourceKey = _sourceFileKey;
                        sdMappingCache.CacheUpdate(structureMappingRec);
                    }
                }

                // check to see if there are existing element mapping records
                List<DbElementMapping> edMappingRecords;

                if ((sourceEd is not null) && (targetEd is not null))
                {
                    // check for explicit matches
                    edMappingRecords = DbElementMapping.SelectList(
                        _db,
                        StructureMappingKey: structureMappingRec.Key,
                        SourceElementKey: sourceEd.Key,
                        TargetElementKey: targetEd.Key);

                    // add no-map records
                    edMappingRecords.AddRange(DbElementMapping.SelectList(
                        _db,
                        SourceFhirPackageKey: _sourcePackage.Key,
                        StructureMappingKey: structureMappingRec.Key,
                        TargetFhirPackageKey: _targetPackage.Key,
                        SourceElementKey: sourceEd.Key,
                        TargetElementIdIsNull: true));
                }
                else
                {
                    // check for path-based matches
                    edMappingRecords = DbElementMapping.SelectList(
                        _db,
                        SourceFhirPackageKey: _sourcePackage.Key,
                        TargetFhirPackageKey: _targetPackage.Key,
                        SourceElementId: sourceEd?.Id ?? sourcePath,
                        TargetElementId: targetEd?.Id ?? targetPath);

                    // add no-map records
                    edMappingRecords.AddRange(DbElementMapping.SelectList(
                        _db,
                        SourceFhirPackageKey: _sourcePackage.Key,
                        StructureMappingKey: structureMappingRec.Key,
                        SourceElementId: sourceEd?.Id ?? sourcePath,
                        TargetFhirPackageKey: _targetPackage.Key,
                        TargetElementIdIsNull: true));
                }

                // if there are no records, we need to create one
                if (edMappingRecords.Count == 0)
                {
                    DbElementMapping edMappingRec = new()
                    {
                        Key = DbElementMapping.GetIndex(),
                        ConceptMapSourceKey = null,
                        ConceptMapFilename = null,
                        FmlSourceKey = _sourceFileKey,
                        FmlFilename = _sourceFilename,

                        StructureMappingKey = structureMappingRec.Key,

                        SourceFhirPackageKey = _sourcePackage.Key,
                        SourceFhirSequence = _sourcePackage.DefinitionFhirSequence,
                        SourceStructureKey = sourceSd.Key,
                        SourceElementKey = sourceEd?.Key,
                        SourceElementId = sourceEd?.Id ?? sourcePath,

                        TargetFhirPackageKey = _targetPackage.Key,
                        TargetFhirSequence = _targetPackage.DefinitionFhirSequence,
                        TargetStructureKey = targetSd.Key,
                        TargetElementKey = targetEd?.Key,
                        TargetElementId = targetEd?.Id ?? targetPath,

                        FmlIsSimpleCopy = _simpleTargetPaths.Contains((sourceEd?.Id ?? sourcePath, targetEd?.Id ?? targetPath)),

                        ExplicitNoMap = false,
                        Relationship = null,
                        ConceptDomainRelationship = null,
                        ValueDomainRelationship = null,
                    };
                    edMappingCache.CacheAdd(edMappingRec);
                }
                else
                {
                    // check for updating FML source key
                    foreach (DbElementMapping edMappingRec in edMappingRecords)
                    {
                        if (edMappingRec.FmlSourceKey != _sourceFileKey)
                        {
                            edMappingRec.FmlSourceKey = _sourceFileKey;
                            edMappingRec.FmlIsSimpleCopy = _simpleTargetPaths.Contains((sourceEd?.Id ?? sourcePath, targetEd?.Id ?? targetPath));
                            edMappingCache.CacheUpdate(edMappingRec);
                        }
                    }
                }
            }
        }

        if ((sdMappingCache.Count > 0) || (edMappingCache.Count > 0))
        {
            sdMappingCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
            sdMappingCache.ToUpdate.Update(_db);

            edMappingCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
            edMappingCache.ToUpdate.Update(_db);

            _logger.LogInformation($"Maps from FML for {_name}: {sdMappingCache.Count} structures, {edMappingCache.Count} elements");
        }
    }


}
