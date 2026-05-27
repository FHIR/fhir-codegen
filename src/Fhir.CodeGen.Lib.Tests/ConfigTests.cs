using System.Diagnostics;
using System.Text;
using Shouldly;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Tests.Extensions;
using Xunit.Abstractions;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

namespace Fhir.CodeGen.Lib.Tests;

public class ConfigTests
{
    [Fact]
    public void TestParseCliInt()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        // build our root command
        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            // note that 'global' here is just recursive DOWNWARD
            rootCommand.AddGlobalOption(co.CliOption);
        }

        Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();

        string[] args = ["--max-expansion-size", "2"];

        // attempt a parse
        ParseResult pr = parser.Parse(args);

        ConfigRoot config = new();

        // parse the arguments into the configuration object
        config.Parse(pr);

        // check our value
        config.MaxExpansionSize.ShouldBe(2);
    }

    [Fact]
    public void TestParseCliString()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        // build our root command
        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            // note that 'global' here is just recursive DOWNWARD
            rootCommand.AddGlobalOption(co.CliOption);
        }

        Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();

        string[] args = ["--output-filename", "a.file"];

        // attempt a parse
        ParseResult pr = parser.Parse(args);

        ConfigRoot config = new();

        // parse the arguments into the configuration object
        config.Parse(pr);

        // check our value
        config.OutputFilename.ShouldBe("a.file");
    }

    [Fact]
    public void TestParseCliBool()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        // build our root command
        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            // note that 'global' here is just recursive DOWNWARD
            rootCommand.AddGlobalOption(co.CliOption);
        }

        Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();

        string[] args = ["--use-official-registries"];

        // attempt a parse
        ParseResult pr = parser.Parse(args);

        ConfigRoot config = new();

        // parse the arguments into the configuration object
        config.Parse(pr);

        // check our value
        config.UseOfficialRegistries.ShouldBe(true);
    }

    [Fact]
    public void TestParseCliBoolTrue()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        // build our root command
        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            // note that 'global' here is just recursive DOWNWARD
            rootCommand.AddGlobalOption(co.CliOption);
        }

        Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();

        string[] args = ["--use-official-registries", "true"];

        // attempt a parse
        ParseResult pr = parser.Parse(args);

        ConfigRoot config = new();

        // parse the arguments into the configuration object
        config.Parse(pr);

        // check our value
        config.UseOfficialRegistries.ShouldBe(true);
    }


    [Fact]
    public void TestParseCliBoolFalse()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        // build our root command
        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            // note that 'global' here is just recursive DOWNWARD
            rootCommand.AddGlobalOption(co.CliOption);
        }

        Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();

        string[] args = ["--use-official-registries", "false"];

        // attempt a parse
        ParseResult pr = parser.Parse(args);

        ConfigRoot config = new();

        // parse the arguments into the configuration object
        config.Parse(pr);

        // check our value
        config.UseOfficialRegistries.ShouldBe(false);
    }

    [Fact]
    public void TestParseCliStringArray()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        // build our root command
        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            // note that 'global' here is just recursive DOWNWARD
            rootCommand.AddGlobalOption(co.CliOption);
        }

        Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();

        string[] args = ["--additional-fhir-registry-urls", "http://a.co/", "--additional-fhir-registry-urls", "http://b.co"];

        // attempt a parse
        ParseResult pr = parser.Parse(args);

        ConfigRoot config = new();

        // parse the arguments into the configuration object
        config.Parse(pr);

        // check our value
        config.AdditionalFhirRegistryUrls.Length.ShouldBe(2);
        config.AdditionalFhirRegistryUrls.Any(v => v == "http://a.co/").ShouldBe(true);
        config.AdditionalFhirRegistryUrls.Any(v => v == "http://b.co").ShouldBe(true);
    }

    /// <summary>
    /// Builds a parser with all <see cref="ConfigRoot"/> options registered
    /// as global options, mirroring the pattern used by the other tests in
    /// this file.
    /// </summary>
    private static Parser BuildRootParser()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            rootCommand.AddGlobalOption(co.CliOption);
        }

        return new CommandLineBuilder(rootCommand).UseDefaults().Build();
    }

    /// <summary>
    /// Captures everything written to <see cref="Console.Out"/> during
    /// <paramref name="action"/> and returns it.
    /// </summary>
    private static string CaptureConsoleOut(Action action)
    {
        TextWriter original = Console.Out;
        StringWriter capture = new();
        try
        {
            Console.SetOut(capture);
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return capture.ToString();
    }

    [Fact]
    public void Parse_WithMissingDefaultFhirCache_DoesNotThrow_AndUsesUserProfileDefault()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        try
        {
            Parser parser = BuildRootParser();
            ParseResult pr = parser.Parse([]);

            TestConfigRoot config = new() { ProfileDir = tempProfile };

            string output = CaptureConsoleOut(() => Should.NotThrow(() => config.Parse(pr)));

            config.FhirCacheDirectory.ShouldBe(Path.Combine(tempProfile, ".fhir", "packages"));
            output.ShouldNotContain("Warning: --fhir-cache");
        }
        finally
        {
            Directory.Delete(tempProfile, recursive: true);
        }
    }

    [Fact]
    public void Parse_WithExplicitMissingRelativeFhirCache_DoesNotThrow_AndFallsBackAndWarns()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        const string missing = "definitely-not-a-real-dir-xyz";

        try
        {
            Parser parser = BuildRootParser();
            ParseResult pr = parser.Parse(["--fhir-cache", missing]);

            TestConfigRoot config = new() { ProfileDir = tempProfile };

            string output = CaptureConsoleOut(() => Should.NotThrow(() => config.Parse(pr)));

            config.FhirCacheDirectory.ShouldNotBeNull();
            config.FhirCacheDirectory!.ShouldEndWith(missing);
            output.ShouldContain($"Warning: --fhir-cache value '{missing}' did not resolve; using as-is.");
        }
        finally
        {
            Directory.Delete(tempProfile, recursive: true);
        }
    }

    [Fact]
    public void Parse_WithExplicitRootedFhirCache_PassesValueThrough()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        // Rooted path that does NOT exist on disk; we never create it.
        string rooted = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests-no-such-dir-" + Path.GetRandomFileName());

        try
        {
            Parser parser = BuildRootParser();
            ParseResult pr = parser.Parse(["--fhir-cache", rooted]);

            TestConfigRoot config = new() { ProfileDir = tempProfile };

            string output = CaptureConsoleOut(() => Should.NotThrow(() => config.Parse(pr)));

            config.FhirCacheDirectory.ShouldBe(rooted);
            output.ShouldNotContain("Warning: --fhir-cache");
        }
        finally
        {
            Directory.Delete(tempProfile, recursive: true);
        }
    }

    [Fact]
    public void Parse_WithExplicitRelativeFhirCacheThatResolves_DoesNotWarn()
    {
        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        // Create a relative directory under AppContext.BaseDirectory so that
        // FindRelativeDir(string.Empty, leafName) finds it on the first try
        // (testDir = AppContext.BaseDirectory + leafName, exists → returned).
        string baseDir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        string leafName = "fhir-codegen-tests-rel-" + Path.GetRandomFileName();
        string createdDir = Path.Combine(baseDir, leafName);
        Directory.CreateDirectory(createdDir);

        try
        {
            Parser parser = BuildRootParser();
            ParseResult pr = parser.Parse(["--fhir-cache", leafName]);

            TestConfigRoot config = new() { ProfileDir = tempProfile };

            string output = CaptureConsoleOut(() => Should.NotThrow(() => config.Parse(pr)));

            config.FhirCacheDirectory.ShouldNotBeNull();
            config.FhirCacheDirectory!.ShouldBe(Path.GetFullPath(createdDir));
            output.ShouldNotContain("Warning: --fhir-cache");
        }
        finally
        {
            Directory.Delete(createdDir, recursive: true);
            Directory.Delete(tempProfile, recursive: true);
        }
    }

    /// <summary>
    /// Test-only <see cref="ConfigRoot"/> subclass that overrides the
    /// user-profile lookup so the cache-resolution tests can use a temp
    /// directory without mutating process-global env vars.
    /// </summary>
    private sealed class TestConfigRoot : ConfigRoot
    {
        public string ProfileDir { get; init; } = string.Empty;

        protected override string GetUserProfileDirectory() => ProfileDir;
    }
}
