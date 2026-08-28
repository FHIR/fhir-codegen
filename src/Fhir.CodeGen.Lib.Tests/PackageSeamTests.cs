// <copyright file="PackageSeamTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shouldly;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Lib.Packaging;

namespace Fhir.CodeGen.Lib.Tests;

/// <summary>Tests for this repository's package consumption seam.</summary>
/// <remarks>
/// These cover the boundary this repository owns, not the package library's cache, registry, or
/// version internals. They read from the shared FHIR package cache and contact no remote registry;
/// the one exception is <see cref="CachedPackageResolvesWhenRegistryUnreachable"/>, which points at
/// a refused loopback address on purpose.
/// </remarks>
public class PackageSeamTests
{
    internal const string? CachePath = null;

    private const string _r4CoreId = "hl7.fhir.r4.core";
    private const string _r4CoreVersion = "4.0.1";
    private const string _r4CoreCanonical = "http://hl7.org/fhir";

    private static async Task<DefinitionCollection> LoadR4(bool resolveDependencies = false)
    {
        PackageLoader loader = new(
            new()
            {
                FhirCacheDirectory = CachePath,
                ResolvePackageDependencies = resolveDependencies,
            },
            new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR4);

        loaded.ShouldNotBeNull();
        return loaded!;
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    internal void PublicSurfaceExposesNoUpstreamPackageTypes()
    {
        Assembly libAssembly = typeof(DefinitionCollection).Assembly;

        List<string> offenders = [];

        static bool IsUpstream(System.Type? t)
        {
            if (t is null)
            {
                return false;
            }

            if (t.Assembly.GetName().Name == "FhirPkg")
            {
                return true;
            }

            return t.IsGenericType && t.GetGenericArguments().Any(IsUpstream);
        }

        foreach (System.Type exported in libAssembly.GetExportedTypes())
        {
            foreach (PropertyInfo p in exported.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsUpstream(p.PropertyType))
                {
                    offenders.Add($"{exported.FullName}.{p.Name} : {p.PropertyType.FullName}");
                }
            }

            foreach (FieldInfo f in exported.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsUpstream(f.FieldType))
                {
                    offenders.Add($"{exported.FullName}.{f.Name} : {f.FieldType.FullName}");
                }
            }

            foreach (MethodInfo m in exported.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsUpstream(m.ReturnType))
                {
                    offenders.Add($"{exported.FullName}.{m.Name} -> {m.ReturnType.FullName}");
                }

                foreach (ParameterInfo parameter in m.GetParameters())
                {
                    if (IsUpstream(parameter.ParameterType))
                    {
                        offenders.Add($"{exported.FullName}.{m.Name}({parameter.Name}) : {parameter.ParameterType.FullName}");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    internal void IdentityRendersAsPackageKeyLiteral()
    {
        PackageIdentity identity = new(_r4CoreId, _r4CoreVersion);

        identity.ToString().ShouldBe("(hl7.fhir.r4.core, 4.0.1)");
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    internal void IdentityRendersDependencyString()
    {
        PackageIdentity identity = new(_r4CoreId, _r4CoreVersion);

        (identity.Id + "@" + identity.Version).ShouldBe("hl7.fhir.r4.core@4.0.1");
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    internal void IdentityKeysAreOrderable()
    {
        PackageIdentity a = new("hl7.fhir.r4.core", "4.0.1");
        PackageIdentity b = new("hl7.fhir.r4.expansions", "4.0.1");

        PackageIdentity[] ordered = [.. new[] { b, a }.Order()];

        ordered[0].ShouldBe(a);
        ordered[1].ShouldBe(b);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task ManifestsAreKeyedByResolvedIdentity()
    {
        DefinitionCollection loaded = await LoadR4();

        PackageIdentity[] coreKeys = [.. loaded.Manifests.Keys.Where(k => k.Id == _r4CoreId)];

        coreKeys.Length.ShouldBe(1);
        coreKeys[0].Version.ShouldBe(_r4CoreVersion);

        loaded.MainPackageId.ShouldBe(_r4CoreId);
        loaded.MainPackageVersion.ShouldBe(_r4CoreVersion);
        loaded.MainPackageCanonical.ShouldBe(_r4CoreCanonical);
    }

    [Theory]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    [InlineData("4.0.1")]
    [InlineData("4.0.x")]
    [InlineData("4.x")]
    [InlineData("*")]
    internal async Task TryGetManifestResolvesExactAndRangeVersions(string version)
    {
        DefinitionCollection loaded = await LoadR4();

        loaded.TryGetManifest(_r4CoreId, version, out CodeGenPackageManifest? manifest).ShouldBeTrue();
        manifest!.Name.ShouldBe(_r4CoreId);
        manifest.Version.ShouldBe(_r4CoreVersion);
    }

    [Theory]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    [InlineData("latest")]
    [InlineData("current")]
    [InlineData("current$main")]
    [InlineData("dev")]
    internal async Task NonSemanticVersionTokensDoNotThrow(string version)
    {
        DefinitionCollection loaded = await LoadR4();

        Should.NotThrow(() => loaded.TryGetManifest(_r4CoreId, version, out _)).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task CorePackageManifestReportsFhirVersion()
    {
        DefinitionCollection loaded = await LoadR4();

        loaded.TryGetManifest(_r4CoreId, _r4CoreVersion, out CodeGenPackageManifest? manifest).ShouldBeTrue();

        manifest!.FhirVersions.ShouldNotBeEmpty();
        FhirReleases.FhirVersionToSequence(manifest.FhirVersions.First()).ShouldBe(FhirReleases.FhirSequenceCodes.R4);

        loaded.FhirSequence.ShouldBe(FhirReleases.FhirSequenceCodes.R4);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task ContentListingProjectsEveryIndexedFile()
    {
        DefinitionCollection loaded = await LoadR4();

        PackageIdentity key = loaded.ContentListings.Keys.First(k => k.Id == _r4CoreId);
        CodeGenPackageIndex listing = loaded.ContentListings[key];

        listing.Files.ShouldNotBeEmpty();
        listing.Files.ShouldAllBe(f => !string.IsNullOrEmpty(f.Filename));

        string indexPath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".fhir",
            "packages",
            $"{_r4CoreId}#{_r4CoreVersion}",
            "package",
            ".index.json");

        if (!File.Exists(indexPath))
        {
            return;
        }

        JsonNode? index = JsonNode.Parse(File.ReadAllText(indexPath));
        int onDisk = index?["files"]?.AsArray().Count ?? 0;

        listing.Files.Count.ShouldBe(onDisk);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task ExpansionPackageIsAutoLoadedAlongsideCore()
    {
        DefinitionCollection loaded = await LoadR4();

        string[] keys = [.. loaded.Manifests.Keys.Select(k => k.Id + "@" + k.Version).Order()];

        keys.ShouldBe(["hl7.fhir.r4.core@4.0.1", "hl7.fhir.r4.expansions@4.0.1"]);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task ShortWildcardDirectiveResolvesFromCache()
    {
        PackageLoader loader = new(
            new() { FhirCacheDirectory = CachePath },
            new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        DefinitionCollection? loaded = await loader.LoadPackages(["hl7.fhir.r4.core#4.x"]);

        loaded.ShouldNotBeNull();
        loaded!.MainPackageId.ShouldBe(_r4CoreId);
        loaded.MainPackageVersion.ShouldBe(_r4CoreVersion);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task DependencyRecursionLoadsDependencies()
    {
        DefinitionCollection withoutDependencies = await LoadR4();
        DefinitionCollection withDependencies = await LoadR4(resolveDependencies: true);

        HashSet<PackageIdentity> baseline = [.. withoutDependencies.Manifests.Keys];
        HashSet<PackageIdentity> expanded = [.. withDependencies.Manifests.Keys];

        expanded.IsSupersetOf(baseline).ShouldBeTrue();
    }

    [Theory]
    [Trait("Category", "PackageSeam")]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    internal void RegistryConfigurationHonorsUseOfficialRegistries(bool useOfficial, bool addFhirUrl, bool addNpmUrl)
    {
        const string fhirUrl = "http://127.0.0.1:1/fhir";
        const string npmUrl = "http://127.0.0.1:1/npm";

        ConfigRoot config = new()
        {
            UseOfficialRegistries = useOfficial,
            AdditionalFhirRegistryUrls = addFhirUrl ? [fhirUrl] : [],
            AdditionalNpmRegistryUrls = addNpmUrl ? [npmUrl] : [],
        };

        CodeGenRegistryConfiguration resolved = FhirPkgPackageSource.ResolveRegistryConfiguration(config);

        resolved.IncludeCiBuilds.ShouldBe(useOfficial);
        resolved.IncludeHl7WebsiteFallback.ShouldBe(useOfficial);

        int officialCount = useOfficial ? 2 : 0;
        int additionalCount = (addFhirUrl ? 1 : 0) + (addNpmUrl ? 1 : 0);

        resolved.Endpoints.Count.ShouldBe(officialCount + additionalCount);

        for (int i = 0; i < officialCount; i++)
        {
            resolved.Endpoints[i].Kind.ShouldBe(CodeGenRegistryKind.FhirNpm);
            resolved.Endpoints[i].Url.ShouldNotBeNullOrWhiteSpace();
        }

        if (addFhirUrl)
        {
            resolved.Endpoints[officialCount].ShouldBe(new CodeGenRegistryEndpoint(fhirUrl, CodeGenRegistryKind.FhirNpm));
        }

        if (addNpmUrl)
        {
            resolved.Endpoints[^1].ShouldBe(new CodeGenRegistryEndpoint(npmUrl, CodeGenRegistryKind.Npm));
        }

        // an empty chain is what actually disables the registries, and only when nothing else re-enables them
        resolved.RegistriesDisabled.ShouldBe(!useOfficial && !addFhirUrl && !addNpmUrl);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task ExactDirectiveResolvesWhenRegistriesAreDisabled()
    {
        PackageLoader loader = new(
            new() { FhirCacheDirectory = CachePath, UseOfficialRegistries = false },
            new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        DefinitionCollection? loaded = await loader.LoadPackages(TestCommon.EntriesR4);

        loaded.ShouldNotBeNull();
        loaded!.MainPackageId.ShouldBe(_r4CoreId);
        loaded.MainPackageVersion.ShouldBe(_r4CoreVersion);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task WildcardDirectiveResolvesWhenRegistriesAreDisabled()
    {
        PackageLoader loader = new(
            new() { FhirCacheDirectory = CachePath, UseOfficialRegistries = false },
            new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        DefinitionCollection? loaded = await loader.LoadPackages(["hl7.fhir.r4.core#4.0.x"]);

        loaded.ShouldNotBeNull();
        loaded!.MainPackageId.ShouldBe(_r4CoreId);
        loaded.MainPackageVersion.ShouldBe(_r4CoreVersion);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task CachedPackageResolvesWhenRegistryUnreachable()
    {
        // the chain is non-empty, so the install really runs and really fails; loopback port 1
        // refuses immediately and needs no DNS
        PackageLoader loader = new(
            new()
            {
                FhirCacheDirectory = CachePath,
                UseOfficialRegistries = false,
                AdditionalFhirRegistryUrls = ["http://127.0.0.1:1/"],
            },
            new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        DefinitionCollection? loaded = await loader.LoadPackages(["hl7.fhir.r4.core#4.0.x"]);

        loaded.ShouldNotBeNull();
        loaded!.MainPackageId.ShouldBe(_r4CoreId);
        loaded.MainPackageVersion.ShouldBe(_r4CoreVersion);
    }

    [Fact]
    [Trait("Category", "PackageSeam")]
    [Trait("DefaultCache", "true")]
    internal async Task DuplicateRequestedDirectiveIsSkippedBeforeInstall()
    {
        // the install runs on a dedicated long-running task, so messages arrive from more than one thread
        ConcurrentQueue<string> sink = new();

        PackageLoader loader = new(
            new()
            {
                FhirCacheDirectory = CachePath,
                UseOfficialRegistries = false,
                LogFactory = LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider(sink))),
            },
            new() { JsonModel = LoaderOptions.JsonDeserializationModel.Default });

        string directive = $"{_r4CoreId}#{_r4CoreVersion}";

        DefinitionCollection? loaded = await loader.LoadPackages([directive, directive]);

        loaded.ShouldNotBeNull();
        loaded!.MainPackageId.ShouldBe(_r4CoreId);
        loaded.MainPackageVersion.ShouldBe(_r4CoreVersion);

        string[] messages = [.. sink];
        string moniker = $"{_r4CoreId}@{_r4CoreVersion}";

        messages.Count(m => m.Contains($"Processing {moniker}", System.StringComparison.Ordinal)).ShouldBe(
            1,
            "the repeated directive must be skipped before the install, not after it");

        messages.Count(m =>
            m.Contains("Skipping already requested directive", System.StringComparison.Ordinal) &&
            m.Contains(moniker, System.StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>A logger provider that captures every formatted message for assertion.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _sink;

        internal CapturingLoggerProvider(ConcurrentQueue<string> sink)
        {
            _sink = sink;
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _sink;

            internal CapturingLogger(ConcurrentQueue<string> sink)
            {
                _sink = sink;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                System.Exception? exception,
                System.Func<TState, System.Exception?, string> formatter)
            {
                _sink.Enqueue(formatter(state, exception));
            }
        }
    }
}
