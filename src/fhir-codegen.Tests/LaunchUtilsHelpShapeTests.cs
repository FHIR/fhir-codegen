// <copyright file="LaunchUtilsHelpShapeTests.cs" company="Microsoft Corporation">
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

public class LaunchUtilsHelpShapeTests
{
    private static IConfiguration BuildEnvConfig() => new ConfigurationBuilder().Build();

    private static RootCommand BuildRoot() => LaunchUtils.BuildCommand(BuildEnvConfig());

    private static IEnumerable<string> AliasesOf(Option opt)
    {
        yield return opt.Name;
        foreach (string a in opt.Aliases)
        {
            yield return a;
        }
    }

    private static List<string> CollectReachableAliases(Command cmd)
    {
        List<string> aliases = [];
        Command? cursor = cmd;
        while (cursor != null)
        {
            foreach (Option opt in cursor.Options)
            {
                aliases.AddRange(AliasesOf(opt));
            }

            cursor = cursor.Parents.OfType<Command>().FirstOrDefault();
        }

        return aliases;
    }

    private static void AssertNoDuplicates(IEnumerable<string> aliases, string context)
    {
        List<IGrouping<string, string>> dupes = [.. aliases
            .GroupBy(a => a, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)];

        dupes.ShouldBeEmpty($"Duplicate option aliases in {context}: {string.Join(", ", dupes.Select(d => $"{d.Key} (x{d.Count()})"))}");
    }

    [Fact]
    public void BuildCommand_RootCommand_HasNoDuplicateOptionAliases()
    {
        RootCommand root = BuildRoot();
        List<string> aliases = [];
        foreach (Option opt in root.Options)
        {
            aliases.AddRange(AliasesOf(opt));
        }

        AssertNoDuplicates(aliases, "RootCommand");
    }

    [Fact]
    public void BuildCommand_GenerateCommand_HasNoDuplicateOptionAliases()
    {
        RootCommand root = BuildRoot();
        Command generate = root.Subcommands.First(c => c.Name == "generate");
        AssertNoDuplicates(CollectReachableAliases(generate), "generate command");
    }

    [Fact]
    public void BuildCommand_LanguageSubcommand_HasNoDuplicateOptionAliases()
    {
        RootCommand root = BuildRoot();
        Command generate = root.Subcommands.First(c => c.Name == "generate");

        generate.Subcommands.Count.ShouldBeGreaterThan(0);

        foreach (Command lang in generate.Subcommands)
        {
            AssertNoDuplicates(CollectReachableAliases(lang), $"generate {lang.Name}");
        }
    }

    [Fact]
    public void BuildCommand_LanguageSubcommand_LocalOptionsAreLanguageOnly()
    {
        ConfigGenerate generateConfig = new();
        HashSet<string> generateAliases = new(StringComparer.Ordinal);
        foreach (ConfigurationOption co in generateConfig.GetOptions())
        {
            generateAliases.Add(co.CliOption.Name);
            foreach (string a in co.CliOption.Aliases)
            {
                generateAliases.Add(a);
            }
        }

        RootCommand root = BuildRoot();
        Command generate = root.Subcommands.First(c => c.Name == "generate");

        foreach (Command lang in generate.Subcommands)
        {
            foreach (Option opt in lang.Options)
            {
                foreach (string alias in AliasesOf(opt))
                {
                    generateAliases.ShouldNotContain(
                        alias,
                        $"Language subcommand '{lang.Name}' locally owns option '{alias}' that also belongs to ConfigGenerate/ConfigRoot.");
                }
            }
        }
    }

    [Fact]
    public void BuildCommand_XverSubcommands_HaveNoDuplicateOptionAliases()
    {
        RootCommand root = BuildRoot();
        Command xver = root.Subcommands.First(c => c.Name == "xver");
        AssertNoDuplicates(CollectReachableAliases(xver), "xver command");

        Command load = xver.Subcommands.First(c => c.Name == "load");
        AssertNoDuplicates(CollectReachableAliases(load), "xver load command");
    }
}
