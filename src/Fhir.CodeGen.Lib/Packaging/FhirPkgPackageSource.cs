// <copyright file="FhirPkgPackageSource.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Lib.Configuration;
using FhirPkg;
using FhirPkg.Indexing;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fhir.CodeGen.Lib.Packaging;

/// <summary>Supplies installed FHIR packages via the <c>fhir-pkg-lib</c> package manager.</summary>
/// <remarks>
/// This is the only type in the solution permitted to name a <c>FhirPkg</c> type. Everything it
/// returns is expressed with the types in this namespace.
/// </remarks>
internal sealed class FhirPkgPackageSource : ICodeGenPackageSource
{
    /// <summary>(Immutable) Options used when reading a cached <c>package.json</c> directly.</summary>
    private static readonly JsonSerializerOptions _rawManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ServiceProvider _serviceProvider;
    private readonly IFhirPackageManager _packageManager;
    private readonly IFhirPackageResourceManager _resourceManager;
    private readonly CodeGenRegistryConfiguration _registryConfiguration;
    private readonly ILogger _logger;

    private bool _disposedValue;

    /// <summary>Initializes a new instance of the <see cref="FhirPkgPackageSource"/> class.</summary>
    /// <param name="config">       The resolved root configuration.</param>
    /// <param name="loggerFactory">The logger factory to hand to the package manager.</param>
    public FhirPkgPackageSource(ConfigRoot config, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<FhirPkgPackageSource>();

        CodeGenRegistryConfiguration registries = ResolveRegistryConfiguration(config);
        _registryConfiguration = registries;

        if (registries.RegistriesDisabled)
        {
            _logger.LogInformation("Package registries are disabled; packages will be resolved from the cache only.");
        }

        ServiceCollection services = new();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddFhirPackageManagement(options =>
        {
            options.CachePath = config.FhirCacheDirectory;

            options.IncludeCiBuilds = registries.IncludeCiBuilds;
            options.IncludeHl7WebsiteFallback = registries.IncludeHl7WebsiteFallback;

            foreach (CodeGenRegistryEndpoint endpoint in registries.Endpoints)
            {
                options.Registries.Add(ToRegistryEndpoint(endpoint));
            }
        });

        _serviceProvider = services.BuildServiceProvider();
        _packageManager = _serviceProvider.GetRequiredService<IFhirPackageManager>();
        _resourceManager = _serviceProvider.GetRequiredService<IFhirPackageResourceManager>();
    }

    /// <summary>Resolves the registry chain described by the root configuration.</summary>
    /// <remarks>
    /// Pure: performs no I/O and builds no services, so the decision can be asserted directly.
    /// </remarks>
    /// <param name="config">The resolved root configuration.</param>
    /// <returns>The resolved registry configuration.</returns>
    internal static CodeGenRegistryConfiguration ResolveRegistryConfiguration(ConfigRoot config)
    {
        List<CodeGenRegistryEndpoint> endpoints = [];

        // `FhirPrimary` + `FhirSecondary` reproduces the shipped default chain; the HL7 website
        // client is appended by `IncludeHl7WebsiteFallback` and must not be seeded twice
        if (config.UseOfficialRegistries)
        {
            endpoints.Add(ToCodeGenEndpoint(RegistryEndpoint.FhirPrimary));
            endpoints.Add(ToCodeGenEndpoint(RegistryEndpoint.FhirSecondary));
        }

        foreach (string url in config.AdditionalFhirRegistryUrls)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                endpoints.Add(new CodeGenRegistryEndpoint(url, CodeGenRegistryKind.FhirNpm));
            }
        }

        foreach (string url in config.AdditionalNpmRegistryUrls)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                endpoints.Add(new CodeGenRegistryEndpoint(url, CodeGenRegistryKind.Npm));
            }
        }

        return new CodeGenRegistryConfiguration()
        {
            Endpoints = endpoints,
            IncludeCiBuilds = config.UseOfficialRegistries,
            IncludeHl7WebsiteFallback = config.UseOfficialRegistries,
        };
    }

    /// <summary>Projects a package-library endpoint onto the record this project owns.</summary>
    /// <param name="endpoint">The package-library endpoint.</param>
    /// <returns>The projected endpoint.</returns>
    private static CodeGenRegistryEndpoint ToCodeGenEndpoint(RegistryEndpoint endpoint) => new(
        endpoint.Url,
        endpoint.Type switch
        {
            RegistryType.FhirCiBuild => CodeGenRegistryKind.FhirCiBuild,
            RegistryType.FhirHttp => CodeGenRegistryKind.FhirHttp,
            RegistryType.Npm => CodeGenRegistryKind.Npm,
            _ => CodeGenRegistryKind.FhirNpm,
        });

    /// <summary>Projects a resolved endpoint back onto the package library's type.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <returns>The package-library endpoint.</returns>
    private static RegistryEndpoint ToRegistryEndpoint(CodeGenRegistryEndpoint endpoint) => new()
    {
        Url = endpoint.Url,
        Type = endpoint.Kind switch
        {
            CodeGenRegistryKind.FhirCiBuild => RegistryType.FhirCiBuild,
            CodeGenRegistryKind.FhirHttp => RegistryType.FhirHttp,
            CodeGenRegistryKind.Npm => RegistryType.Npm,
            _ => RegistryType.FhirNpm,
        },
    };

    /// <summary>Parses a package directive into its component parts.</summary>
    /// <param name="directive">The package directive to parse.</param>
    /// <returns>The parsed directive.</returns>
    public CodeGenPackageDirective Parse(string directive)
    {
        string raw = directive.Replace('#', '@');

        try
        {
            PackageDirective parsed = PackageDirective.Parse(directive);

            return new CodeGenPackageDirective()
            {
                RawDirective = raw,
                PackageId = parsed.PackageId,
                RequestedVersion = parsed.RequestedVersion,
                ResolvedVersion = parsed.ResolvedVersion?.ToString()
                    ?? ((parsed.VersionType == VersionType.Exact) ? parsed.RequestedVersion : null),
                IsExactVersion = parsed.VersionType == VersionType.Exact,
                IsGuideWithFhirSuffix = parsed.NameType == PackageNameType.GuideWithFhirSuffix,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not parse package directive {directive}: {message}", directive, ex.Message);
            return new CodeGenPackageDirective() { RawDirective = raw };
        }
    }

    /// <summary>Resolves a package directive, installing the package if it is not already present.</summary>
    /// <param name="directive">        The package directive to resolve.</param>
    /// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
    /// <returns>The installed package, or null when it could not be resolved.</returns>
    public async Task<CodeGenPackage?> GetOrInstallAsync(string directive, CancellationToken cancellationToken = default)
    {
        // dependency traversal is driven by the loader, not by the package manager
        InstallOptions options = new() { IncludeDependencies = false };

        PackageRecord? record = null;

        // an empty registry list is indistinguishable from the default chain to the package
        // library, so cache-only resolution has to skip the registry-backed install entirely
        if (!_registryConfiguration.RegistriesDisabled)
        {
            try
            {
                record = await _packageManager.InstallAsync(directive, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not resolve {directive} from a registry: {message}", directive, ex.Message);
            }
        }

        // the upstream range grammar rejects the shorter wildcard forms this project has always
        // accepted (`4.x`), which the replaced disk cache resolved locally
        record ??= await ResolveFromCacheAsync(directive, options, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        PackageIndex? index = record.Index;

        // packages carrying a pre-schema-v2 `.index.json` come back unindexed; ask for one
        if ((index?.Files is null) || (index.Files.Count == 0))
        {
            try
            {
                index = await _resourceManager.IndexPackageAsync(record.Reference, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not index package {directive}: {message}", directive, ex.Message);
            }
        }

        return Project(record, index);
    }

    /// <summary>Resolves a directive the registries could not against the newest satisfying cached package.</summary>
    /// <param name="directive">        The package directive to resolve.</param>
    /// <param name="options">          The install options to reuse.</param>
    /// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
    /// <returns>The installed package record, or null when the cache holds nothing satisfying.</returns>
    private async Task<PackageRecord?> ResolveFromCacheAsync(string directive, InstallOptions options, CancellationToken cancellationToken)
    {
        CodeGenPackageDirective parsed = Parse(directive);

        if (string.IsNullOrEmpty(parsed.PackageId) ||
            string.IsNullOrEmpty(parsed.RequestedVersion))
        {
            return null;
        }

        IReadOnlyList<PackageRecord> cached;

        try
        {
            cached = await _packageManager.ListCachedSummariesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not list cached packages while resolving {directive}: {message}", directive, ex.Message);
            return null;
        }

        string? best = null;

        foreach (PackageRecord candidate in cached)
        {
            string? candidateName = candidate.Reference.Name;
            string? candidateVersion = candidate.Reference.Version;

            if (string.IsNullOrEmpty(candidateName) ||
                string.IsNullOrEmpty(candidateVersion) ||
                !string.Equals(candidateName, parsed.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !PackageVersionMatcher.Satisfies(candidateVersion, parsed.RequestedVersion!))
            {
                continue;
            }

            if ((best is null) || (PackageVersionMatcher.Compare(candidateVersion, best) > 0))
            {
                best = candidateVersion;
            }
        }

        if (best is null)
        {
            return null;
        }

        _logger.LogInformation(
            "Resolved {directive} to {resolved} from the package cache",
            directive,
            $"{parsed.PackageId}@{best}");

        // the version was just found on disk, so this short-circuits to the cache; guard it anyway
        // so a surprise registry attempt cannot turn a cache hit into a thrown exception
        try
        {
            return await _packageManager.InstallAsync($"{parsed.PackageId}@{best}", options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load cached package {directive}: {message}", $"{parsed.PackageId}@{best}", ex.Message);
            return null;
        }
    }

    /// <summary>Projects an installed package record onto the types this project owns.</summary>
    /// <param name="record">The record returned by the package manager.</param>
    /// <param name="index"> The resolved package index.</param>
    /// <returns>The projected package.</returns>
    private static CodeGenPackage Project(PackageRecord record, PackageIndex? index)
    {
        string rootPath = record.DirectoryPath;

        string contentPath = string.IsNullOrEmpty(record.ContentPath)
            ? Path.Combine(rootPath, "package")
            : record.ContentPath;

        CachePackageManifest? raw = ReadRawManifest(Path.Combine(contentPath, "package.json"))
            ?? ReadRawManifest(Path.Combine(rootPath, "package", "package.json"));

        // the record's content path is authoritative when it exists; otherwise honor `directories.lib`
        if (!Directory.Exists(contentPath) &&
            (raw is not null) &&
            raw.Directories.TryGetValue("lib", out string? libDirectory) &&
            !string.IsNullOrEmpty(libDirectory))
        {
            contentPath = Path.Combine(rootPath, libDirectory);
        }

        PackageIdentity identity = new(
            string.IsNullOrEmpty(record.Reference.Name) ? record.Manifest.Name : record.Reference.Name,
            string.IsNullOrEmpty(record.Reference.Version) ? record.Manifest.Version : record.Reference.Version);

        return new CodeGenPackage()
        {
            Identity = identity,
            Manifest = ProjectManifest(record.Manifest, raw),
            Index = ProjectIndex(index),
            ContentPath = contentPath,
            RootPath = rootPath,
        };
    }

    /// <summary>Projects a package manifest, filling members the package manager does not surface.</summary>
    /// <param name="manifest">The manifest returned by the package manager.</param>
    /// <param name="raw">     The cached <c>package.json</c>, when it could be read.</param>
    /// <returns>The projected manifest.</returns>
    private static CodeGenPackageManifest ProjectManifest(PackageManifest manifest, CachePackageManifest? raw)
    {
        List<string> fhirVersions = [.. manifest.FhirVersions ?? []];

        // `fhir-version-list` is not bound by the package manager, and core packages rely on it
        if (fhirVersions.Count == 0)
        {
            fhirVersions = [.. raw?.AllFhirVersions ?? []];
        }

        if ((fhirVersions.Count == 0) && (manifest.InferredFhirRelease is FhirRelease inferred))
        {
            string? inferredLiteral = FhirReleaseMapping.ToVersionString(inferred);

            if (!string.IsNullOrEmpty(inferredLiteral))
            {
                fhirVersions = [inferredLiteral];
            }
        }

        IReadOnlyDictionary<string, string> dependencies =
            (manifest.Dependencies is { Count: > 0 })
                ? manifest.Dependencies
                : raw?.Dependencies ?? [];

        return new CodeGenPackageManifest()
        {
            Name = manifest.Name,
            Version = manifest.Version,
            CanonicalUrl = manifest.Canonical ?? raw?.CanonicalUrl,

            // the package manager binds `homepage`; this project has always used `url`
            WebPublicationUrl = (raw is null) ? manifest.Homepage : raw.WebPublicationUrl,
            Title = manifest.Title ?? raw?.Title,
            Description = manifest.Description ?? raw?.Description,
            PackageType = manifest.Type ?? raw?.Type,
            FhirVersions = fhirVersions,
            Dependencies = dependencies,
        };
    }

    /// <summary>Projects a package index onto the content listing this project owns.</summary>
    /// <param name="index">The index returned by the package manager.</param>
    /// <returns>The projected content listing.</returns>
    private static CodeGenPackageIndex ProjectIndex(PackageIndex? index) => new()
    {
        // the package manager does not record a per-file relative path; consumers fall back to the filename
        Files = index?.Files?.Select(f => new CodeGenPackageIndexEntry()
        {
            Filename = f.Filename,
            RelativePath = null,
            ResourceType = f.ResourceType,
        }).ToList() ?? [],
    };

    /// <summary>Reads a cached <c>package.json</c> directly.</summary>
    /// <param name="path">Full path to the manifest file.</param>
    /// <returns>The deserialized manifest, or null when it is missing or unreadable.</returns>
    private static CachePackageManifest? ReadRawManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CachePackageManifest>(File.ReadAllText(path), _rawManifestOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposedValue)
        {
            return;
        }

        if (disposing)
        {
            _serviceProvider.Dispose();
        }

        _disposedValue = true;
    }

    /// <summary>Releases the resources held by this package source.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
