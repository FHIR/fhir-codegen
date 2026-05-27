// <copyright file="ConfigDocs.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Configuration;

/// <summary>
/// Configuration for the <c>docs</c> top-level command (documentation tooling).
/// </summary>
public class ConfigDocs : ConfigRoot
{
    /// <summary>The default output path for generated CLI Markdown.</summary>
    public const string DefaultOutputPath = "docs/articles/cli.md";

    /// <summary>Gets or sets the output path for generated documentation.</summary>
    [ConfigOption(
        ArgName = "--output",
        EnvName = "Docs_Output",
        ArgArity = "0..1",
        Description = "Path to write the generated documentation file.")]
    public string OutputPath { get; set; } = DefaultOutputPath;

    private static ConfigurationOption OutputPathParameter { get; } = new()
    {
        Name = "Docs_Output",
        EnvVarName = "Docs_Output",
        DefaultValue = DefaultOutputPath,
        CliOption = new System.CommandLine.Option<string>("--output")
        {
            Description = "Path to write the generated documentation file.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    private static readonly ConfigurationOption[] _options =
    [
        OutputPathParameter,
    ];

    /// <summary>Gets the array of configuration options.</summary>
    /// <returns>An array of configuration option.</returns>
    public override ConfigurationOption[] GetOptions()
    {
        return [.. base.GetOptions(), .. _options];
    }

    /// <summary>Parses the given parse result into this configuration instance.</summary>
    /// <param name="parseResult">The parse result.</param>
    public override void Parse(System.CommandLine.ParseResult parseResult)
    {
        base.Parse(parseResult);

        foreach (ConfigurationOption opt in _options)
        {
            switch (opt.Name)
            {
                case "Docs_Output":
                    OutputPath = GetOpt(parseResult, opt, OutputPath);
                    break;
            }
        }
    }
}
