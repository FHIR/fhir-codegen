using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;

//using Microsoft.Extensions.Logging;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;

namespace Fhir.CodeGen.Lib.Tests;

// TODO: @brianpos - there are a lot of similar functions in DefinitionCollectionTx, we should discuss on where we can consolidate
public static class CanonicalExtensions
{
    public static string? Version(this Canonical url)
    {
        int indexOfVersion = url.Value.IndexOf("|");
        if (indexOfVersion == -1)
            return null;
        var result = url.Value.Substring(indexOfVersion + 1);
        int indexOfFragment = result.IndexOf("#");
        if (indexOfFragment != -1)
            return result.Substring(0, indexOfFragment);
        return result;
    }

    public static void Version(this Canonical url, string version)
    {
        if (string.IsNullOrEmpty(version))
            return;

        string newUrl = $"{url.BaseCanonicalUrl()}|{version}";
        string? fragment = url.Fragment();
        int indexOfVersion = url.Value.IndexOf("|");
        if (!string.IsNullOrEmpty(fragment))
            newUrl += $"#{fragment}";
        url.Value = newUrl;
    }

    public static string BaseCanonicalUrl(this Canonical url)
    {
        int indexOfVersion = url.Value.IndexOf("|");
        if (indexOfVersion != -1)
            return url.Value.Substring(0, indexOfVersion);
        int indexOfFragment = url.Value.IndexOf("#");
        if (indexOfFragment != -1)
            return url.Value.Substring(0, indexOfFragment);
        return url.Value;
    }

    public static string? Fragment(this Canonical url)
    {
        int indexOfFragment = url.Value.IndexOf("#");
        if (indexOfFragment != -1)
            return url.Value.Substring(0, indexOfFragment);
        return null;
    }
}

/// <summary>
/// This resolver will take any non version specific question and then resolve with
/// the designated version number
/// </summary>
internal class FixedVersionResolver : IAsyncResourceResolver
{
    public FixedVersionResolver(string version, IAsyncResourceResolver source)
    {
        _fixedVersion = version;
        _source = source;
    }
    private string _fixedVersion;
    private IAsyncResourceResolver _source;

    public async Task<Resource> ResolveByCanonicalUriAsync(string uri)
    {
        Canonical cu = new Canonical(uri);
        if (string.IsNullOrWhiteSpace(cu.Version()))
            cu.Version(_fixedVersion);
        return await _source.ResolveByCanonicalUriAsync(cu);
    }

    public async Task<Resource> ResolveByUriAsync(string uri)
    {
        Canonical cu = new Canonical(uri);
        if (string.IsNullOrWhiteSpace(cu.Version()))
            cu.Version(_fixedVersion);
        return await _source.ResolveByUriAsync(cu);
    }
}

internal class VersionFilterResolver : IAsyncResourceResolver
{
    public VersionFilterResolver(string version, IAsyncResourceResolver source)
    {
        _fixedVersion = version;
        _source = source;
    }
    private string _fixedVersion;
    private IAsyncResourceResolver _source;

    public async Task<Resource> ResolveByCanonicalUriAsync(string uri)
    {
        string convertedUrl = CrossVersionResolver.ConvertCanonical(uri);
        Canonical cu = new Canonical(convertedUrl);
        if (!string.IsNullOrWhiteSpace(cu.Version()))
        {
            if (cu.Version() != _fixedVersion)
                return null!;
        }
        var result = await _source.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
        return result;
    }

    public async Task<Resource> ResolveByUriAsync(string uri)
    {
        string convertedUrl = CrossVersionResolver.ConvertCanonical(uri);
        Canonical cu = new Canonical(convertedUrl);
        if (!string.IsNullOrWhiteSpace(cu.Version()))
        {
            if (cu.Version() != _fixedVersion)
                return null!;
        }
        return await _source.ResolveByUriAsync(cu.BaseCanonicalUrl());
    }
}

internal class CrossVersionResolver : IAsyncResourceResolver
{
    public CrossVersionResolver()
    {
        var settingsJson = new FhirJsonParsingSettings() { PermissiveParsing = true };
        var settingsXml = new FhirXmlParsingSettings() { PermissiveParsing = true };
        var settingsDir = new DirectorySourceSettings()
        {
            JsonParserSettings = settingsJson,
            XmlParserSettings = settingsXml,
            ParserSettings = new ParserSettings() { ExceptionHandler = (object source, ExceptionNotification args) => { } }
        };

        string crossVersionPackages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FhirCrossVersionTests");
        if (!System.IO.Directory.Exists(crossVersionPackages))
        {
            System.Diagnostics.Trace.WriteLine($"Cross Version package cache folder does not exist {crossVersionPackages}");
        }

        //dstu2 = new DirectorySource(Contents.Combine(crossVersionPackages, "r2b"), settingsDir);
        //stu3 = new DirectorySource(Contents.Combine(crossVersionPackages, "r3"), settingsDir);
        //r4 = new DirectorySource(Contents.Combine(crossVersionPackages, "r4"), settingsDir);
        //r4b = new DirectorySource(Contents.Combine(crossVersionPackages, "r4b"), settingsDir);
        //r5 = ZipSource.CreateValidationSource();
    }

    public async System.Threading.Tasks.Task<IEnumerable<DefinitionCollection>> Initialize(string[] versions)
    {
        List<DefinitionCollection> result = new List<DefinitionCollection>();
        PackageLoader loader = new(new() { AutoLoadExpansions = false, ResolvePackageDependencies = false, LoadStructures = [ FhirArtifactClassEnum.Resource, FhirArtifactClassEnum.ComplexType, FhirArtifactClassEnum.PrimitiveType ] });
        foreach (string version in versions)
        {
            FhirReleases.FhirSequenceCodes sequence = FhirReleases.FhirVersionToSequence(version);
            switch (FhirReleases.FhirVersionToSequence(version))
            {
                case FhirReleases.FhirSequenceCodes.DSTU2:
                    dstu2 ??= await LoadPackage(loader, sequence.ToCorePackageDirective());
                    result.Add((DefinitionCollection)dstu2);
                    break;
                case FhirReleases.FhirSequenceCodes.STU3:
                    stu3 ??= await LoadPackage(loader, sequence.ToCorePackageDirective());
                    result.Add((DefinitionCollection)stu3);
                    break;
                case FhirReleases.FhirSequenceCodes.R4:
                    r4 ??= await LoadPackage(loader, sequence.ToCorePackageDirective());
                    result.Add((DefinitionCollection)r4);
                    break;
                case FhirReleases.FhirSequenceCodes.R4B:
                    r4b ??= await LoadPackage(loader, sequence.ToCorePackageDirective());
                    result.Add((DefinitionCollection)r4b);
                    break;
                case FhirReleases.FhirSequenceCodes.R5:
                    r5 ??= await LoadPackage(loader, sequence.ToCorePackageDirective());
                    result.Add((DefinitionCollection)r5);
                    break;
                default:
                    throw new ApplicationException($"Unknown version {version}");
            }
        }
        return result;
    }

    /// <summary>Loads.</summary>
    /// <exception cref="Exception">Thrown when an exception error condition occurs.</exception>
    /// <param name="directive">The directive to load.</param>
    /// <returns>A PackageCacheEntry.</returns>
    private async Task<DefinitionCollection> LoadPackage(PackageLoader loader, string directive)
    {
        DefinitionCollection? loaded = await loader.LoadPackages( [ directive ] );
        if (loaded == null)
        {
            throw new ApplicationException($"Failed to load package {directive} into cache");
        }
        return loaded;
    }


    public IAsyncResourceResolver OnlyDstu2 { get { return new VersionFilterResolver("2.0", dstu2 ?? throw new Exception("Cannot resolve in DSTU2 since it was not initialized!")); } }
    public IAsyncResourceResolver OnlyStu3 { get { return new VersionFilterResolver("3.0", stu3 ?? throw new Exception("Cannot resolve in STU3 since it was not initialized!")); } }
    public IAsyncResourceResolver OnlyR4 { get { return new VersionFilterResolver("4.0", r4 ?? throw new Exception("Cannot resolve in R4 since it was not initialized!")); } }
    public IAsyncResourceResolver OnlyR4B { get { return new VersionFilterResolver("4.3", r4b ?? throw new Exception("Cannot resolve in R4B since it was not initialized!")); } }
    public IAsyncResourceResolver OnlyR5 { get { return new VersionFilterResolver("5.0", r5 ?? throw new Exception("Cannot resolve in R5 since it was not initialized!")); } }

    public void CustomExceptionHandler(object source, ExceptionNotification args)
    {
        // System.Diagnostics.Trace.WriteLine(args.Message);
    }

    private IAsyncResourceResolver? dstu2;
    private IAsyncResourceResolver? stu3;
    private IAsyncResourceResolver? r4;
    private IAsyncResourceResolver? r4b;
    private IAsyncResourceResolver? r5;

    private const string fhirBaseCanonical = "http://hl7.org/fhir/";

    public static string ConvertCanonical(string uri)
    {
        if (uri.StartsWith(fhirBaseCanonical))
        {
            var remainder = uri.Substring(fhirBaseCanonical.Length);
            string resourceName;
            if (remainder.StartsWith("StructureDefinition/"))
                remainder = remainder.Substring("StructureDefinition/".Length);
            if (!remainder.Contains("/"))
                return uri;
            resourceName = remainder.Substring(remainder.IndexOf("/") + 1);
            if (resourceName.StartsWith("StructureDefinition/"))
                resourceName = resourceName.Substring("StructureDefinition/".Length);
            remainder = remainder.Substring(0, remainder.IndexOf("/"));

            // convert this from the old format into the versioned format
            // http://hl7.org/fhir/3.0/StructureDefinition/Account
            // =>
            // http://hl7.org/fhir/StructureDefinition/Account|3.0
            // http://hl7.org/fhir/StructureDefinition/Account|3.0.1
            // http://hl7.org/fhir/StructureDefinition/Account|4.0.1
            // i.e. https://github.com/FHIR/fhir-codegen/blob/dev/src/Microsoft.Health.Fhir.SpecManager/Manager/FhirPackageCommon.cs#L513


            // TODO: @brianpos - I would recommend using the functions in FhirReleases that I keep up to date with builds and versions
            string version = remainder;
            switch (version)
            {
                case "1.0":
                case "1.0.2":
                case "DSTU2":
                    version = "1.0";
                    break;
                case "3.0":
                case "3.0.0":
                case "3.0.1":
                case "3.0.2":
                case "STU3":
                    version = "3.0";
                    break;

                case "4.0":
                case "4.0.0":
                case "4.0.1":
                case "R4":
                    version = "4.0";
                    break;

                case "4.3":
                case "4.3.0":
                case "R4B":
                    version = "4.3";
                    break;

                case "5.0":
                case "5.0.0":
                case "R5":
                    version = "5.0";
                    break;
                default:
                    return uri;
            }
            return $"{fhirBaseCanonical}StructureDefinition/{resourceName}|{version}";
        }
        return uri;
    }

    public async Task<Resource> ResolveByCanonicalUriAsync(string uri)
    {
        string convertedUrl = ConvertCanonical(uri);
        Canonical cu = new Canonical(convertedUrl);
        Resource? result = null;

        FhirReleases.FhirSequenceCodes canonicalVersion = FhirReleases.FhirVersionToSequence(cu.Version() ?? string.Empty);

        switch (canonicalVersion)
        {
            case FhirReleases.FhirSequenceCodes.DSTU2:
                if (dstu2 != null)
                {
                    result = await dstu2.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.STU3:
                if (stu3 != null)
                {
                    result = await stu3.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.R4:
                if (r4 != null)
                {
                    result = await r4.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.R4B:
                if (r4b != null)
                {
                    result = await r4b.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.R5:
                if (r5 != null)
                {
                    result = await r5.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            default:
                // try all versions we have until we can resolve or we run out of versions
                if (r4 != null)
                {
                    result = await r4.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if ((result == null) && (r5 != null))
                {
                    result = await r5.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if ((result == null) && (r4b != null))
                {
                    result = await r4b.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if ((result == null) && (stu3 != null))
                {
                    result = await stu3.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if ((result == null) && (dstu2 != null))
                {
                    result = await dstu2.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                break;
        }

        if (result == null)
        {
            Console.WriteLine($"  * Failed to resolve: {uri} at [{cu.Version()}] {cu.BaseCanonicalUrl()}");
        }

        return result!;
    }

    public async Task<Resource> ResolveByUriAsync(string uri)
    {
        string convertedUrl = ConvertCanonical(uri);
        Canonical cu = new Canonical(convertedUrl);

        FhirReleases.FhirSequenceCodes canonicalVersion = FhirReleases.FhirVersionToSequence(cu.Version() ?? string.Empty);

        switch (canonicalVersion)
        {
            case FhirReleases.FhirSequenceCodes.DSTU2:
                if (dstu2 != null)
                {
                    return await dstu2.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.STU3:
                if (stu3 != null)
                {
                    return await stu3.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.R4:
                if (r4 != null)
                {
                    return await r4.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.R4B:
                if (r4b != null)
                {
                    return await r4b.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            case FhirReleases.FhirSequenceCodes.R5:
                if (r5 != null)
                {
                    return await r5.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }
                break;

            default:
                // try all versions we have until we can resolve or we run out of versions
                if (r4 != null)
                {
                    return await r4.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if (r5 != null)
                {
                    return await r5.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if (r4b != null)
                {
                    return await r4b.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if (stu3 != null)
                {
                    return await stu3.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                if (dstu2 != null)
                {
                    return await dstu2.ResolveByCanonicalUriAsync(cu.BaseCanonicalUrl());
                }

                break;
        }

        return null!;
    }
}
