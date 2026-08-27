// <copyright file="PackageLoader.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Language.Debugging;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Lib.Packaging;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Polyfill;
using Tasks = System.Threading.Tasks;
using Fhir.CodeGen.Common.Utils;

namespace Fhir.CodeGen.Lib.Loader;

internal static partial class PackageLoaderLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {processingKey}")]
    internal static partial void LogProcessingStartMessage(this ILogger logger, string processingKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loading package {moniker} (will not check dependencies)")]
    internal static partial void LogPackageLoading(this ILogger logger, string moniker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Package {moniker} ({packageFhirVersion}) does not match requested FHIR version {expectedFhirVersion}")]
    internal static partial void LogPackageFhirMismatch(this ILogger logger, string moniker, string packageFhirVersion, string? expectedFhirVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping already loaded dependency: {moniker}")]
    internal static partial void LogSkipMessage(this ILogger logger, string moniker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-loading core expansion: {moniker}")]
    internal static partial void LogAutoExpansionMessage(this ILogger logger, string moniker);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error parsing {style} {mime}: {exMessage}: {innerMessage}")]
    internal static partial void LogParseError(this ILogger logger, string style, string mime, string exMessage, string? innerMessage = null);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unsupported parse format {mime} for {style}")]
    internal static partial void LogInvalidFormat(this ILogger logger, string style, string mime);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Package {actual} loaded in place of {requested}")]
    internal static partial void LogPackageSubstitutionSuccess(this ILogger logger, string requested, string actual);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not find a package for {requested} with the correct FHIR version - please specify manually if required")]
    internal static partial void LogPackageSubstitutionFailure(this ILogger logger, string requested);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dependencies resolved for {moniker}, loading package contents")]
    internal static partial void LogPackageDependenciesResolved(this ILogger logger, string moniker);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to access the syncrhonization object for {moniker}!")]
    internal static partial void LogSynchronizationFailed(this ILogger logger, string moniker);

}


/// <summary>A package loader.</summary>
public partial class PackageLoader : IDisposable
{
    private const int _packageAccessTimeout = 5 * 60 * 1000;

    private readonly HttpClient _httpClient;
    private readonly ICodeGenPackageSource _packageSource;

    private bool _disposedValue = false;

    /// <summary>(Immutable) The sorted order for definition types we want to load.</summary>
    private static readonly string[] _sortedLoadOrder =
    [
        "CodeSystem",
        "ValueSet",
        "StructureDefinition",
        "SearchParameter",
        "OperationDefinition",
        "CapabilityStatement",
        "Conformance",
        "ImplementationGuide",
        "CompartmentDefinition",
    ];

    private ConfigRoot _rootConfiguration;

    private FhirReleases.FhirSequenceCodes _defaultFhirVersion;

    /// <summary>The JSON model.</summary>
    private LoaderOptions.JsonDeserializationModel _jsonModel;

    /// <summary>Options for controlling the JSON.</summary>
    private JsonSerializerOptions _jsonOptions;

    /// <summary>The lenient JSON parser.</summary>
    private FhirJsonPocoDeserializer _jsonParser;

    /// <summary>The logger.</summary>
    private ILogger _logger;

#if !DISABLE_XML
    /// <summary>The lenient XML parser.</summary>
    private FhirXmlPocoDeserializer _xmlParser;
#endif

    private Fhir.CodeGen.CrossVersionLoader.Converter_43_50? _converter_43_50 = null;
    private Fhir.CodeGen.CrossVersionLoader.Converter_30_50? _converter_30_50 = null;
    private Fhir.CodeGen.CrossVersionLoader.Converter_20_50? _converter_20_50 = null;

    private object _convertLockObject = new();

    /// <summary>Initializes a new instance of the <see cref="PackageLoader"/> class.</summary>
    /// <param name="config">(Optional) The configuration.</param>
    /// <param name="opts">  (Optional) Options for controlling the operation.</param>
    public PackageLoader(
        ConfigRoot? config = null,
        LoaderOptions? opts = null,
        HttpClient? httpClient = null)
    {
        _logger = (config?.LogFactory ?? LoggerFactory.Create(builder => builder.AddConsole())).CreateLogger<PackageLoader>();

        // the package library owns its own HTTP client factory; this one serves the direct
        // downloads and cross-version fetches that this class still performs itself
        _httpClient = httpClient ?? new();

        // use defaults if nothing was specified
        opts ??= new();

        _rootConfiguration = config ?? new();

        _packageSource = new FhirPkgPackageSource(_rootConfiguration, _rootConfiguration.LogFactory);

        _defaultFhirVersion = FhirReleases.FhirVersionToSequence(_rootConfiguration.FhirVersion);

        _jsonOptions = opts.FhirJsonOptions.UsingMode(DeserializerModes.Ostrich);
        _jsonParser = new(opts.FhirJsonSettings);

#if !DISABLE_XML
        _xmlParser = new(opts.FhirXmlSettings);
#endif
        _jsonModel = opts.JsonModel;
    }

    /// <summary>Adds all interaction parameters to a core definition collection.</summary>
    /// <param name="dc">The device-context.</param>
    private void AddAllInteractionParameters(DefinitionCollection dc)    {
        dc.AddHttpQueryParameter(new()
        {
            Name = "_format",
            Url = "http://hl7.org/fhir/http.html#format",
            Description = "Parameter to specify alternative response formats by their MIME-types.",
            ParamType = SearchParamType.String,
        });

        dc.AddHttpQueryParameter(new()
        {
            Name = "_summary",
            Url = "http://hl7.org/fhir/search.html#summary",
            Description = "Request to return a portion of matching resources.",
            ParamType = SearchParamType.Token,
            AllowedValues = ["true", "false", "count", "data", "text",],
        });

        dc.AddHttpQueryParameter(new()
        {
            Name = "_elements",
            Url = "http://hl7.org/fhir/search.html#elements",
            Description = "Request to return specific elements from resources.",
            ParamType = SearchParamType.Token,
        });

        // add parameters from R4 and later
        if (dc.FhirSequence >= FhirReleases.FhirSequenceCodes.R4)
        {
            dc.AddHttpQueryParameter(new()
            {
                Name = "_pretty",
                Url = "http://hl7.org/fhir/http.html#pretty",
                Description = "Ask for a pretty printed response for human convenience.",
                ParamType = SearchParamType.String,
                AllowedValues = [ "true", "false", ],
            });
        }
    }

    /// <summary>Adds a search result parameters to a core definition collection.</summary>
    /// <param name="dc">The device-context.</param>
    private void AddSearchResultParameters(DefinitionCollection dc)
    {
        dc.AddSearchResultParameter(new()
        {
            Name = "_sort",
            Url = "http://hl7.org/fhir/search.html#sort",
            Description = "Used to indicate which order to return the results, which can have a value of one of the search parameters.",
            ParamType = SearchParamType.String,
        });

        dc.AddSearchResultParameter(new()
        {
            Name = "_count",
            Url = "http://hl7.org/fhir/search.html#count",
            Description = "A hint to the server regarding how many resources should be returned in a single page. Servers SHALL NOT return more resources than requested but are allowed to return less than the client requested.",
            ParamType = SearchParamType.Number,
        });

        dc.AddSearchResultParameter(new()
        {
            Name = "_include",
            Url = "http://hl7.org/fhir/search.html#include",
            Description = "Request to return resources related to the search results, by moving forward across references.",
            ParamType = SearchParamType.String,
        });

        dc.AddSearchResultParameter(new()
        {
            Name = "_revinclude",
            Url = "http://hl7.org/fhir/search.html#revinclude",
            Description = "Request to return resources related to the search results, by moving backwards across references.",
            ParamType = SearchParamType.String,
        });

        dc.AddSearchResultParameter(new()
        {
            Name = "_contained",
            Url = "http://hl7.org/fhir/search.html#contained",
            Description = "Request modification to handling of contained resource searching.",
            ParamType = SearchParamType.Token,
            AllowedValues = [ "true", "false", "both", ],
        });

        dc.AddSearchResultParameter(new()
        {
            Name = "_containedType",
            Url = "http://hl7.org/fhir/search.html#containedType",
            Description = "When contained resources are being returned, whether the server should return either the container or the contained resource.",
            ParamType = SearchParamType.Token,
            AllowedValues = [ "container", "contained", ],
        });

        // add parameters from R4 and later
        if (dc.FhirSequence >= FhirReleases.FhirSequenceCodes.R4)
        {
            dc.AddSearchResultParameter(new()
            {
                Name = "_total",
                Url = "http://hl7.org/fhir/search.html#total",
                Description = "Optimization hint for servers indicating reliance on the Bundle.total element.",
                ParamType = SearchParamType.Token,
                AllowedValues = [ "none", "estimate", "accurate" ],
            });
        }
    }

    /// <summary>Adds a missing core search parameters to a core definition collection.</summary>
    /// <param name="dc">The device-context.</param>
    private void AddMissingCoreSearchParameters(DefinitionCollection dc, string packageId, string packageVersion)
    {
        dc.AddSearchParameter(new()
        {
            Id = "Resource-content",
            Name = "_content",
            Code = "_content",
            Url = "http://hl7.org/fhir/SearchParameter/Resource-content",
            Version = dc.FhirSequence.ToLongVersion(),
            Title = "Resource content filter",
            Status = PublicationStatus.Active,
            Description = "Search on the entire content of the resource.",
            Base = [ VersionIndependentResourceTypesAll.Resource ],
            Type = SearchParamType.Special,
        }, packageId, packageVersion, true);

        dc.AddSearchParameter(new()
        {
            Id = "Resource-filter",
            Name = "_filter",
            Code = "_filter",
            Url = "http://hl7.org/fhir/SearchParameter/Resource-filter",
            Version = dc.FhirSequence.ToLongVersion(),
            Title = "Advanced search filter",
            Status = PublicationStatus.Active,
            Description = "Filter search parameter which supports a more sophisticated grammar for searching.",
            Base = [ VersionIndependentResourceTypesAll.Resource ],
            Type = SearchParamType.Special,
        }, packageId, packageVersion, true);

        dc.AddSearchParameter(new()
        {
            Id = "Resource-text",
            Name = "_text",
            Code = "_text",
            Url = "http://hl7.org/fhir/SearchParameter/Resource-text",
            Version = dc.FhirSequence.ToLongVersion(),
            Title = "Resource text filter",
            Status = PublicationStatus.Active,
            Description = "Search the narrative content of a resource.",
            Base = [ VersionIndependentResourceTypesAll.Resource ],
            Type = SearchParamType.String,
        }, packageId, packageVersion, true);

        dc.AddSearchParameter(new()
        {
            Id = "Resource-list",
            Name = "_list",
            Code = "_list",
            Url = "http://hl7.org/fhir/SearchParameter/Resource-list",
            Version = dc.FhirSequence.ToLongVersion(),
            Title = "List reference filter",
            Status = PublicationStatus.Active,
            Description = "Filter based on resources referenced by a List resource.",
            Base = [ VersionIndependentResourceTypesAll.Resource ],
            Type = SearchParamType.Reference,
            Target = [ VersionIndependentResourceTypesAll.List ],
        }, packageId, packageVersion, true);

        if (dc.FhirSequence >= FhirReleases.FhirSequenceCodes.R4)
        {
            dc.AddSearchParameter(new()
            {
                Id = "Resource-has",
                Name = "_has",
                Code = "_has",
                Url = "http://hl7.org/fhir/SearchParameter/Resource-has",
                Version = dc.FhirSequence.ToLongVersion(),
                Title = "Limited support for reverse chaining",
                Status = PublicationStatus.Active,
                Description = "For selecting resources based on the properties of resources that refer to them.",
                Base = [ VersionIndependentResourceTypesAll.Resource ],
                Type = SearchParamType.Special,
            }, packageId, packageVersion, true);

            dc.AddSearchParameter(new()
            {
                Id = "Resource-type",
                Name = "_type",
                Code = "_type",
                Url = "http://hl7.org/fhir/SearchParameter/Resource-type",
                Version = dc.FhirSequence.ToLongVersion(),
                Title = "Resource type filter",
                Status = PublicationStatus.Active,
                Description = "For filtering resources based on their type in searches across resource types.",
                Base = [ VersionIndependentResourceTypesAll.Resource ],
                Type = SearchParamType.Token,
            }, packageId, packageVersion, true);
        }
    }

    private void CreateConverterIfRequired(FhirReleases.FhirSequenceCodes fhirSequence)
    {
        // create the converter we need
        switch (fhirSequence)
        {
            case FhirReleases.FhirSequenceCodes.DSTU2:
                {
                    if (_converter_20_50 is null)
                    {
                        lock (_convertLockObject)
                        {
                            _converter_20_50 ??= new();
                        }
                    }
                }
                break;

            case FhirReleases.FhirSequenceCodes.STU3:
                {
                    if (_converter_30_50 is null)
                    {
                        lock (_convertLockObject)
                        {
                            _converter_30_50 ??= new();
                        }
                    }
                }
                break;

            case FhirReleases.FhirSequenceCodes.R4:
            case FhirReleases.FhirSequenceCodes.R4B:
                {
                    if (_converter_43_50 is null)
                    {
                        lock (_convertLockObject)
                        {
                            _converter_43_50 ??= new();
                        }
                    }
                }
                break;

            default:
            case FhirReleases.FhirSequenceCodes.R5:
                {
                }
                break;
        }
    }

    private bool LoadFromDirectory(ref DefinitionCollection? definitions, string sourcePath, string? fhirVersion)
    {
        FhirReleases.FhirSequenceCodes fhirSequence = string.IsNullOrEmpty(fhirVersion)
            ? _defaultFhirVersion
            : FhirReleases.FhirVersionToSequence(fhirVersion!);

        if (fhirSequence == FhirReleases.FhirSequenceCodes.Unknown)
        {
            if ((definitions is null) ||
                (definitions.FhirSequence == FhirReleases.FhirSequenceCodes.Unknown))
            {
                throw new Exception("Cannot load from a directory with an unknown FHIR version");
            }

            fhirSequence = definitions.FhirSequence;
        }

        CreateConverterIfRequired(fhirSequence);

        string? name = Path.GetFileName(sourcePath);

        if (name is null)
        {
            throw new Exception($"Failed to get directory name from '{sourcePath}'");
        }

        string canonical = $"file://{sourcePath}";

        definitions ??= new(_rootConfiguration)
        {
            Name = name,
            FhirSequence = fhirSequence,
            FhirVersionLiteral = fhirSequence.ToLiteral(),
            MainPackageId = name,
            MainPackageVersion = "dev",
            MainPackageCanonical = canonical,
            Logger = _rootConfiguration.LogFactory.CreateLogger<DefinitionCollection>(),
        };

        // get files in the directory
        string[] files = Directory.GetFiles(sourcePath, "*.json", SearchOption.TopDirectoryOnly);
        foreach (string path in files)
        {
            string fileExtension = Path.GetExtension(path);

            object? r;

            switch (fhirSequence)
            {
                case FhirReleases.FhirSequenceCodes.DSTU2:
                    {
                        r = ParseContents20(fileExtension, path: path);
                    }
                    break;

                case FhirReleases.FhirSequenceCodes.STU3:
                    {
                        r = ParseContents30(fileExtension, path: path);
                    }
                    break;

                case FhirReleases.FhirSequenceCodes.R4:
                case FhirReleases.FhirSequenceCodes.R4B:
                    {
                        r = ParseContents43(fileExtension, path: path);
                    }
                    break;

                default:
                case FhirReleases.FhirSequenceCodes.R5:
                    {
                        r = ParseContentsPoco(fileExtension, path);
                    }
                    break;
            }

            if (r is null)
            {
                throw new Exception($"Failed to parse '{path}'");
            }

            definitions.AddResource(r, _defaultFhirVersion, name, "dev", canonical);
        }

        return true;
    }

    /// <summary>Loads a package.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="packages">   The cached package.</param>
    /// <param name="definitions">(Optional) The definitions.</param>
    /// <param name="fhirVersion">(Optional) The FHIR version.</param>
    /// <returns>An asynchronous result that yields the package.</returns>
    public async Tasks.Task<DefinitionCollection?> LoadPackages(
        IEnumerable<string> packages,
        DefinitionCollection? definitions = null,
        string? fhirVersion = null,
        HashSet<FhirArtifactClassEnum>? loadFilterOverride = null,
        bool? autoLoadExpansionsValue = null,
        bool? resolveDependenciesValue = null)
    {
        await Tasks.Task.Delay(0);

        bool autoLoadExpansions = autoLoadExpansionsValue ?? _rootConfiguration.AutoLoadExpansions;
        bool resolveDependencies = resolveDependenciesValue ?? _rootConfiguration.ResolvePackageDependencies;

        string? requestedFhirVersion = fhirVersion;

        if (!packages.Any())
        {
            return null;
        }

        HashSet<FhirArtifactClassEnum> artifactFilter = loadFilterOverride ?? new (_rootConfiguration.LoadStructures);

        // we need to filter structures post parsing if we are not loading all known types
        bool filterStructureDefinitions = !artifactFilter.Contains(FhirArtifactClassEnum.PrimitiveType) ||
                                          !artifactFilter.Contains(FhirArtifactClassEnum.ComplexType) ||
                                          !artifactFilter.Contains(FhirArtifactClassEnum.Resource) ||
                                          !artifactFilter.Contains(FhirArtifactClassEnum.Extension) ||
                                          !artifactFilter.Contains(FhirArtifactClassEnum.Profile) ||
                                          !artifactFilter.Contains(FhirArtifactClassEnum.LogicalModel) ||
                                          !artifactFilter.Contains(FhirArtifactClassEnum.Interface);

        foreach (string inputDirective in packages)
        {
            if (string.IsNullOrEmpty(inputDirective))
            {
                continue;
            }

            // check to see if we think this is a directory
            if ((inputDirective.IndexOfAny(Path.GetInvalidPathChars()) == -1) &&
                (inputDirective.Contains('/') || inputDirective.Contains('\\') || inputDirective.Contains('~')))
            {
                // check to see if we can find the directory

                string resolvedDir = FileSystemUtils.FindRelativeDir(".", inputDirective, false);

                if (!string.IsNullOrEmpty(resolvedDir))
                {
                    _logger.LogProcessingStartMessage(inputDirective);

                    if (!LoadFromDirectory(ref definitions, resolvedDir, fhirVersion))
                    {
                        throw new Exception($"Failed to load package from directory: {inputDirective}");
                    }

                    // if we loaded from the directory, just continue
                    continue;
                }
            }

            CodeGenPackageDirective packageDirective = _packageSource.Parse(inputDirective);

            if (packageDirective.PackageId is null)
            {
                throw new Exception($"Failed to parse package reference: {inputDirective}");
            }

            // check to see if this is an explicit request for a FHIR release that is no longer available
            if (FhirPackageUtils.PackageIsFhirRelease(packageDirective.PackageId) &&
                packageDirective.IsExactVersion &&
                FhirReleases.VersionIsUnavailable(packageDirective.RequestedVersion ?? string.Empty))
            {
                packageDirective = _packageSource.Parse(
                    packageDirective.PackageId + "@" + FhirReleases.GetCurrentPatch(packageDirective.RequestedVersion ?? string.Empty));

                if (packageDirective.PackageId is null)
                {
                    throw new Exception($"Failed to parse package reference: {inputDirective}");
                }

                if (definitions?.TryGetManifest(packageDirective.PackageId, packageDirective.RequestedVersion ?? string.Empty, out _) == true)
                {
                    // we have already loaded this package, just continue
                    continue;
                }
            }

            // create our definition collection shell if we do not have one
            if (definitions is null)
            {
                definitions = new(_rootConfiguration)
                {
                    Name = packageDirective.PackageId,
                    Logger = _rootConfiguration.LogFactory.CreateLogger<DefinitionCollection>(),
                };

                if (!string.IsNullOrEmpty(fhirVersion))
                {
                    definitions.FhirSequence = FhirReleases.FhirVersionToSequence(fhirVersion!);
                }
            }

            // check if we are flagged to load expansions and this is a core package
            if (autoLoadExpansions &&
                (
                    FhirPackageUtils.PackageIsFhirCore(packageDirective.PackageId) ||
                    FhirPackageUtils.PackageIsFhirCorePartial(packageDirective.PackageId)
                ) &&
                (packageDirective.PackageId != "hl7.fhir.r2.core"))
            {
                string expansionPackageName = packageDirective.PackageId.Replace(".core", ".expansions");
                string expansionDirective = expansionPackageName +
                    "@" +
                    (packageDirective.RequestedVersion ?? "latest");

                _logger.LogAutoExpansionMessage($"Auto-loading core expansions: {expansionDirective}...");

                await LoadPackages([expansionDirective], definitions, requestedFhirVersion);
            }

            _logger.LogProcessingStartMessage(packageDirective.AnyDirective);

            string installDirective = packageDirective.RawDirective;

            // the cross-process guard is a named Mutex, which may only be released by the thread
            // that took it - so the wait, the install, and the release all run on one dedicated thread
            CodeGenPackage? cachedPackage = await Tasks.Task.Factory.StartNew(
                () =>
                {
                    using Mutex packageAccessMutex = new Mutex(true, "fcg-" + installDirective, out bool mutexCreated);

                    // if we did not create the mutex, we need to wait for it
                    if (!mutexCreated &&
                        !packageAccessMutex.WaitOne(_packageAccessTimeout))
                    {
                        _logger.LogSynchronizationFailed(installDirective);
                        throw new Exception($"Failed to access the synchronization object for {installDirective}");
                    }

                    try
                    {
                        return _packageSource.GetOrInstallAsync(installDirective).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        packageAccessMutex.ReleaseMutex();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);

            if ((cachedPackage is null) ||
                string.IsNullOrEmpty(cachedPackage.Identity.Id) ||
                string.IsNullOrEmpty(cachedPackage.Identity.Version))
            {
                // failed to install
                throw new Exception($"Failed to install package {installDirective} as requested by {inputDirective}");
            }

            packageDirective = packageDirective with
            {
                PackageId = cachedPackage.Identity.Id,
                ResolvedVersion = cachedPackage.Identity.Version,
            };

            // skip if we have already loaded this package
            if (definitions.TryGetManifest(cachedPackage.Identity, out _))
            {
                _logger.LogSkipMessage($"Skipping already loaded dependency: {packageDirective.NpmDirective}");
                continue;
            }

            string resolvedDirective = cachedPackage.Identity.Id + "@" + cachedPackage.Identity.Version;

            CodeGenPackageManifest manifest = cachedPackage.Manifest;

            // check to see if we have a restricted FHIR version and need to filter
            if ((!string.IsNullOrEmpty(requestedFhirVersion)) &&
                (definitions.FhirSequence != FhirReleases.FhirSequenceCodes.Unknown) &&
                (manifest.FhirVersions.FirstOrDefault() is string manifestFhirVersion) &&
                !string.IsNullOrEmpty(manifestFhirVersion) &&
                (definitions.FhirSequence != FhirReleases.FhirVersionToSequence(manifestFhirVersion)))
            {
                _logger.LogPackageFhirMismatch(packageDirective.AnyDirective, manifestFhirVersion, fhirVersion);

                // check for NOT having a version already specified
                if (!packageDirective.IsGuideWithFhirSuffix)
                {
                    string requiredRLiteral = definitions.FhirSequence.ToRLiteral().ToLowerInvariant();
                    string desiredMoniker = $"{packageDirective.PackageId}.{requiredRLiteral}@{packageDirective.ResolvedVersion}";

                    CodeGenPackageDirective desiredDirective = _packageSource.Parse(desiredMoniker);

                    await LoadPackages([desiredMoniker], definitions, requestedFhirVersion);
                    if (definitions.TryGetManifest(desiredDirective.PackageId ?? string.Empty, desiredDirective.RequestedVersion ?? string.Empty, out _))
                    {
                        _logger.LogPackageSubstitutionSuccess(desiredMoniker, resolvedDirective);
                    }
                    else
                    {
                        _logger.LogPackageSubstitutionFailure(resolvedDirective);
                    }
                }

                // whether it loaded or not, we cannot do more this pass
                continue;
            }

            //// check to see if this is a different FHIR version from what we expect
            //if ((definitions.FhirSequence != FhirReleases.FhirSequenceCodes.Unknown) &&
            //    (manifest.GetFhirVersion() is string manifestFhirVersion) &&
            //    !string.IsNullOrEmpty(manifestFhirVersion) &&
            //    (definitions.FhirSequence != FhirReleases.FhirVersionToSequence(manifestFhirVersion)))
            //{
            //    Console.WriteLine($"Package {packageDirective.Moniker} FHIR version mismatch: {manifest?.GetFhirVersion()} != {fhirVersion}, attempting to resolve...");

            //    string requiredRLiteral = definitions.FhirSequence.ToRLiteral().ToLowerInvariant();

            //    string desiredMoniker = $"{packageDirective.Name}.{requiredRLiteral}@{packageDirective.Version}";

            //    await LoadPackages([desiredMoniker], definitions);

            //    if (!definitions.Manifests.ContainsKey(desiredMoniker))
            //    {
            //        throw new Exception($"Package {packageDirective.Moniker} FHIR version mismatch: {manifest?.GetFhirVersion()} != {fhirVersion} and could not be resolved!");
            //    }
            //}

            // flag we are tracking this package
            definitions.AddManifest(cachedPackage.Identity, manifest);

            if (string.IsNullOrEmpty(definitions.MainPackageId) || (definitions.Name == manifest.Name))
            {
                definitions.MainPackageId = manifest.Name;
                definitions.MainPackageVersion = manifest.Version;
                definitions.MainPackageCanonical = manifest.CanonicalUrl
                    ?? throw new Exception($"Package manifest for {resolvedDirective} does not contain a canonical URL");
            }

            string? packageFhirVersionLiteral = manifest.FhirVersions.FirstOrDefault();

            // update the collection FHIR version based on the first package we come across with one
            if (string.IsNullOrEmpty(definitions.FhirVersionLiteral) && (!string.IsNullOrEmpty(packageFhirVersionLiteral)))
            {
                definitions.FhirVersionLiteral = packageFhirVersionLiteral!;

                definitions.FhirSequence = FhirReleases.FhirVersionToSequence(packageFhirVersionLiteral!);

                definitions.FhirVersion = definitions.FhirSequence switch
                {
                    FhirReleases.FhirSequenceCodes.Unknown => null,
                    FhirReleases.FhirSequenceCodes.DSTU2 => FHIRVersion.N1_0,
                    FhirReleases.FhirSequenceCodes.STU3 => FHIRVersion.N3_0,
                    FhirReleases.FhirSequenceCodes.R4 => FHIRVersion.N4_0,
                    FhirReleases.FhirSequenceCodes.R4B => FHIRVersion.N4_3,
                    FhirReleases.FhirSequenceCodes.R5 => FHIRVersion.N5_0,
                    FhirReleases.FhirSequenceCodes.R6 => FHIRVersion.N6_0,
                    _ => null,
                };
            }

            FhirReleases.FhirSequenceCodes packageFhirVersion = string.IsNullOrEmpty(packageFhirVersionLiteral)
                ? definitions.FhirSequence
                : FhirReleases.FhirVersionToSequence(packageFhirVersionLiteral ?? string.Empty);

            CreateConverterIfRequired(definitions.FhirSequence);

            // if we are resolving dependencies, check them now
            if (resolveDependencies && manifest.Dependencies.Any())
            {
                await LoadPackages(manifest.Dependencies.Select(kvp => $"{kvp.Key}@{kvp.Value}"), definitions, requestedFhirVersion);
                _logger.LogPackageDependenciesResolved(resolvedDirective);
            }
            else
            {
                _logger.LogPackageLoading(resolvedDirective);
            }

            // grab the contents of our package
            CodeGenPackageIndex packageIndex = cachedPackage.Index;
            string packageDirectory = cachedPackage.ContentPath;

            if (!Directory.Exists(packageDirectory))
            {
                throw new Exception($"Package directory {packageDirectory} does not exist!");
            }

            if (string.IsNullOrEmpty(packageDirectory))
            {
                throw new Exception("Package directory is empty");
            }

            if (!(packageIndex.Files?.Any() ?? false))
            {
                throw new Exception("Package contents are empty");
            }

            string packageRootDirectory = Path.Combine(packageDirectory, "..");

            definitions.AddContentListing(cachedPackage.Identity, packageIndex);

            // create an dictionary of indexes we are going to load - note that we are essentially traversing twice, but that is better than projecting each time
            List<int>[] sortedFileIndexes = new List<int>[_sortedLoadOrder.Length];

            for (int i = 0; i < sortedFileIndexes.Length; i++)
            {
                sortedFileIndexes[i] = [];
            }

            // traverse our files
            int fileIndex = 0;
            foreach (CodeGenPackageIndexEntry resourceListing in packageIndex.Files)
            {
                int loadIndex = Array.IndexOf(_sortedLoadOrder, resourceListing.ResourceType);
                if (loadIndex < 0)
                {
                    fileIndex++;
                    continue;
                }

                // add this index to the list of indexes for this type
                sortedFileIndexes[loadIndex].Add(fileIndex++);
            }

            // iterate over our sorted list of indexes
            for (int i = 0; i < _sortedLoadOrder.Length; i++)
            {
                string rt = _sortedLoadOrder[i];

                Type? netType = Hl7.Fhir.Model.ModelInfo.GetTypeForFhirType(rt);

                if (netType is null)
                {
                    if (rt == "Conformance")
                    {
                        netType = typeof(CapabilityStatement);
                    }
                    else
                    {
                        throw new Exception($"Failed to find .NET type for FHIR type {rt}");
                    }
                }

                // check to see if we want to load this type
                switch (rt)
                {
                    case "CodeSystem":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.CodeSystem))
                        {
                            continue;
                        }
                        break;

                    case "ValueSet":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.ValueSet))
                        {
                            continue;
                        }
                        break;

                    case "StructureDefinition":
                        // note: structure definitions can be one of several types and need to be filtered again after parsing
                        if (artifactFilter.Contains(FhirArtifactClassEnum.PrimitiveType) ||
                            artifactFilter.Contains(FhirArtifactClassEnum.ComplexType) ||
                            artifactFilter.Contains(FhirArtifactClassEnum.Resource) ||
                            artifactFilter.Contains(FhirArtifactClassEnum.Extension) ||
                            artifactFilter.Contains(FhirArtifactClassEnum.Profile) ||
                            artifactFilter.Contains(FhirArtifactClassEnum.LogicalModel) ||
                            artifactFilter.Contains(FhirArtifactClassEnum.Interface))
                        {
                            break;
                        }
                        continue;

                    case "SearchParameter":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.SearchParameter))
                        {
                            continue;
                        }
                        break;

                    case "OperationDefinition":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.Operation))
                        {
                            continue;
                        }
                        break;

                    case "Conformance":
                    case "CapabilityStatement":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.CapabilityStatement))
                        {
                            continue;
                        }
                        break;

                    case "ImplementationGuide":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.ImplementationGuide))
                        {
                            continue;
                        }
                        break;

                    case "CompartmentDefinition":
                        if (!artifactFilter.Contains(FhirArtifactClassEnum.Compartment))
                        {
                            continue;
                        }
                        break;
                }

                foreach (int fi in sortedFileIndexes[i])
                {
                    CodeGenPackageIndexEntry pFile = packageIndex.Files[fi];

                    if (pFile.Filename is null)
                    {
                        continue;
                    }

                    // load the file
                    string path = pFile.RelativePath is not null
                        ? Path.Combine(packageRootDirectory, pFile.RelativePath)
                        : Path.Combine(packageDirectory, pFile.Filename);

                    if (!File.Exists(path))
                    {
                        if (pFile.Filename.StartsWith("ig-") && pFile.Filename.EndsWith(".json"))
                        {
                            // ignore IG artifacts that do not exist - issue in IG publisher for IGs that target multiple FHIR versions
                            continue;
                        }

                        throw new Exception($"Listed file {packageDirective.AnyDirective}:{pFile.Filename} does not exist (looking for '{path}')");
                    }

                    string fileExtension = Path.GetExtension(pFile.Filename);

                    object? r;

                    switch (definitions.FhirSequence)
                    {
                        case FhirReleases.FhirSequenceCodes.DSTU2:
                            {
                                r = ParseContents20(fileExtension, path: path);
                            }
                            break;

                        case FhirReleases.FhirSequenceCodes.STU3:
                            {
                                r = ParseContents30(fileExtension, path: path);
                            }
                            break;

                        case FhirReleases.FhirSequenceCodes.R4:
                        case FhirReleases.FhirSequenceCodes.R4B:
                            {
                                r = ParseContents43(fileExtension, path: path);
                            }
                            break;

                        default:
                        case FhirReleases.FhirSequenceCodes.R5:
                            {
                                if (_jsonModel == LoaderOptions.JsonDeserializationModel.SystemTextJson)
                                {
                                    r = ParseContentsSystemTextStream(fileExtension, netType, path: path);
                                }
                                else
                                {
                                    r = ParseContentsPoco(fileExtension, path);
                                }
                            }
                            break;
                    }

                    if (r is null)
                    {
                        throw new Exception($"Failed to parse {rt} {packageDirective.AnyDirective}:{pFile.Filename}");
                    }

                    // filter for structure types we are not processing
                    if (filterStructureDefinitions && r is StructureDefinition sd)
                    {
                        if (!artifactFilter.Contains(sd.cgArtifactClass()))
                        {
                            // skip this artifact
                            continue;
                        }
                    }

                    definitions.AddResource(r, packageFhirVersion, manifest.Name, manifest.Version, manifest.CanonicalUrl!);
                }
            }

            // check for loading canonical examples
            if (_rootConfiguration.LoadCanonicalExamples)
            {
                string exampleDir = Path.Combine(packageDirectory, "examples");

                if (Directory.Exists(exampleDir))
                {
                    // get files in the directory
                    string[] files = Directory.GetFiles(exampleDir, "*.json", SearchOption.TopDirectoryOnly);
                    foreach (string path in files)
                    {
                        string fileExtension = Path.GetExtension(path);

                        object? r;

                        switch (definitions.FhirSequence)
                        {
                            case FhirReleases.FhirSequenceCodes.DSTU2:
                                {
                                    r = ParseContents20(fileExtension, path: path);
                                }
                                break;

                            case FhirReleases.FhirSequenceCodes.STU3:
                                {
                                    r = ParseContents30(fileExtension, path: path);
                                }
                                break;

                            case FhirReleases.FhirSequenceCodes.R4:
                            case FhirReleases.FhirSequenceCodes.R4B:
                                {
                                    r = ParseContents43(fileExtension, path: path);
                                }
                                break;

                            default:
                            case FhirReleases.FhirSequenceCodes.R5:
                                {
                                    r = ParseContentsPoco(fileExtension, path);
                                }
                                break;
                        }

                        if (r is null)
                        {
                            throw new Exception($"Failed to parse '{path}'");
                        }

                        definitions.AddResource(r, _defaultFhirVersion, manifest.Name, manifest.Version, manifest.CanonicalUrl!);
                    }
                }
            }

            // check to see if this package is a 'core' FHIR package to add missing contents
            if ((manifest.PackageType == "core") ||
                (manifest.PackageType == "fhir.core") ||
                FhirPackageUtils.PackageIsFhirRelease(packageDirective.PackageId))
            {
                AddMissingCoreSearchParameters(definitions!, manifest.Name, manifest.Version);
                AddAllInteractionParameters(definitions!);
                AddSearchResultParameters(definitions!);
            }
        }

        // generate any missing Snapshots - note this has to be done after all resources are loaded so dependencies can be resolved
        if (definitions != null)
        {
            //_ = await definitions.TryGenerateMissingSnapshots();
            _ = definitions.TryGenerateMissingSnapshots().Result;
        }

        // reconcile inheritance of elements
        if (definitions != null)
        {
            _ = definitions.TryReconcileElementInheritance();
        }

        // check for DSTU2 - need to reconcile profile snapshots that are missing elements
        if (definitions?.FhirSequence == FhirReleases.FhirSequenceCodes.DSTU2)
        {
            _ = definitions.TryReconcileProfileSnapshots();
        }

        return definitions;
    }

    public object? ParseContents50(string format, Type? resourceType = null, string path = "", string content = "")
    {
        switch (format.ToLowerInvariant())
        {
            case ".json":
            case "json":
            case "fhir+json":
            case "application/json":
            case "application/fhir+json":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllText(path);
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirJsonNode.Parse(content);

                    if (resourceType != null)
                    {
                        return sn.ToPoco(resourceType);
                    }

                    return sn.ToPoco();
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("R5", "JSON", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            case ".xml":
            case "xml":
            case "fhir+xml":
            case "application/xml":
            case "application/fhir+xml":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllText(path);
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirXmlNode.Parse(content);
                    if (resourceType != null)
                    {
                        return sn.ToPoco(resourceType);
                    }

                    return sn.ToPoco();
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("R5", "XML", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            default:
                {
                    _logger.LogInvalidFormat("R5", format);
                    return null;
                }
        }
    }

    /// <summary>Parse contents.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="format"> Describes the format to use.</param>
    /// <param name="path">   (Optional) Full pathname of the file.</param>
    /// <param name="content">(Optional) The content.</param>
    /// <returns>A TResource?</returns>
    public object? ParseContentsPoco(string format, string path = "", string content = "")
    {
        switch (format.ToLowerInvariant())
        {
            case ".json":
            case "json":
            case "fhir+json":
            case "application/json":
            case "application/fhir+json":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
#if NET8_0_OR_GREATER
                        content = File.ReadAllTextAsync(path).Result;
#else
                        content = File.ReadAllText(path);
#endif
                    }

                    // always use lenient parsing
                    Resource parsed = _jsonParser.DeserializeResource(content);
                    return parsed;
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("POCO", "JSON", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            case ".xml":
            case "xml":
            case "fhir+xml":
            case "application/xml":
            case "application/fhir+xml":
#if DISABLE_XML
                throw new Exception("XML is currently disabled");
#else
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllTextAsync(path).Result;
                    }

                    // always use lenient parsing
                    Resource parsed = _xmlParser.DeserializeResource(content);
                    return parsed;
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("POCO", "XML", ex.Message, ex.InnerException?.Message);
                    return null;
                }
#endif
            default:
                {
                    _logger.LogInvalidFormat("POCO", format);
                    return null;
                }
        }
    }

    /// <summary>Parse contents of in the FHIR R4B format.</summary>
    /// <typeparam name="TResource">Type of the resource.</typeparam>
    /// <param name="format">Describes the format to use.</param>
    /// <param name="path">  Full pathname of the file.</param>
    /// <returns>An asynchronous result that yields a TResource?</returns>
    public object? ParseContents43(string format, string path = "", string content = "")
    {
        switch (format.ToLowerInvariant())
        {
            case ".json":
            case "json":
            case "fhir+json":
            case "application/json":
            case "application/fhir+json":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllText(path);
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirJsonNode.Parse(content);
                    return _converter_43_50!.Convert(sn);
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("R4B", "JSON", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            case ".xml":
            case "xml":
            case "fhir+xml":
            case "application/xml":
            case "application/fhir+xml":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllText(path);
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirXmlNode.Parse(content);
                    return _converter_43_50!.Convert(sn);
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("R4B", "XML", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            default:
                {
                    _logger.LogInvalidFormat("R4B", format);
                    return null;
                }
        }
    }

    public object? ParseContents30(string format, string path = "", string content = "")
    {
        switch (format.ToLowerInvariant())
        {
            case ".json":
            case "json":
            case "fhir+json":
            case "application/json":
            case "application/fhir+json":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllText(path);
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirJsonNode.Parse(content);
                    return _converter_30_50!.Convert(sn);
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("STU3", "JSON", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            case ".xml":
            case "xml":
            case "fhir+xml":
            case "application/xml":
            case "application/fhir+xml":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
#if NET8_0_OR_GREATER
                        content = File.ReadAllTextAsync(path).Result;
#else
                        content = File.ReadAllText(path);
#endif
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirXmlNode.Parse(content);
                    return _converter_30_50!.Convert(sn);
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("STU3", "XML", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            default:
                {
                    _logger.LogInvalidFormat("STU3", format);
                    return null;
                }
        }
    }

    public object? ParseContents20(string format, string path = "", string content = "")
    {
        switch (format.ToLowerInvariant())
        {
            case ".json":
            case "json":
            case "fhir+json":
            case "application/json":
            case "application/fhir+json":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
#if NET8_0_OR_GREATER
                        content = File.ReadAllTextAsync(path).Result;
#else
                        content = File.ReadAllText(path);
#endif
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirJsonNode.Parse(content);
                    return _converter_20_50!.Convert(sn);
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("DSTU2", "JSON", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            case ".xml":
            case "xml":
            case "fhir+xml":
            case "application/xml":
            case "application/fhir+xml":
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
#if NET8_0_OR_GREATER
                        content = File.ReadAllTextAsync(path).Result;
#else
                        content = File.ReadAllText(path);
#endif
                    }

                    Hl7.Fhir.ElementModel.ISourceNode sn = FhirXmlNode.Parse(content);
                    return _converter_20_50!.Convert(sn);
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("DSTU2", "XML", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            default:
                {
                    _logger.LogInvalidFormat("DSTU2", format);
                    return null;
                }
        }
    }

    /// <summary>Parse the contents of a resource in the specified format.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="format">      The format of the content.</param>
    /// <param name="resourceType">Type of the resource.</param>
    /// <param name="path">        (Optional) Full pathname of the file.</param>
    /// <param name="content">     (Optional) The content.</param>
    /// <returns>
    /// The parsed resource of type <typeparamref name="TResource"/> or null if parsing fails.
    /// </returns>
    public object? ParseContentsSystemTextStream(string format, Type resourceType, string path = "", string content = "")
    {
        switch (format.ToLowerInvariant())
        {
            case ".json":
            case "json":
            case "fhir+json":
            case "application/json":
            case "application/fhir+json":
                try
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        return JsonSerializer.Deserialize(content, resourceType, _jsonOptions);
                    }

                    using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        return JsonSerializer.Deserialize(fs, resourceType, _jsonOptions);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("Stream", "JSON", ex.Message, ex.InnerException?.Message);
                    return null;
                }

            case ".xml":
            case "xml":
            case "fhir+xml":
            case "application/xml":
            case "application/fhir+xml":
#if DISABLE_XML
                throw new Exception("XML is currently disabled");
#else
                try
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        content = File.ReadAllText(path);
                    }

                    // always use lenient parsing
                    Resource parsed = _xmlParser.DeserializeResource(content);
                    return parsed;
                }
                catch (Exception ex)
                {
                    _logger.LogParseError("Stream", "XML", ex.Message, ex.InnerException?.Message);
                    return null;
                }
#endif
            default:
                {
                    _logger.LogInvalidFormat("Stream", format);
                    return null;
                }
        }
    }

    /// <summary>Error handler as mitigation for https://github.com/FirelyTeam/firely-net-sdk/issues/2701. </summary>
    /// <param name="reader">           [in,out] The reader.</param>
    /// <param name="targetType">       Type of the target.</param>
    /// <param name="originalValue">    The original value.</param>
    /// <param name="originalException">Details of the exception.</param>
    /// <returns>A Tuple.</returns>
    public static (object?, FhirJsonException?) LocalPrimitiveParseHandler(
        ref Utf8JsonReader reader,
        Type targetType,
        object? originalValue,
        FhirJsonException originalException)
    {
        if (targetType == typeof(long))
        {
            if (originalValue is long ol)
            {
                return (ol, null);
            }

            if (originalValue is string s)
            {
                if (long.TryParse(s, out long l))
                {
                    return (l, null);
                }
            }
        }

        return (originalValue, originalException);
    }


    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="PackageLoader"/>
    /// and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to
    ///  release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
                if (_converter_43_50 != null)
                {
                    lock (_convertLockObject)
                    {
                        _converter_43_50 = null;
                    }
                }

                if (_converter_30_50 != null)
                {
                    lock (_convertLockObject)
                    {
                        _converter_30_50 = null;
                    }
                }

                if (_converter_20_50 != null)
                {
                    lock (_convertLockObject)
                    {
                        _converter_20_50 = null;
                    }
                }

                _packageSource.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            _disposedValue = true;
        }
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged
    /// resources.
    /// </summary>
    void IDisposable.Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
