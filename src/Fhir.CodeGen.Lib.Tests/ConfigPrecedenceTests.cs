// <copyright file="ConfigPrecedenceTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.CommandLine;
using Fhir.CodeGen.Lib.Configuration;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

/// <summary>
/// Pins down the configuration-source precedence contract that
/// <see cref="ConfigRoot.GetOpt{T}"/> and <see cref="ConfigRoot.GetOptArray{T}"/>
/// implement under the System.CommandLine 2.0 GA + D1(b) shape.
/// </summary>
/// <remarks>
/// Reachable precedence (low -&gt; high): <c>ConfigurationOption.DefaultValue</c>
/// &lt; environment variable &lt; CLI argument.
///
/// <para>
/// <c>appsettings.json</c> values are not currently surfaced into option defaults
/// under D1(b) — see Phase 5 deviation in <c>scratch/0424-05/plan.md</c>. If
/// D1(a) (per-option ApplyDefault lambdas) is later adopted, this fixture
/// should be extended with the appsettings.json cells.
/// </para>
///
/// <para>
/// Env-var mutation is process-wide; tests in this class are serialized by the
/// <c>EnvVarSerial</c> collection.
/// </para>
/// </remarks>
[Collection("EnvVarSerial")]
public class ConfigPrecedenceTests
{
    /// <summary>Disposable scope that sets and restores a single env var.</summary>
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

    /// <summary>Builds a minimal root command + parses args into a fresh ConfigRoot.</summary>
    private static ConfigRoot ParseArgs(params string[] args)
    {
        ConfigRoot config = new();
        ConfigurationOption[] opts = config.GetOptions();

        RootCommand rootCommand = new("Precedence test root.");
        foreach (ConfigurationOption co in opts)
        {
            co.CliOption.Recursive = true;
            rootCommand.Options.Add(co.CliOption);
        }

        ParserConfiguration parserConfig = new();
        System.CommandLine.ParseResult pr = rootCommand.Parse(args, parserConfig);
        config.Parse(pr);
        return config;
    }

    // ---- MaxExpansionSize (int, EnvVar = Max_Expansion_Size) -----------------

    [Fact]
    public void Precedence_MaxExpansionSize_NoSources_Resolves_Default()
    {
        ConfigRoot config = ParseArgs();
        config.MaxExpansionSize.ShouldBe(ConfigRoot.DefaultMaxExpansionSize);
    }

    [Fact]
    public void Precedence_MaxExpansionSize_EnvOnly_Resolves_Env()
    {
        using EnvVarScope _ = new("Max_Expansion_Size", "4242");
        ConfigRoot config = ParseArgs();
        config.MaxExpansionSize.ShouldBe(4242);
    }

    [Fact]
    public void Precedence_MaxExpansionSize_CliOnly_Resolves_Cli()
    {
        ConfigRoot config = ParseArgs("--max-expansion-size", "7");
        config.MaxExpansionSize.ShouldBe(7);
    }

    [Fact]
    public void Precedence_MaxExpansionSize_EnvAndCli_CliWins()
    {
        using EnvVarScope _ = new("Max_Expansion_Size", "4242");
        ConfigRoot config = ParseArgs("--max-expansion-size", "7");
        config.MaxExpansionSize.ShouldBe(7);
    }

    // ---- OutputFilename (string, EnvVar = Output_Filename) --------------------

    [Fact]
    public void Precedence_OutputFilename_NoSources_Resolves_Default()
    {
        ConfigRoot config = ParseArgs();
        config.OutputFilename.ShouldBe(string.Empty);
    }

    [Fact]
    public void Precedence_OutputFilename_EnvOnly_Resolves_Env()
    {
        using EnvVarScope _ = new("Output_Filename", "from-env.txt");
        ConfigRoot config = ParseArgs();
        config.OutputFilename.ShouldBe("from-env.txt");
    }

    [Fact]
    public void Precedence_OutputFilename_CliOnly_Resolves_Cli()
    {
        ConfigRoot config = ParseArgs("--output-filename", "from-cli.txt");
        config.OutputFilename.ShouldBe("from-cli.txt");
    }

    [Fact]
    public void Precedence_OutputFilename_EnvAndCli_CliWins()
    {
        using EnvVarScope _ = new("Output_Filename", "from-env.txt");
        ConfigRoot config = ParseArgs("--output-filename", "from-cli.txt");
        config.OutputFilename.ShouldBe("from-cli.txt");
    }

    // ---- UseOfficialRegistries (bool, EnvVar = Use_Official_Registries) -------

    [Fact]
    public void Precedence_UseOfficialRegistries_NoSources_Resolves_Default()
    {
        ConfigRoot config = ParseArgs();
        config.UseOfficialRegistries.ShouldBeTrue();
    }

    [Fact]
    public void Precedence_UseOfficialRegistries_CliFalse_Resolves_False()
    {
        ConfigRoot config = ParseArgs("--use-official-registries", "false");
        config.UseOfficialRegistries.ShouldBeFalse();
    }

    [Fact]
    public void Precedence_UseOfficialRegistries_EnvAndCli_CliWins()
    {
        using EnvVarScope _ = new("Use_Official_Registries", "true");
        ConfigRoot config = ParseArgs("--use-official-registries", "false");
        config.UseOfficialRegistries.ShouldBeFalse();
    }
}

/// <summary>
/// xUnit collection serializer for env-var-mutating fixtures: process-wide
/// environment state is shared between tests, so any class that flips env vars
/// via <see cref="System.Environment.SetEnvironmentVariable(string, string?)"/>
/// must run serially with respect to every other env-var-touching class.
/// </summary>
[CollectionDefinition("EnvVarSerial", DisableParallelization = true)]
public class EnvVarSerialCollection
{
}
