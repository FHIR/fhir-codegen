// <copyright file="LaunchUtilsParseTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.CommandLine;
using fhir_codegen_shared;
using Fhir.CodeGen.Lib.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace fhir_codegen.Tests;

public class LaunchUtilsParseTests
{
    private static IConfiguration BuildEnvConfig() => new ConfigurationBuilder().Build();

    private static (RootCommand Root, ParseResult Result) Parse(params string[] args)
    {
        (RootCommand root, ParserConfiguration parserConfig, _) = LaunchUtils.BuildCli(BuildEnvConfig());
        ParseResult pr = root.Parse(args, parserConfig);
        pr.Errors.ShouldBeEmpty(string.Join("; ", pr.Errors.Select(e => e.Message)));
        return (root, pr);
    }

    private static ConfigGenerate ParseGenerate(string language, params string[] args)
    {
        List<string> full = ["generate", language, .. args];
        (_, ParseResult pr) = Parse([.. full]);
        ICodeGenConfig config = LaunchUtils.ParseConfig(pr, "generate", language);
        return (ConfigGenerate)config;
    }

    [Fact]
    public void ParseConfig_GenerateTypeScript_PopulatesRootGenerateAndLanguageOpts()
    {
        ConfigGenerate config = ParseGenerate(
            "TypeScript",
            "--fhir-cache", "C:/tmp/cache",
            "-p", "hl7.fhir.r4.core#4.0.1",
            "--include-experimental",
            "--namespace", "MyNs");

        config.FhirCacheDirectory.ShouldBe("C:/tmp/cache");
        config.IncludeExperimental.ShouldBeTrue();
        config.Packages.ShouldContain("hl7.fhir.r4.core#4.0.1");

        // Language-specific option lives on the TypeScriptOptions subclass.
        System.Reflection.PropertyInfo? nsProp = config.GetType().GetProperty("Namespace");
        nsProp.ShouldNotBeNull();
        nsProp!.GetValue(config).ShouldBe("MyNs");
    }

    [Fact]
    public void ParseConfig_GenerateTypeScript_AcceptsRootOptionAfterSubcommand()
    {
        ConfigGenerate config = ParseGenerate(
            "TypeScript",
            "--fhir-cache", "C:/tmp/cache",
            "-p", "hl7.fhir.r4.core#4.0.1");

        config.FhirCacheDirectory.ShouldBe("C:/tmp/cache");
        config.Packages.ShouldContain("hl7.fhir.r4.core#4.0.1");
    }

    [Fact]
    public void ParseConfig_GenerateTypeScript_AcceptsRootOptionBeforeGenerate()
    {
        (RootCommand _, ParseResult pr) = Parse(
            "--fhir-cache", "C:/tmp/cache",
            "generate", "TypeScript",
            "-p", "hl7.fhir.r4.core#4.0.1");

        ICodeGenConfig config = LaunchUtils.ParseConfig(pr, "generate", "TypeScript");
        ConfigGenerate cg = (ConfigGenerate)config;
        cg.FhirCacheDirectory.ShouldBe("C:/tmp/cache");
        cg.Packages.ShouldContain("hl7.fhir.r4.core#4.0.1");
    }
}
// <copyright file="LaunchUtilsParseTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.CommandLine;
using fhir_codegen_shared;
using Fhir.CodeGen.Lib.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace fhir_codegen.Tests;

public class LaunchUtilsParseTests : IDisposable
{
    private readonly string _tempCacheDir;

    public LaunchUtilsParseTests()
    {
        _tempCacheDir = Path.Combine(
            Path.GetTempPath(),
            "fhir-codegen-tests",
            "launch-utils-parse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempCacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempCacheDir))
            {
                Directory.Delete(_tempCacheDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; do not mask the test outcome.
        }

        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildEnvConfig() => new ConfigurationBuilder().Build();

    private static (RootCommand Root, ParseResult Result) Parse(params string[] args)
    {
        (RootCommand root, ParserConfiguration parserConfig, _) = LaunchUtils.BuildCli(BuildEnvConfig());
        ParseResult pr = root.Parse(args, parserConfig);
        pr.Errors.ShouldBeEmpty(string.Join("; ", pr.Errors.Select(e => e.Message)));
        return (root, pr);
    }

    private static ConfigGenerate ParseGenerate(string language, params string[] args)
    {
        List<string> full = ["generate", language, .. args];
        (_, ParseResult pr) = Parse([.. full]);
        ICodeGenConfig config = LaunchUtils.ParseConfig(pr, "generate", language);
        return (ConfigGenerate)config;
    }

    [Fact]
    public void ParseConfig_GenerateTypeScript_PopulatesRootGenerateAndLanguageOpts()
    {
        ConfigGenerate config = ParseGenerate(
            "TypeScript",
            "--fhir-cache", _tempCacheDir,
            "-p", "hl7.fhir.r4.core#4.0.1",
            "--include-experimental",
            "--namespace", "MyNs");

        config.FhirCacheDirectory.ShouldBe(_tempCacheDir);
        config.IncludeExperimental.ShouldBeTrue();
        config.Packages.ShouldContain("hl7.fhir.r4.core#4.0.1");

        // Language-specific option lives on the TypeScriptOptions subclass.
        System.Reflection.PropertyInfo? nsProp = config.GetType().GetProperty("Namespace");
        nsProp.ShouldNotBeNull();
        nsProp!.GetValue(config).ShouldBe("MyNs");
    }

    [Fact]
    public void ParseConfig_GenerateTypeScript_AcceptsRootOptionAfterSubcommand()
    {
        ConfigGenerate config = ParseGenerate(
            "TypeScript",
            "--fhir-cache", _tempCacheDir,
            "-p", "hl7.fhir.r4.core#4.0.1");

        config.FhirCacheDirectory.ShouldBe(_tempCacheDir);
        config.Packages.ShouldContain("hl7.fhir.r4.core#4.0.1");
    }

    [Fact]
    public void ParseConfig_GenerateTypeScript_AcceptsRootOptionBeforeGenerate()
    {
        (RootCommand _, ParseResult pr) = Parse(
            "--fhir-cache", _tempCacheDir,
            "generate", "TypeScript",
            "-p", "hl7.fhir.r4.core#4.0.1");

        ICodeGenConfig config = LaunchUtils.ParseConfig(pr, "generate", "TypeScript");
        ConfigGenerate cg = (ConfigGenerate)config;
        cg.FhirCacheDirectory.ShouldBe(_tempCacheDir);
        cg.Packages.ShouldContain("hl7.fhir.r4.core#4.0.1");
    }

    [Fact]
    public void ParseConfig_DocsCli_PopulatesOutputPath()
    {
        (RootCommand _, ParseResult pr) = Parse(
            "docs", "cli",
            "--output", "tmp/cli.md");

        ICodeGenConfig config = LaunchUtils.ParseConfig(pr, "docs", "cli");
        ConfigDocs docs = config.ShouldBeOfType<ConfigDocs>();
        docs.OutputPath.ShouldBe("tmp/cli.md");
    }

    [Fact]
    public void ParseConfig_DocsCli_DefaultsOutputPathWhenOmitted()
    {
        (RootCommand _, ParseResult pr) = Parse("docs", "cli");

        ICodeGenConfig config = LaunchUtils.ParseConfig(pr, "docs", "cli");
        ConfigDocs docs = config.ShouldBeOfType<ConfigDocs>();
        docs.OutputPath.ShouldBe(ConfigDocs.DefaultOutputPath);
    }
}
