using System.Diagnostics;
using System.Text;
using Shouldly;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Tests.Extensions;
using Xunit.Abstractions;
using System.CommandLine;

namespace Fhir.CodeGen.Lib.Tests;

[Collection("EnvVarSerial")]
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
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();

        string[] args = ["--max-expansion-size", "2"];

        // attempt a parse
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);

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
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();

        string[] args = ["--output-filename", "a.file"];

        // attempt a parse
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);

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
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();

        string[] args = ["--use-official-registries"];

        // attempt a parse
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);

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
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();

        string[] args = ["--use-official-registries", "true"];

        // attempt a parse
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);

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
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();

        string[] args = ["--use-official-registries", "false"];

        // attempt a parse
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);

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
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();

        string[] args = ["--additional-fhir-registry-urls", "http://a.co/", "--additional-fhir-registry-urls", "http://b.co"];

        // attempt a parse
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);

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
    /// as recursive (globally visible) options, mirroring the pattern used
    /// by the other tests in this file.
    /// </summary>
    private static (RootCommand Root, ParserConfiguration Config) BuildRootParser()
    {
        ConfigurationOption[] configurationOptions = (new ConfigRoot()).GetOptions();

        RootCommand rootCommand = new("Root command for unit testing.");
        foreach (ConfigurationOption co in configurationOptions)
        {
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();
        return (rootCommand, parserConfig);
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
        using EnvVarScope envScope = new("Fhir_Cache", null);

        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        try
        {
            (RootCommand root, ParserConfiguration parserConfig) = BuildRootParser();
            ParseResult pr = root.Parse([], parserConfig);

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
        using EnvVarScope envScope = new("Fhir_Cache", null);

        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        const string missing = "definitely-not-a-real-dir-xyz";

        try
        {
            (RootCommand root, ParserConfiguration parserConfig) = BuildRootParser();
            ParseResult pr = root.Parse(["--fhir-cache", missing], parserConfig);

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
        using EnvVarScope envScope = new("Fhir_Cache", null);

        string tempProfile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempProfile);

        // Rooted path that does NOT exist on disk; we never create it.
        string rooted = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests-no-such-dir-" + Path.GetRandomFileName());

        try
        {
            (RootCommand root, ParserConfiguration parserConfig) = BuildRootParser();
            ParseResult pr = root.Parse(["--fhir-cache", rooted], parserConfig);

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
        using EnvVarScope envScope = new("Fhir_Cache", null);

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
            (RootCommand root, ParserConfiguration parserConfig) = BuildRootParser();
            ParseResult pr = root.Parse(["--fhir-cache", leafName], parserConfig);

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

    /// <summary>
    /// Disposable scope that sets and restores a single env var, used to
    /// isolate the FhirCache resolution tests below from any pre-existing
    /// <c>Fhir_Cache</c> setting on the developer's machine. Mirrors the
    /// helper in <see cref="ConfigPrecedenceTests"/>.
    /// </summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
