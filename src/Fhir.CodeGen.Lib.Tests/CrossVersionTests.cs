// <copyright file="FhirPackageTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>
extern alias stu3;
extern alias r4;
extern alias r4b;

using System.Diagnostics;
using System.Text;
using Shouldly;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Source;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Lib.Tests.Extensions;
using Fhir.CodeGen.MappingLanguage;
using Xunit.Abstractions;


namespace Fhir.CodeGen.Lib.Tests;

public class CrossVersionTests
{
    public CrossVersionTests(ITestOutputHelper outputWriter)
    {
        Console.SetOut(new TestWriter(outputWriter));
    }

    internal static CrossVersionResolver cvr = new CrossVersionResolver();

    private static string FindRelativeDir(string path)
    {
        return DirectoryContentsAttribute.FindRelativeDir(string.Empty, path, true);
    }

    [Theory(DisplayName = "TestLoadingFml", Skip = "Tests require external repo - run manually if desired")]
    [Trait("Category", "FML")]
    [Trait("RequiresExternalRepo", "true")]
    [Trait("ExternalRepo", "HL7/fhir-cross-version")]
    [InlineData("fhir-cross-version/input/R2toR3", "2to3")]
    [InlineData("fhir-cross-version/input/R3toR2", "3to2")]
    [InlineData("fhir-cross-version/input/R3toR4", "3to4")]
    [InlineData("fhir-cross-version/input/R4toR3", "4to3")]
    [InlineData("fhir-cross-version/input/R4toR5", "4to5")]
    [InlineData("fhir-cross-version/input/R5toR4", "5to4")]
    [InlineData("fhir-cross-version/input/R4BtoR5", "4Bto5")]
    [InlineData("fhir-cross-version/input/R5toR4B", "5to4B")]
    public void TestLoadingFml(string path, string versionToVersion)
    {
        //string prefixPath = @"C:\git\fhir-cross-version\input\";
        int versionToVersionLen = versionToVersion.Length;

        // files have different styles in each directory, but we want all FML files anyway
        string[] files = Directory.GetFiles(FindRelativeDir(path), $"*.fml", SearchOption.TopDirectoryOnly);

        FhirMappingLanguage content = new();

        Dictionary<string, Dictionary<string, CrossVersionMapCollection.FmlTargetInfo>> fmlPathLookup = [];

        int errorCount = 0;
        foreach (string filename in files)
        {
            string fmlContent = File.ReadAllText(filename);

            // Console.WriteLine($"Parsing {filename}");
            System.Diagnostics.Trace.Write($"Parsing {filename}\n");
            if (!content.TryParse(fmlContent, out FhirStructureMap? fml))
            {
                Console.WriteLine($"Error loading {filename}: could not parse");
                errorCount++;
                continue;
            }

            fml.ShouldNotBeNull();

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

            if (name.EndsWith(versionToVersion, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^versionToVersionLen];
            }

            if (name.Equals("primitives", StringComparison.OrdinalIgnoreCase))
            {
                // skip primitive type map - we have that information internally already
                continue;
            }

            try
            {
                CrossVersionMapCollection.ProcessCrossVersionFml(name, fml, fmlPathLookup);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error Processing {filename}: {ex.Message}");
                errorCount++;
            }

            //ProcessCrossVersionFml(string name, FhirStructureMap fml, Dictionary<string, List<GroupExpression>> fmlPathLookup)
        }
        errorCount.ShouldBe(0, "Should be no parsing/processing errors");
    }

    internal IAsyncResourceResolver OnlyVersion(CrossVersionResolver resolver, string version)
    {
        switch (version)
        {
            case "2": return resolver.OnlyDstu2;
            case "3": return resolver.OnlyStu3;
            case "4": return resolver.OnlyR4;
            case "4B": return resolver.OnlyR4B;
            case "5": return resolver.OnlyR5;
        }
        return null!;
    }

    [Theory(DisplayName = "ValidateCrossVersionMaps", Skip = "Tests are used to test FML, not this project - run manually if desired")]
    [Trait("Category", "FML")]
    [Trait("RequiresExternalRepo", "true")]
    [Trait("ExternalRepo", "HL7/fhir-cross-version")]
    // [InlineData("fhir-cross-version/input/R2toR3", "2to3")]
    // [InlineData("fhir-cross-version/input/R3toR2", "3to2")]
    [InlineData("fhir-cross-version/input/R3toR4", "3to4")]
    // [InlineData("fhir-cross-version/input/R4toR3", "4to3")]
    [InlineData("fhir-cross-version/input/R4toR5", "4to5")]
    [InlineData("fhir-cross-version/input/R5toR4", "5to4")]
    [InlineData("fhir-cross-version/input/R4BtoR5", "4Bto5")]
    [InlineData("fhir-cross-version/input/R5toR4B", "5to4B")]
    public async System.Threading.Tasks.Task ValidateCrossVersionMaps(string path, string versionToVersion)
    {
        int versionToVersionLen = versionToVersion.Length;
        int errorCount = 0;
        int warningCount = 0;

        // files have different styles in each directory, but we want all FML files anyway
        string[] files = Directory.GetFiles(FindRelativeDir(path), $"*.fml", SearchOption.TopDirectoryOnly);

        FhirMappingLanguage content = new();

        Dictionary<string, Dictionary<string, CrossVersionMapCollection.FmlTargetInfo>> fmlPathLookup = [];

        var versions = versionToVersion.Split("to");
        var dcs = (await cvr.Initialize(versions)).ToList();
        CachedResolver source = new CachedResolver(cvr);
        source.Load += Source_Load;

        var sourceResolver = new CachedResolver(new MultiResolver(OnlyVersion(cvr, versions[0]), cvr));
        sourceResolver.Load += Source_Load;

        var targetResolver = new CachedResolver(new MultiResolver(OnlyVersion(cvr, versions[1]), cvr));
        targetResolver.Load += Source_Load;

        async Task<StructureDefinition?> resolveMapUseCrossVersionType(string url, string? alias)
        {
            if (await source.ResolveByCanonicalUriAsync(url) is StructureDefinition sd)
            {
                // Console.WriteLine(" - yup");
                return sd;
            }
            Console.WriteLine($"\nError: Resolving Type {url} as {alias} was not found");
            errorCount++;
            return null;
        }
        IEnumerable<FhirStructureMap> resolveMaps(string url)
        {
            Console.WriteLine($"Resolving Maps {url}");
            return new List<FhirStructureMap>();
        }

        List<FhirStructureMap> allMaps = new List<FhirStructureMap>();
        foreach (string filename in files)
        {
            string fmlContent = File.ReadAllText(filename);

            if (!content.TryParse(fmlContent, out FhirStructureMap? fml))
            {
                Console.WriteLine($"Error loading {filename}: could not parse");
                errorCount++;
                continue;
            }

            fml.ShouldNotBeNull();

            allMaps.Add(fml);
        }
        errorCount.ShouldBe(0, "Should be no parsing/processing errors");

        // Prepare a cache of the TYPE based map groups
        Dictionary<string, GroupDeclaration> namedGroups = new Dictionary<string, GroupDeclaration>();
        Dictionary<string, GroupDeclaration?> typedGroups = new Dictionary<string, GroupDeclaration?>();

        // With a default set of maps from the fhir types to fhirpath primitives
        typedGroups.Add("http://hl7.org/fhirpath/System.Date -> http://hl7.org/fhirpath/System.DateTime", null);
        typedGroups.Add("http://hl7.org/fhirpath/System.DateTime -> http://hl7.org/fhirpath/System.Date", null);
        typedGroups.Add("http://hl7.org/fhirpath/System.String -> http://hl7.org/fhirpath/System.Integer", null);
        typedGroups.Add("http://hl7.org/fhirpath/System.Integer -> http://hl7.org/fhirpath/System.String", null);
        typedGroups.Add("http://hl7.org/fhirpath/System.Integer -> http://hl7.org/fhirpath/System.Decimal", null);

        foreach (var fml in allMaps)
        {
            foreach (var group in fml.GroupsByName.Values)
            {
                // Console.WriteLine($"{group.TypeMode} {group.Name}");
                if (!namedGroups.ContainsKey(group.Name))
                    namedGroups.Add(group.Name, group);
                else
                {
                    Console.WriteLine($"Error: Duplicate group name: {group.Name}");
                    errorCount++;
                }

                if (group.TypeMode == StructureMap.StructureMapGroupTypeMode.TypeAndTypes
                    || group.TypeMode == StructureMap.StructureMapGroupTypeMode.Types)
                {
                    Console.Write($"{group.TypeMode} {group.Name}");

                    // Check that all the parameters have type declarations
                    Dictionary<string, StructureDefinition?> aliasedTypes = new();
                    foreach (var use in fml.StructuresByUrl)
                    {
                        // Console.WriteLine($"Use {use.Key} as {use.Value?.Alias}");
                        var sd = await resolveMapUseCrossVersionType(use.Key.Trim('\"'), use.Value?.Alias);
                        if (use.Value?.Alias != null)
                            aliasedTypes.Add(use.Value.Alias, sd);
                        else if (sd != null && sd.Name != null)
                            aliasedTypes.Add(use.Value?.Alias ?? sd.Name, sd);
                    }
                    string? typeMapping = null;
                    foreach (var gp in group.Parameters)
                    {
                        if (string.IsNullOrEmpty(gp.TypeIdentifier))
                        {
                            Console.WriteLine($"\n    * No type provided for parameter `{gp.Identifier}`");
                            errorCount++;
                        }
                        else
                        {
                            string? type = gp.TypeIdentifier;
                            // lookup the type in the aliases
                            var resolver = gp.InputMode == StructureMap.StructureMapInputMode.Source ? sourceResolver : targetResolver;
                            if (type != null)
                            {
                                if (!type.Contains('/') && aliasedTypes.ContainsKey(type))
                                {
                                    var sd = aliasedTypes[type];
                                    if (sd != null)
                                    {
                                        var sw = new FmlStructureDefinitionWalker(sd, resolver);
                                        type = $"{sd.Url}|{sd.Version}";
                                        gp.ParameterElementDefinition = sw.Current;
                                    }
                                }
                                else if (type != "string")
                                {
                                    Console.WriteLine($"\nError: Group {group.Name} parameter {gp.Identifier} at @{gp.Line}:{gp.Column} has no type `{gp.TypeIdentifier}`");
                                    errorCount++;
                                }
                            }
                            if (!string.IsNullOrEmpty(typeMapping))
                            {
                                if (group.TypeMode == StructureMap.StructureMapGroupTypeMode.TypeAndTypes)
                                {
                                    Console.Write($"\t\t{typeMapping}");
                                    if (typedGroups.ContainsKey(typeMapping))
                                    {
                                        GroupDeclaration? existingGroup = typedGroups[typeMapping];
                                        Console.WriteLine($"    Error: Group {group.Name} @{group.Line}:{group.Column} duplicates the default type mappings declared in group `{existingGroup?.Name}` @{existingGroup?.Line}:{existingGroup?.Column}");
                                        errorCount++;
                                    }
                                    else
                                    {
                                        typedGroups.Add(typeMapping, group);
                                    }
                                }
                                typeMapping += " -> ";
                            }
                            typeMapping += type;
                        }
                    }

                    if (typeMapping == null)
                    {
                        // TODO: @brianpos - is this correct to throw?  cannot have null value in typeMapping for the dictionary calls after this
                        throw new Exception($"    Error: Group {group.Name} has no type mapping!");
                    }

                    Console.Write($"\t\t{typeMapping}");
                    Console.Write("\n");

                    if (typedGroups.TryGetValue(typeMapping, out GroupDeclaration? eg))
                    {
                        Console.WriteLine($"    Error: Group {group.Name} duplicates the type mappings declared in group {eg?.Name}");
                        errorCount++;
                    }
                    else
                    {
                        typedGroups.Add(typeMapping, group);
                    }
                }
                else
                {
                    // Console.WriteLine($"skipping {group.TypeMode} {group.Name}");
                }
            }
        }

        // Now scan all these maps
        var options = new CrossVersionCheckOptions
        {
            resolveMapUseCrossVersionType = resolveMapUseCrossVersionType,
            resolveMaps = resolveMaps,
            source = GetModelOptions(versions[0], sourceResolver),
            SourcePackage = dcs[0],
            target = GetModelOptions(versions[1], targetResolver),
            TargetPackage= dcs[1],
            namedGroups = namedGroups,
            typedGroups = typedGroups,
        };
        foreach (var fml in allMaps)
        {
            try
            {
                var outcome = await FmlValidator.VerifyFmlDataTypes(fml, options);
                if (!outcome.Success)
                    errorCount++;
                if (outcome.Warnings > 0)
                    warningCount++; // += outcome.Warnings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Processing {fml.MetadataByPath["name"].Literal?.ValueAsString}: {ex.Message}");
                errorCount++;
            }

            //ProcessCrossVersionFml(string name, FhirStructureMap fml, Dictionary<string, List<GroupExpression>> fmlPathLookup)
        }
        Assert.True(errorCount == 0 && warningCount == 0, $"FML Errors: {errorCount}, Warnings: {warningCount}");
    }

    [Theory(DisplayName = "CheckFmlMissingProps", Skip = "Tests are used to test FML, not this project - run manually if desired")]
    [Trait("Category", "FML")]
    [Trait("RequiresExternalRepo", "true")]
    [Trait("ExternalRepo", "HL7/fhir-cross-version")]
    // [InlineData("fhir-cross-version/input/R2toR3", "2to3")]
    // [InlineData("fhir-cross-version/input/R3toR2", "3to2")]
    [InlineData("fhir-cross-version/input/R3toR4", "3to4")]
    // [InlineData("fhir-cross-version/input/R4toR3", "4to3")]
    [InlineData("fhir-cross-version/input/R4toR5", "4to5")]
    [InlineData("fhir-cross-version/input/R5toR4", "5to4")]
    [InlineData("fhir-cross-version/input/R4BtoR5", "4Bto5")]
    [InlineData("fhir-cross-version/input/R5toR4B", "5to4B")]
    public async System.Threading.Tasks.Task CheckFmlMissingProps(string path, string versionToVersion)
    {
        int versionToVersionLen = versionToVersion.Length;
        int errorCount = 0;
        int warningCount = 0;

        // files have different styles in each directory, but we want all FML files anyway
        string[] files = Directory.GetFiles(FindRelativeDir(path), $"*.fml", SearchOption.TopDirectoryOnly);

        FhirMappingLanguage content = new();

        Dictionary<string, Dictionary<string, CrossVersionMapCollection.FmlTargetInfo>> fmlPathLookup = [];

        var versions = versionToVersion.Split("to");
        var dcs = (await cvr.Initialize(versions)).ToList();
        CachedResolver source = new CachedResolver(cvr);
        source.Load += Source_Load;

        var sourceResolver = new CachedResolver(new MultiResolver(OnlyVersion(cvr, versions[0]), cvr));
        sourceResolver.Load += Source_Load;

        var targetResolver = new CachedResolver(new MultiResolver(OnlyVersion(cvr, versions[1]), cvr));
        targetResolver.Load += Source_Load;

        async Task<StructureDefinition?> resolveMapUseCrossVersionType(string url, string? alias)
        {
            if (await source.ResolveByCanonicalUriAsync(url) is StructureDefinition sd)
            {
                // Console.WriteLine(" - yup");
                return sd;
            }
            Console.WriteLine($"\nError: Resolving Type {url} as {alias} was not found");
            errorCount++;
            return null;
        }
        IEnumerable<FhirStructureMap> resolveMaps(string url)
        {
            Console.WriteLine($"Resolving Maps {url}");
            return new List<FhirStructureMap>();
        }

        List<FhirStructureMap> allMaps = new List<FhirStructureMap>();
        foreach (string filename in files)
        {
            // if (!filename.Contains("Goal") && !filename.Contains("Extension") && !filename.Contains("rimitive")) continue;
            string fmlContent = File.ReadAllText(filename);

            if (!content.TryParse(fmlContent, out FhirStructureMap? fml))
            {
                Console.WriteLine($"Error loading {filename}: could not parse");
                errorCount++;
                continue;
            }

            fml.ShouldNotBeNull();

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

            if (name.EndsWith(versionToVersion, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^versionToVersionLen];
            }

            try
            {
                CrossVersionMapCollection.ProcessCrossVersionFml(name, fml, fmlPathLookup);
                allMaps.Add(fml);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error Processing {filename}: {ex.Message}");
                errorCount++;
            }
        }
        errorCount.ShouldBe(0, "Should be no parsing/processing errors");

        // Prepare a cache of the TYPE based map groups
        Dictionary<string, GroupDeclaration> namedGroups = new Dictionary<string, GroupDeclaration>();
        Dictionary<string, GroupDeclaration?> typedGroups = new Dictionary<string, GroupDeclaration?>();

        foreach (var fml in allMaps)
        {
            foreach (var group in fml.GroupsByName.Values)
            {
                // Console.WriteLine($"{group.TypeMode} {group.Name}");
                if (!namedGroups.ContainsKey(group.Name))
                    namedGroups.Add(group.Name, group);
                else
                {
                    Console.WriteLine($"Error: Duplicate group name: {group.Name}");
                    errorCount++;
                }

                if (group.TypeMode == StructureMap.StructureMapGroupTypeMode.TypeAndTypes
                    || group.TypeMode == StructureMap.StructureMapGroupTypeMode.Types)
                {
                    // Check that all the parameters have type declarations
                    Dictionary<string, StructureDefinition?> aliasedTypes = new();
                    foreach (var use in fml.StructuresByUrl)
                    {
                        var sd = await resolveMapUseCrossVersionType(use.Key.Trim('\"'), use.Value?.Alias);
                        if (use.Value?.Alias != null)
                            aliasedTypes.Add(use.Value.Alias, sd);
                        else if (sd != null && sd.Name != null)
                            aliasedTypes.Add(use.Value?.Alias ?? sd.Name, sd);
                    }
                    string? typeMapping = null;
                    foreach (var gp in group.Parameters)
                    {
                        if (string.IsNullOrEmpty(gp.TypeIdentifier))
                        {
                            Console.WriteLine($"\n    * No type provided for parameter `{gp.Identifier}`");
                            errorCount++;
                        }
                        else
                        {
                            string? type = gp.TypeIdentifier;
                            // lookup the type in the aliases
                            var resolver = gp.InputMode == StructureMap.StructureMapInputMode.Source ? sourceResolver : targetResolver;
                            if (type != null)
                            {
                                if (!type.Contains('/') && aliasedTypes.ContainsKey(type))
                                {
                                    var sd = aliasedTypes[type];
                                    if (sd != null)
                                    {
                                        var sw = new FmlStructureDefinitionWalker(sd, resolver);
                                        type = $"{sd.Url}|{sd.Version}";
                                        gp.ParameterElementDefinition = sw.Current;
                                    }
                                }
                                else if (type != "string")
                                {
                                    Console.WriteLine($"\nError: Group {group.Name} parameter {gp.Identifier} at @{gp.Line}:{gp.Column} has no type `{gp.TypeIdentifier}`");
                                    errorCount++;
                                }
                            }
                            if (!string.IsNullOrEmpty(typeMapping))
                                typeMapping += " -> ";
                            typeMapping += type;
                        }
                    }
                }
                else
                {
                    // Console.WriteLine($"skipping {group.TypeMode} {group.Name}");
                }
            }
        }

        // Now scan all these maps
        var options = new CrossVersionCheckOptions
        {
            resolveMapUseCrossVersionType = resolveMapUseCrossVersionType,
            resolveMaps = resolveMaps,
            source = GetModelOptions(versions[0], sourceResolver),
            SourcePackage = dcs[0],
            target = GetModelOptions(versions[1], targetResolver),
            TargetPackage = dcs[1],
            namedGroups = namedGroups,
            typedGroups = typedGroups,
        };
        foreach (var fml in allMaps)
        {
            try
            {
                var outcome = await CrossVersionMapCollection.CheckFmlForMissingProperties(fml, options);
                if (!outcome.Success)
                    errorCount++;
                if (outcome.Warnings > 0)
                    warningCount++; // += outcome.Warnings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Processing {fml.MetadataByPath["name"].Literal?.ValueAsString}: {ex.Message}");
                errorCount++;
            }

            //ProcessCrossVersionFml(string name, FhirStructureMap fml, Dictionary<string, List<GroupExpression>> fmlPathLookup)
        }
        Assert.True(errorCount == 0 && warningCount == 0, $"FML Errors: {errorCount}, Warnings: {warningCount}");
    }

    private static ModelOptions GetModelOptions(string version, CachedResolver resolver)
    {
        ModelInspector inspector = r4.Hl7.Fhir.Model.ModelInfo.ModelInspector;
        List<string> supportedResources = r4.Hl7.Fhir.Model.ModelInfo.SupportedResources;
        Type[] openTypes = r4.Hl7.Fhir.Model.ModelInfo.OpenTypes;
        switch(version)
        {
            case "3":
                inspector = stu3.Hl7.Fhir.Model.ModelInfo.ModelInspector;
                supportedResources = stu3.Hl7.Fhir.Model.ModelInfo.SupportedResources;
                openTypes = stu3.Hl7.Fhir.Model.ModelInfo.OpenTypes;
                break;
            case "4":
                inspector = r4.Hl7.Fhir.Model.ModelInfo.ModelInspector;
                supportedResources = r4.Hl7.Fhir.Model.ModelInfo.SupportedResources;
                openTypes = r4.Hl7.Fhir.Model.ModelInfo.OpenTypes;
                break;
            case "4B":
                inspector = r4b.Hl7.Fhir.Model.ModelInfo.ModelInspector;
                supportedResources = r4b.Hl7.Fhir.Model.ModelInfo.SupportedResources;
                openTypes = r4b.Hl7.Fhir.Model.ModelInfo.OpenTypes;
                break;
            case "5":
                inspector = Hl7.Fhir.Model.ModelInfo.ModelInspector;
                supportedResources = Hl7.Fhir.Model.ModelInfo.SupportedResources;
                openTypes = Hl7.Fhir.Model.ModelInfo.OpenTypes;
                break;
            default:
                Debugger.Break();
                inspector = Hl7.Fhir.Model.ModelInfo.ModelInspector;
                supportedResources = Hl7.Fhir.Model.ModelInfo.SupportedResources;
                openTypes = Hl7.Fhir.Model.ModelInfo.OpenTypes;
                break;
        }
        return new ModelOptions
        {
            Resolver = resolver,
            MI = inspector,
            SupportedResources = supportedResources,
            OpenTypes = openTypes,
        };
    }

    private void Source_Load(object sender, CachedResolver.LoadResourceEventArgs e)
    {
        if (e.Resource is IVersionableConformanceResource cr)
        {
            // Console.WriteLine($"{e.CanonicalUrl} {cr.Name} | {cr.Version}");
        }
    }

    //[Xunit.Fact(DisplayName = "PrepareCrossVersionStructureDefinitionCache")]
    //public async System.Threading.Tasks.Task PrepareCrossVersionStructureDefinitionCache()
    //{
    //    // Download the cross version packages zip file
    //    // http://fhir.org/packages/xver-packages.zip
    //    string crossVersionPackages = Contents.Combine(
    //        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    //        "FhirCrossVersionTests");
    //    if (!Directory.Exists(crossVersionPackages))
    //        Directory.CreateDirectory(crossVersionPackages);

    //    Console.ForegroundColor = ConsoleColor.Green;
    //    Console.WriteLine($"Cross version cache located in {crossVersionPackages}");
    //    Console.ResetColor();

    //    string crossVersionPackagesZipFile = Contents.Combine(crossVersionPackages, "xver-packages.zip");

    //    if (!File.Exists(crossVersionPackagesZipFile))
    //    {
    //        Console.WriteLine($"Downloading http://fhir.org/packages/xver-packages.zip");
    //        HttpClient server = new HttpClient();
    //        var stream = await server.GetStreamAsync("http://fhir.org/packages/xver-packages.zip");
    //        using (var outStream = File.OpenWrite(crossVersionPackagesZipFile))
    //        {
    //            await stream.CopyToAsync(outStream);
    //            await outStream.FlushAsync();
    //        }
    //    }

    //    using (var zipStream = File.OpenRead(crossVersionPackagesZipFile))
    //    {
    //        ZipArchive archive = new ZipArchive(zipStream);
    //        foreach (var item in archive.Entries)
    //        {
    //            if ((item.Name.EndsWith(".as.r5.tgz") || item.Name.EndsWith("hl7.fhir.r5.core.tgz")) && !item.Name.StartsWith("."))
    //            {
    //                Console.Write($"Verifying cache for {item.Name}");
    //                var path = Contents.Combine(
    //                    crossVersionPackages,
    //                    item.Name.Split('.').Skip(2).First());
    //                if (!Directory.Exists(path))
    //                {
    //                    Console.WriteLine($"\r\n    Extracting for {item.Name.Split('.').Skip(2).First()}");
    //                    Directory.CreateDirectory(path);

    //                    // Now extract this package into this folder
    //                    using (var tarStream = new GZipStream(item.Open(), CompressionMode.Decompress))
    //                    {
    //                        TarReader r = new TarReader(tarStream);
    //                        var a = await r.GetNextEntryAsync();
    //                        while (a != null)
    //                        {
    //                            if (!a.Name.StartsWith("package/other/")
    //                                && !a.Name.StartsWith("package/openapi/")
    //                                && !a.Name.StartsWith("package/xml/")
    //                                && a.Name != "package/.index.json"
    //                                && a.Name.Contains("/StructureDefinition-"))
    //                            {
    //                                // Console.WriteLine($"{a.Name}");
    //                                await a.ExtractToFileAsync(Contents.Combine(path, a.Name.Replace("package/", "")), true);
    //                            }
    //                            a = await r.GetNextEntryAsync();
    //                        }
    //                    }
    //                }
    //                else
    //                {
    //                    Console.WriteLine($" - current");
    //                }
    //            }
    //        }
    //    }
    //}

    [Theory(DisplayName = "XVerEmitsComplexTypeArtifacts", Skip = "Tests require external repo - run manually if desired")]
    [Trait("Category", "XVer")]
    [Trait("RequiresExternalRepo", "true")]
    [Trait("ExternalRepo", "HL7/fhir-cross-version")]
    [InlineData("R4", "R5")]
    public void XVerEmitsComplexTypeArtifacts(string srcShort, string tgtShort)
    {
        // This test pins the "widen xver export beyond Resources" fix from
        // scratch/0527-01/. It is gated behind RequiresExternalRepo=true so
        // CI (filter "RequiresExternalRepo!=true") skips it; run locally
        // against a pre-populated ~/.fhir cache after invoking the xver
        // pipeline (e.g.
        //   dotnet run --project src/fhir-codegen/fhir-codegen.csproj --
        //     xver --output-path .\out\xver
        // ).
        //
        // Sentinel complex / metadata types: at least one Profile and one
        // Extension StructureDefinition should be generated per type, plus
        // a top-level type-map ConceptMap, and the Type Lookup page tree.
        string[] sentinelTypes =
        [
            "Dosage", "Address", "HumanName", "Identifier",
            "CodeableReference", "ContactDetail", "Expression", "RelatedArtifact",
        ];

        string outRoot = Path.Combine(FindRelativeDir("out"), "xver");
        Directory.Exists(outRoot).ShouldBeTrue($"Expected xver output at {outRoot}; run the xver CLI first.");

        // Each emitted IG sits under out/xver/<package>/, with input/resources
        // and input/pagecontent subdirectories.
        string[] pkgDirs = Directory.GetDirectories(outRoot,
            $"hl7.fhir.uv.xver-{srcShort.ToLowerInvariant()}.{tgtShort.ToLowerInvariant()}*",
            SearchOption.TopDirectoryOnly);
        pkgDirs.Length.ShouldBeGreaterThan(0, $"No xver package directory found for {srcShort}->{tgtShort}");

        foreach (string pkgDir in pkgDirs)
        {
            string resourcesDir = Path.Combine(pkgDir, "input", "resources");
            string pageContentDir = Path.Combine(pkgDir, "input", "pagecontent");

            Directory.Exists(resourcesDir).ShouldBeTrue($"Missing resources dir under {pkgDir}");
            Directory.Exists(pageContentDir).ShouldBeTrue($"Missing pagecontent dir under {pkgDir}");

            string[] allResourceFiles = Directory.GetFiles(resourcesDir, "*.json", SearchOption.TopDirectoryOnly);

            // Sentinel complex types must each produce at least one extension SD.
            // Per-complex-type profiles are no longer emitted; the type ConceptMap
            // plus per-element extension definitions carry the representation.
            foreach (string sentinel in sentinelTypes)
            {
                allResourceFiles.Any(f => Path.GetFileName(f).Contains(sentinel, StringComparison.OrdinalIgnoreCase) &&
                                          Path.GetFileName(f).Contains("extension", StringComparison.OrdinalIgnoreCase))
                    .ShouldBeTrue($"Expected at least one extension StructureDefinition for complex type {sentinel} under {resourcesDir}");
            }

            // Type-map ConceptMap must be present.
            allResourceFiles.Any(f => Path.GetFileName(f).Contains("type-map-to", StringComparison.OrdinalIgnoreCase))
                .ShouldBeTrue($"Expected a *-type-map-to-* ConceptMap under {resourcesDir}");

            // Type lookup page index.
            File.Exists(Path.Combine(pageContentDir, "index-types.md"))
                .ShouldBeTrue($"Expected index-types.md under {pageContentDir}");

            // Resource lookup page index, with the renamed title.
            string lookupSdPath = Path.Combine(pageContentDir, "index-resources.md");
            File.Exists(lookupSdPath).ShouldBeTrue($"Expected index-resources.md under {pageContentDir}");

            // Exclusion guard: no artifacts for the abstract bases.
            string[] excludedRoots = ["Base", "Element", "BackboneElement", "BackboneType"];
            foreach (string excluded in excludedRoots)
            {
                allResourceFiles.Any(f =>
                {
                    string name = Path.GetFileName(f);
                    return name.StartsWith($"StructureDefinition-{excluded}", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith($"StructureDefinition-extension-{excluded}", StringComparison.OrdinalIgnoreCase);
                }).ShouldBeFalse($"Expected no artifacts for abstract base type {excluded} under {resourcesDir}");
            }
        }
    }

}

public class TestWriter : TextWriter
{
    public ITestOutputHelper OutputWriter { get; }

    public override System.Text.Encoding Encoding => System.Text.Encoding.ASCII;

    public TestWriter(ITestOutputHelper outputWriter)
    {
        OutputWriter = outputWriter;
    }

    private StringBuilder cache = new();

    public override void Write(char value)
    {
        if (value == '\n')
        {
            OutputWriter.WriteLine(cache.ToString());
            cache.Clear();
        }
        else
        {
            cache.Append(value);
        }
    }
    public override void Flush()
    {
        if (cache.Length == 0) return;
        OutputWriter.WriteLine(cache.ToString());
        cache.Clear();
    }
}
