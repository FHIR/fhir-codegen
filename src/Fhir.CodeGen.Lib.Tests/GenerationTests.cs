// <copyright file="GenerationTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>


using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Fhir.CodeGen.Lib.Language;
using Fhir.CodeGen.Lib.Language.Firely;
using Fhir.CodeGen.Lib.Language.Info;
using Fhir.CodeGen.Lib.Language.OpenApi;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Lib.Tests.Extensions;
using Fhir.CodeGen.Common.Packaging;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Fhir.CodeGen.Lib.Tests;

public class GenerationTestBase : IDisposable
{
    /// <summary>True to write generated files.</summary>
    internal static bool WriteGeneratedFiles = false;

    internal const string? CachePath = null;

    private bool _disposedValue = false;

    public GenerationTestBase()
    {
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
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

    /// <summary>CompareValueSets generation.</summary>
    /// <exception cref="ArgumentException">Thrown when one or more arguments have unsupported or
    ///  illegal values.</exception>
    /// <param name="existingPath">        Full pathname of the existing file.</param>
    /// <param name="currentMS">           The current milliseconds.</param>
    /// <param name="compareLinesDirectly">(Optional) True to compare lines directly, false to compare set of line hashes.</param>
    internal static void CompareGeneration(
        string existingPath,
        MemoryStream currentMS,
        bool compareLinesDirectly = true)
    {
        // make sure the MS is up to date and at the beginning
        currentMS.Flush();
        currentMS.Seek(0, SeekOrigin.Begin);

        if (WriteGeneratedFiles)
        {
            using (StreamReader sr = new(currentMS))
            {
                string current = sr.ReadToEnd();

                File.WriteAllText(existingPath, current);
                Assert.Fail("Generated files updated, please re-run the test");
            }

            return;
        }

        if (!Path.IsPathRooted(existingPath))
        {
            existingPath = Path.Combine(Directory.GetCurrentDirectory(), existingPath);
            existingPath = Path.GetFullPath(existingPath);
        }

        if (!File.Exists(existingPath))
        {
            throw new ArgumentException($"Could not find file at path: {existingPath}");
        }

        string version = FileVersionInfo.GetVersionInfo(Assembly.GetAssembly(typeof(DefinitionCollection))!.Location).ProductVersion?.ToString() ?? string.Empty;

        if (version.Contains('+'))
        {
            version = version.Substring(0, version.IndexOf('+'));
        }

        if (existingPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            JToken expected = JToken.Parse(File.ReadAllText(existingPath));

            // Select the token you want to remove
            JToken? tokenToRemove = expected.SelectToken("info.version");
            tokenToRemove?.Parent?.Remove();

            using StreamReader msReader = new(currentMS);
            string currentString = msReader.ReadToEnd();
            JToken actual = JToken.Parse(currentString);

            tokenToRemove = actual.SelectToken("info.version");
            tokenToRemove?.Parent?.Remove();

            actual.ShouldBeEquivalentTo(expected);

            return;
        }

        using FileStream existingFS = new(existingPath, FileMode.Open, FileAccess.Read);

        // compare files line by line
        using StreamReader existingReader = new(existingFS);
        using StreamReader currentReader = new(currentMS);

        HashSet<string> existingLines = [];
        HashSet<string> currentLines = [];

        int i = 0;
        bool done = false;
        while (!done)
        {
            string? existingLine = existingReader.ReadLine();
            string? currentLine = currentReader.ReadLine();

            if ((existingLine == null) && (currentLine == null))
            {
                done = true;
                break;
            }

            if (existingLine == null)
            {
                Assert.Fail($"Failed to read line {i} in existing file!");
                return;
            }

            if (currentLine == null)
            {
                Assert.Fail($"Failed to read line {i} in current file!");
                return;
            }

            i++;
            if (currentLine.Contains(version))
            {
                // skip any lines with a version in them
                continue;
            }

            if (compareLinesDirectly)
            {
                currentLine.ShouldBe(existingLine, $"Line {i} found:\n\t{currentLine}\nexpected:\n\t{existingLine}");
            }
            else
            {
                existingLines.Add(existingLine.TrimEnd());
                currentLines.Add(currentLine.TrimEnd());
            }
        }

        if (!compareLinesDirectly)
        {
            currentLines.Count().ShouldBe(existingLines.Count(), "Line count does not match");

            currentLines.ExceptWith(existingLines);

            currentLines.ShouldBeEmpty("Lines do not match:\n" + string.Join("\n", currentLines));
        }
    }

    internal static void CompareGenerationHashes(string existingPath, Dictionary<string, string> current)
    {
        if (WriteGeneratedFiles)
        {
            File.WriteAllText(existingPath, JsonSerializer.Serialize(current));
            Assert.Fail("Hash file updated, please re-run the test");

            return;
        }

        if (!File.Exists(existingPath))
        {
            throw new ArgumentException($"Could not find file at path: {existingPath}");
        }

        string json = File.ReadAllText(existingPath);

        Dictionary<string, string>? previous = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        previous.ShouldNotBeNull();
        if (previous == null)
        {
            return;
        }

        previous.Count.ShouldBe(current.Count);

        foreach ((string currentPath, string hash) in current)
        {
            string path = currentPath.Contains('/')
                ? currentPath.Replace('/', '\\')
                : currentPath;

            _ = previous.TryGetValue(path, out string? previousHash);

            hash.ShouldBeEquivalentTo(previousHash, $"Hashes do not match for {path}!");
        }
    }
}

public class GenerationTestsR5 : GenerationTestBase
{
    [Theory]
    [InlineData("Info", "TestData/Generated/Info-R5.txt")]
    [InlineData("TypeScript", "TestData/Generated/TypeScript-R5.ts")]
    [Trait("Category", "Generation")]
    [Trait("FhirVersion", "R5")]
    internal async Task TestLangR5(string langName, string filePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR5);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R5");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

        using (MemoryStream ms = new())
        {
            switch (langName)
            {
                case "Info":
                    {
                        LangInfo exportLang = new();
                        LangInfo.InfoOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;

                case "TypeScript":
                    {
                        TypeScript exportLang = new();
                        TypeScript.TypeScriptOptions options = new()
                        {
                            WriteStream = ms,
                        };
                        exportLang.Export(options, dc);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown language: {langName}");
            }

            CompareGeneration(path, ms);
        }
    }

    [Theory(DisplayName = "TestFirelyHashesR5", Skip = "Firely Generation is not currently deterministic - these tests are not able to succeeed")]
    [InlineData(CSharpFirelyCommon.GenSubset.Base, "TestData/Hashes/CSharpFirely2-R5-Base.json")]
    [InlineData(CSharpFirelyCommon.GenSubset.Conformance, "TestData/Hashes/CSharpFirely2-R5-Conformance.json")]
    [InlineData(CSharpFirelyCommon.GenSubset.Satellite, "TestData/Hashes/CSharpFirely2-R5-Satellite.json")]
    [Trait("Category", "Generation")]
    [Trait("Comparison", "Hash")]
    [Trait("FhirVersion", "R5")]
    internal async Task TestFirelyHashesR5(CSharpFirelyCommon.GenSubset subset, string comparisonFilePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR5);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R5");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(comparisonFilePath)
            ? comparisonFilePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), comparisonFilePath);

        FirelyGenOptions options = new()
        {
            Subset = subset,
        };
        CSharpFirely2 exportLang = new();

        IFileHashTestable langHashTestable = exportLang;

        langHashTestable.GenerateHashesInsteadOfOutput = true;

        exportLang.Export(options, dc);

        CompareGenerationHashes(path, langHashTestable.FileHashes);
    }
}

public class GenerationTestsR4B : GenerationTestBase
{
    [Theory]
    [InlineData("Info", "TestData/Generated/Info-R4B.txt")]
    [InlineData("TypeScript", "TestData/Generated/TypeScript-R4B.ts")]
    [Trait("Category", "Generation")]
    [Trait("FhirVersion", "R4B")]
    internal async Task TestLangR4B(string langName, string filePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR4B);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R4B");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

        if (!File.Exists(path))
        {
            throw new ArgumentException($"Could not find file at path: {path}");
        }

        using (MemoryStream ms = new())
        {
            switch (langName)
            {
                case "Info":
                    {
                        LangInfo exportLang = new();
                        LangInfo.InfoOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;

                case "TypeScript":
                    {
                        TypeScript exportLang = new();
                        TypeScript.TypeScriptOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown language: {langName}");
            }

            CompareGeneration(path, ms);
        }
    }

    [Theory]
    [InlineData(CSharpFirelyCommon.GenSubset.Satellite, "TestData/Hashes/CSharpFirely2-R4B-Satellite.json")]
    [Trait("Category", "Generation")]
    [Trait("Comparison", "Hash")]
    [Trait("FhirVersion", "R4B")]
    internal async Task TestFirelyHashesR4B(CSharpFirelyCommon.GenSubset subset, string comparisonFilePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR4B);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R4B");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(comparisonFilePath)
            ? comparisonFilePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), comparisonFilePath);

        FirelyGenOptions options = new()
        {
            Subset = subset,
        };
        CSharpFirely2 exportLang = new();

        IFileHashTestable langHashTestable = exportLang;

        langHashTestable.GenerateHashesInsteadOfOutput = true;

        exportLang.Export(options, dc);

        CompareGenerationHashes(path, langHashTestable.FileHashes);
    }
}

public class GenerationTestsR4 : GenerationTestBase
{
    [Theory]
    [InlineData("Info", "TestData/Generated/Info-R4.txt", "")]
    [InlineData("TypeScript", "TestData/Generated/TypeScript-R4.ts", "")]
    //[InlineData("OpenApi-Json-Inline-None", "TestData/Generated/OpenApi-R4-Inline-None.json")]
    [InlineData("OpenApi-Json-Inline-None-Candle-Filtered", "TestData/Generated/OpenApi-R4-Inline-None-Candle-Filtered.json", "TestData/R4/CapabilityStatement-candle-local.json")]
    [InlineData("OpenApi-Yaml-Inline-None-Candle-Filtered", "TestData/Generated/OpenApi-R4-Inline-None-Candle-Filtered.yaml", "TestData/R4/CapabilityStatement-candle-local.json")]
    [InlineData("OpenApi-Yaml-Inline-Names-Candle-Filtered", "TestData/Generated/OpenApi-R4-Inline-Names-Candle-Filtered.yaml", "TestData/R4/CapabilityStatement-candle-local.json")]
    [InlineData("OpenApi-Yaml-Inline-Detailed-Candle-Filtered", "TestData/Generated/OpenApi-R4-Inline-Detailed-Candle-Filtered.yaml", "TestData/R4/CapabilityStatement-candle-local.json")]
    [Trait("Category", "Generation")]
    [Trait("FhirVersion", "R4")]
    internal async Task TestLangR4(string langName, string filePath, string? capabilityJsonPath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR4);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R4");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

        if (!File.Exists(path))
        {
            throw new ArgumentException($"Could not find file at path: {path}");
        }

        capabilityJsonPath = string.IsNullOrEmpty(capabilityJsonPath)
            ? string.Empty
            : Path.IsPathRooted(capabilityJsonPath)
                ? capabilityJsonPath
                : Path.GetRelativePath(Directory.GetCurrentDirectory(), capabilityJsonPath);

        bool compareLinesDirectly = true;

        using (MemoryStream ms = new())
        {
            switch (langName)
            {
                case "Info":
                    {
                        LangInfo exportLang = new();
                        LangInfo.InfoOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;

                case "TypeScript":
                    {
                        TypeScript exportLang = new();
                        TypeScript.TypeScriptOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;

                case "OpenApi-Json-Inline-None":
                    {
                        // note that we can only use the WriteStream for OpenApi if MultiFile is off
                        OpenApiOptions options = new()
                        {
                            WriteStream = ms,
                            MultiFile = false,
                            OpenApiVersion = OpenApiCommon.OaVersion.v3,
                            FileFormat = OpenApiCommon.OaFileFormat.JSON,
                            SchemaStyle = OpenApiCommon.OaSchemaStyleCodes.Inline,
                            SchemaLevel = OpenApiCommon.OaSchemaLevelCodes.None,
                        };

                        LangOpenApi exportLang = new();
                        exportLang.Export(options, dc);
                    }
                    break;

                case "OpenApi-Json-Inline-None-Candle-Filtered":
                    {
                        if (string.IsNullOrEmpty(capabilityJsonPath))
                        {
                            throw new ArgumentException($"Missing csPath for {langName}");
                        }

                        object? csObj = loader.ParseContents43("application/fhir+json", capabilityJsonPath);

                        Hl7.Fhir.Model.CapabilityStatement? cs = csObj is Hl7.Fhir.Model.CapabilityStatement c
                            ? c
                            : throw new ArgumentException("Failed to parse CapabilityStatement");

                        // note that we can only use the WriteStream for OpenApi if MultiFile is off
                        OpenApiOptions options = new()
                        {
                            WriteStream = ms,
                            MultiFile = false,
                            OpenApiVersion = OpenApiCommon.OaVersion.v3,
                            FileFormat = OpenApiCommon.OaFileFormat.JSON,
                            SchemaStyle = OpenApiCommon.OaSchemaStyleCodes.Inline,
                            SchemaLevel = OpenApiCommon.OaSchemaLevelCodes.None,
                            ServerCapabilities = cs,
                            ExportKeys = [ "Patient", "Observation" ]
                        };

                        LangOpenApi exportLang = new();
                        exportLang.Export(options, dc);
                    }
                    break;

                case "OpenApi-Yaml-Inline-None-Candle-Filtered":
                    {
                        if (string.IsNullOrEmpty(capabilityJsonPath))
                        {
                            throw new ArgumentException($"Missing csPath for {langName}");
                        }

                        object? csObj = loader.ParseContents43("application/fhir+json", capabilityJsonPath);

                        Hl7.Fhir.Model.CapabilityStatement? cs = csObj is Hl7.Fhir.Model.CapabilityStatement c
                            ? c
                            : throw new ArgumentException("Failed to parse CapabilityStatement");

                        // note that we can only use the WriteStream for OpenApi if MultiFile is off
                        OpenApiOptions options = new()
                        {
                            WriteStream = ms,
                            MultiFile = false,
                            OpenApiVersion = OpenApiCommon.OaVersion.v3,
                            FileFormat = OpenApiCommon.OaFileFormat.YAML,
                            SchemaStyle = OpenApiCommon.OaSchemaStyleCodes.Inline,
                            SchemaLevel = OpenApiCommon.OaSchemaLevelCodes.None,
                            ServerCapabilities = cs,
                            ExportKeys = ["Patient", "Observation"]
                        };

                        LangOpenApi exportLang = new();
                        exportLang.Export(options, dc);
                        compareLinesDirectly = false;
                    }
                    break;

                case "OpenApi-Yaml-Inline-Names-Candle-Filtered":
                    {
                        if (string.IsNullOrEmpty(capabilityJsonPath))
                        {
                            throw new ArgumentException($"Missing csPath for {langName}");
                        }

                        object? csObj = loader.ParseContents43("application/fhir+json", capabilityJsonPath);

                        Hl7.Fhir.Model.CapabilityStatement? cs = csObj is Hl7.Fhir.Model.CapabilityStatement c
                            ? c
                            : throw new ArgumentException("Failed to parse CapabilityStatement");

                        // note that we can only use the WriteStream for OpenApi if MultiFile is off
                        OpenApiOptions options = new()
                        {
                            WriteStream = ms,
                            MultiFile = false,
                            OpenApiVersion = OpenApiCommon.OaVersion.v3,
                            FileFormat = OpenApiCommon.OaFileFormat.YAML,
                            SchemaStyle = OpenApiCommon.OaSchemaStyleCodes.Inline,
                            SchemaLevel = OpenApiCommon.OaSchemaLevelCodes.Names,
                            ServerCapabilities = cs,
                            ExportKeys = ["Patient", "Observation"]
                        };

                        LangOpenApi exportLang = new();
                        exportLang.Export(options, dc);
                        compareLinesDirectly = false;
                    }
                    break;

                case "OpenApi-Yaml-Inline-Detailed-Candle-Filtered":
                    {
                        if (string.IsNullOrEmpty(capabilityJsonPath))
                        {
                            throw new ArgumentException($"Missing csPath for {langName}");
                        }

                        object? csObj = loader.ParseContents43("application/fhir+json", capabilityJsonPath);

                        Hl7.Fhir.Model.CapabilityStatement? cs = csObj is Hl7.Fhir.Model.CapabilityStatement c
                            ? c
                            : throw new ArgumentException("Failed to parse CapabilityStatement");

                        // note that we can only use the WriteStream for OpenApi if MultiFile is off
                        OpenApiOptions options = new()
                        {
                            WriteStream = ms,
                            MultiFile = false,
                            OpenApiVersion = OpenApiCommon.OaVersion.v3,
                            FileFormat = OpenApiCommon.OaFileFormat.YAML,
                            SchemaStyle = OpenApiCommon.OaSchemaStyleCodes.Inline,
                            SchemaLevel = OpenApiCommon.OaSchemaLevelCodes.Detailed,
                            ServerCapabilities = cs,
                            ExportKeys = ["Patient", "Observation"]
                        };

                        LangOpenApi exportLang = new();
                        exportLang.Export(options, dc);
                        compareLinesDirectly = false;
                    }
                    break;

                default:
                    throw new ArgumentException($"Unknown language: {langName}");
            }

            CompareGeneration(path, ms, compareLinesDirectly);
        }
    }

    [Theory(DisplayName = "TestFirelyHashesR4", Skip = "Firely Generation is not currently deterministic - these tests are not able to succeeed")]
    [InlineData(CSharpFirelyCommon.GenSubset.Satellite, "TestData/Hashes/CSharpFirely2-R4-Satellite.json")]
    [Trait("Category", "Generation")]
    [Trait("Comparison", "Hash")]
    [Trait("FhirVersion", "R4")]
    internal async Task TestFirelyHashesR4(CSharpFirelyCommon.GenSubset subset, string comparisonFilePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR4);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R4");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(comparisonFilePath)
            ? comparisonFilePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), comparisonFilePath);

        FirelyGenOptions options = new()
        {
            Subset = subset,
        };
        CSharpFirely2 exportLang = new();

        IFileHashTestable langHashTestable = exportLang;

        langHashTestable.GenerateHashesInsteadOfOutput = true;

        exportLang.Export(options, dc);

        CompareGenerationHashes(path, langHashTestable.FileHashes);
    }
}

public class GenerationTestsR3 : GenerationTestBase
{
    [Theory]
    [InlineData("Info", "TestData/Generated/Info-R3.txt")]
    [InlineData("TypeScript", "TestData/Generated/TypeScript-R3.ts")]
    [Trait("Category", "Generation")]
    [Trait("FhirVersion", "R3")]
    internal async Task TestLangR3(string langName, string filePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR3);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R3");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

        using (MemoryStream ms = new())
        {
            switch (langName)
            {
                case "Info":
                    {
                        LangInfo exportLang = new();
                        LangInfo.InfoOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;

                case "TypeScript":
                    {
                        TypeScript exportLang = new();
                        TypeScript.TypeScriptOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown language: {langName}");
            }

            CompareGeneration(path, ms);
        }
    }

    [Theory(DisplayName = "TestFirelyHashesR3", Skip = "Firely Generation is not currently deterministic - these tests are not able to succeeed")]
    [InlineData(CSharpFirelyCommon.GenSubset.Satellite, "TestData/Hashes/CSharpFirely2-R3-Satellite.json")]
    [Trait("Category", "Generation")]
    [Trait("Comparison", "Hash")]
    [Trait("FhirVersion", "R3")]
    internal async Task TestFirelyHashesR3(CSharpFirelyCommon.GenSubset subset, string comparisonFilePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR3);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R3");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(comparisonFilePath)
            ? comparisonFilePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), comparisonFilePath);

        FirelyGenOptions options = new()
        {
            Subset = subset,
        };
        CSharpFirely2 exportLang = new();

        IFileHashTestable langHashTestable = exportLang;

        langHashTestable.GenerateHashesInsteadOfOutput = true;

        exportLang.Export(options, dc);

        CompareGenerationHashes(path, langHashTestable.FileHashes);
    }
}

public class GenerationTestsR2 : GenerationTestBase
{
    [Theory]
    [InlineData("Info", "TestData/Generated/Info-R2.txt")]
    [InlineData("TypeScript", "TestData/Generated/TypeScript-R2.ts")]
    [Trait("Category", "Generation")]
    [Trait("FhirVersion", "R2")]
    internal async Task TestLangR2(string langName, string filePath)
    {
        PackageLoader loader = new(new() { FhirCacheDirectory = CachePath });
        DefinitionCollection? dc = await loader.LoadPackages(TestCommon.EntriesR2);
        if (dc == null)
        {
            throw new Exception("Failed to load FHIR R2");
        }

        // Get the absolute path to the file
        string path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

        string data = File.ReadAllText(path);

        using (MemoryStream ms = new())
        {
            switch (langName)
            {
                case "Info":
                    {
                        LangInfo exportLang = new();
                        LangInfo.InfoOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;

                case "TypeScript":
                    {
                        TypeScript exportLang = new();
                        TypeScript.TypeScriptOptions options = new()
                        {
                            WriteStream = ms
                        };
                        exportLang.Export(options, dc);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown language: {langName}");
            }

            CompareGeneration(path, ms);
        }
    }
}
