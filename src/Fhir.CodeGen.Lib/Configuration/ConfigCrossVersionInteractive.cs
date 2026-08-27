// <copyright file="ConfigCrossVersionInteractive.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Configuration;

public class ConfigCrossVersionInteractive : ConfigRoot
{
    [ConfigOption(
        ArgName = "--cross-version-directory",
        EnvName = "Cross_Version_Directory",
        ArgArity = "0..1",
        Description = "Local path to the 'HL7/fhir-cross-version' repository clone.")]
    public string CrossVersionRepoDirectory { get; set; } = "git/fhir-cross-version";

    private static ConfigurationOption CrossVersionRepoDirectoryParameter => new()
    {
        Name = "Cross_Version_Directory",
        DefaultValue = "git/fhir-cross-version",
        CliOption = new System.CommandLine.Option<string[]>("--cross-version-directory")
        {
            Description = "Local path to the 'HL7/fhir-cross-version' repository clone.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--left-package-directive",
        EnvName = "Left_Package_Directive",
        ArgArity = "0..1",
        Description = "Directive for the left (source) package.")]
    public string LeftPackageDirective { get; set; } = string.Empty;

    private static ConfigurationOption LeftPackageDirectiveParameter => new()
    {
        Name = "Left_Package_Directive",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--left-package-directive")
        {
            Description = "Directive for the left (source) package.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--right-package-directive",
        EnvName = "Right_Package_Directive",
        ArgArity = "0..1",
        Description = "Directive for the right (target) package.")]
    public string RightPackageDirective { get; set; } = string.Empty;

    private static ConfigurationOption RightPackageDirectiveParameter => new()
    {
        Name = "Right_Package_Directive",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--right-package-directive")
        {
            Description = "Directive for the right (target) package.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
               ArgName = "--existing-comparison-path",
               EnvName = "Existing_Comparison_Path",
               ArgArity = "0..1",
               Description = "Path to existing comparison files.")]
    public string ExistingComparisonPath { get; set; } = string.Empty;

    private static ConfigurationOption ExistingComparisonPathParameter => new()
    {
        Name = "Existing_Comparison_Path",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--existing-comparison-path")
        {
            Description = "Path to existing comparison files.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    private static readonly ConfigurationOption[] _options =
    [
        CrossVersionRepoDirectoryParameter,
        LeftPackageDirectiveParameter,
        RightPackageDirectiveParameter,
        ExistingComparisonPathParameter,
    ];


    /// <summary>Gets the array of configuration options.</summary>
    /// <returns>An array of configuration option.</returns>
    public override ConfigurationOption[] GetOptions()
    {
        return [.. base.GetOptions(), .. _options];
    }

    public override void Parse(System.CommandLine.ParseResult parseResult)
    {
        // parse base properties
        base.Parse(parseResult);

        // iterate over options for ones we are interested in
        foreach (ConfigurationOption opt in _options)
        {
            switch (opt.Name)
            {
                case "Cross_Version_Directory":
                    CrossVersionRepoDirectory = GetOpt(parseResult, opt, CrossVersionRepoDirectory);
                    break;
                case "Left_Package_Directive":
                    LeftPackageDirective = GetOpt(parseResult, opt, LeftPackageDirective);
                    break;
                case "Right_Package_Directive":
                    RightPackageDirective = GetOpt(parseResult, opt, RightPackageDirective);
                    break;
                case "Existing_Comparison_Path":
                    ExistingComparisonPath = GetOpt(parseResult, opt, ExistingComparisonPath);
                    break;
            }
        }
    }

}
