// <copyright file="ConfigSql.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Configuration;

public class ConfigSql : ConfigRoot
{
    [ConfigOption(
    ArgName = "--view-definition-directory",
    EnvName = "View_Definition_Directory",
    ArgArity = "0..1",
    Description = "Local path to a source for view definitions to load.")]
    public string ViewDefinitionDirectory { get; set; } = "";

    private static ConfigurationOption ViewDefinitionDirectoryParameter => new()
    {
        Name = "View_Definition_Directory",
        DefaultValue = "",
        CliOption = new System.CommandLine.Option<string>("--view-definition-directory")
        {
            Description = "Local path to a source for view definitions to load.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
    ArgName = "--export-db-name",
    EnvName = "Export_Database_Name",
    ArgArity = "0..1",
    Description = "Name of the database to generate.")]
    public string ExportDatabaseName { get; set; } = "";

    private static ConfigurationOption ExportDatabaseNameParameter => new()
    {
        Name = "Export_Database_Name",
        DefaultValue = "export.sqlite",
        CliOption = new System.CommandLine.Option<string>("--export-db-name")
        {
            Description = "Name of the database to generate.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };


    private static readonly ConfigurationOption[] _options =
    [
        ViewDefinitionDirectoryParameter,
        ExportDatabaseNameParameter,
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
                case "View_Definition_Directory":
                    {
                        string dir = GetOpt(parseResult, opt, ViewDefinitionDirectory);

                        if (string.IsNullOrEmpty(dir))
                        {
                            dir = FileSystemUtils.FindRelativeDir(string.Empty, ".");
                        }
                        else if (!Path.IsPathRooted(dir))
                        {
                            dir = FileSystemUtils.FindRelativeDir(string.Empty, dir);
                        }

                        ViewDefinitionDirectory = dir;

                    }
                    break;
                case "Export_Database_Name":
                    ExportDatabaseName = GetOpt(parseResult, opt, ExportDatabaseName);
                    break;
            }
        }
    }

}
