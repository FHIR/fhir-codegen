using System.Diagnostics;
using System.Text;
using Shouldly;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Tests.Extensions;
using Xunit.Abstractions;
using System.CommandLine;
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
}
