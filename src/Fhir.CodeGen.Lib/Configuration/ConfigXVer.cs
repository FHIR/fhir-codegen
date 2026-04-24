// <copyright file="ConfigXVer.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Configuration;

public class ConfigXVer : ConfigRoot
{
    [ConfigOption(
        ArgAliases = ["--compare", "--compare-package", "-c"],
        EnvName = "Compare_Package",
        ArgArity = "0..*",
        Description = "Comparison packages to load, as directives ([name][#|@][version/literal])")]
    public string[] ComparePackages { get; set; } = [];

    /// <summary>
    /// Gets or sets the configuration option for the packages to load.
    /// </summary>
    private static ConfigurationOption ComparePackagesParameter => new()
    {
        Name = "Compare_Package",
        EnvVarName = "Compare_Package",
        DefaultValue = Array.Empty<string>(),
        CliOption = new System.CommandLine.Option<string[]>("--compare", "--compare-package", "-c")
        {
            Description = "Comparison packages to load, as directives ([name][#|@][version/literal])",
            Arity = System.CommandLine.ArgumentArity.OneOrMore,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--map-source-path",
        EnvName = "Map_Source_Path",
        ArgArity = "0..1",
        Description = "Path to FHIR maps to load (e.g., clone of HL7/fhir-cross-version).")]
    public string CrossVersionMapSourcePath { get; set; } = string.Empty;

    private static ConfigurationOption CrossVersionMapSourcePathParameter => new()
    {
        Name = "Map_Source_Path",
        EnvVarName = "Map_Source_Path",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string?>("--map-source-path")
        {
            Description = "Path to FHIR maps to load (e.g., clone of HL7/fhir-cross-version).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };


    [ConfigOption(
        ArgName = "--db",
        EnvName = "Comparison_Database_Path",
        ArgArity = "0..1",
        Description = "Path or filename for the comparison database FHIR maps to load or export.")]
    public string CrossVersionDbPath { get; set; } = string.Empty;

    private static ConfigurationOption CrossVersionDbPathParameter => new()
    {
        Name = "Comparison_Database_Path",
        EnvVarName = "Comparison_Database_Path",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string?>("--db")
        {
            Description = "Path or filename for the comparison database FHIR maps to load or export.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--reload-db",
        EnvName = "Reload_Comparison_Database",
        ArgArity = "0..1",
        Description = "Set to force reloading of the comparison database from definitions.")]
    public bool ReloadDatabase { get; set; } = false;

    private static ConfigurationOption ReloadDatabaseParameter => new()
    {
        Name = "Reload_Comparison_Database",
        EnvVarName = "Reload_Comparison_Database",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--reload-db")
        {
            Description = "Set to force reloading of the comparison database from definitions.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--source-db",
        EnvName = "Comparison_Source_Database",
        ArgArity = "0..1",
        Description = "Fully specified filename for a source database to use for comparison processing.")]
    public string CrossVersionSourceDb { get; set; } = string.Empty;

    private static ConfigurationOption CrossVersionSourceDbParameter => new()
    {
        Name = "Comparison_Source_Database",
        EnvVarName = "Comparison_Source_Database",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string?>("--source-db")
        {
            Description = "Fully specified filename for a source database to use for comparison processing.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--comparison-pair-filter-key",
        EnvName = "Comparison_Pair_Filter_Key",
        ArgArity = "0..*",
        Description = "Set of Package Comparison Pair keys to process (used to reduce comparisons).")]
    public HashSet<int> ComparisonPairFilterKeys { get; set; } = [];

    private static ConfigurationOption ComparisonPairFilterKeysParameter => new()
    {
        Name = "Comparison_Pair_Filter_Key",
        EnvVarName = "Comparison_Pair_Filter_Key",
        DefaultValue = new HashSet<int>(),
        CliOption = new System.CommandLine.Option<string?>("--comparison-pair-filter-key")
        {
            Description = "Set of Package Comparison Pair keys to process (used to reduce comparisons).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--export-r2",
        EnvName = "Export_R2",
        ArgArity = "0..1",
        Description = "Set to export DSTU2 artifacts from the comparison database.")]
    public bool ExportR2 { get; set; } = false;

    private static ConfigurationOption ExportR2Parameter => new()
    {
        Name = "Export_R2",
        EnvVarName = "Export_R2",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--export-r2")
        {
            Description = "Set to export DSTU2 artifacts from the comparison database.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--export-r3",
        EnvName = "Export_R3",
        ArgArity = "0..1",
        Description = "Set to export STU3 artifacts from the comparison database.")]
    public bool ExportR3 { get; set; } = true;

    private static ConfigurationOption ExportR3Parameter => new()
    {
        Name = "Export_R3",
        EnvVarName = "Export_R3",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--export-r3")
        {
            Description = "Set to export STU3 artifacts from the comparison database.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--export-r4",
        EnvName = "Export_R4",
        ArgArity = "0..1",
        Description = "Set to export R4 artifacts from the comparison database.")]
    public bool ExportR4 { get; set; } = true;

    private static ConfigurationOption ExportR4Parameter => new()
    {
        Name = "Export_R4",
        EnvVarName = "Export_R4",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--export-r4")
        {
            Description = "Set to export R4 artifacts from the comparison database.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--export-r4b",
        EnvName = "Export_R4B",
        ArgArity = "0..1",
        Description = "Set to export R4B artifacts from the comparison database.")]
    public bool ExportR4B { get; set; } = true;

    private static ConfigurationOption ExportR4BParameter => new()
    {
        Name = "Export_R4B",
        EnvVarName = "Export_R4B",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--export-r4b")
        {
            Description = "Set to export R4B artifacts from the comparison database.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--export-r5",
        EnvName = "Export_R5",
        ArgArity = "0..1",
        Description = "Set to export R5 artifacts from the comparison database.")]
    public bool ExportR5 { get; set; } = true;

    private static ConfigurationOption ExportR5Parameter => new()
    {
        Name = "Export_R5",
        EnvVarName = "Export_R5",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--export-r5")
        {
            Description = "Set to export R5 artifacts from the comparison database.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--export-r6",
        EnvName = "Export_R6",
        ArgArity = "0..1",
        Description = "Set to export R6 artifacts from the comparison database.")]
    public bool ExportR6 { get; set; } = true;

    private static ConfigurationOption ExportR6Parameter => new()
    {
        Name = "Export_R6",
        EnvVarName = "Export_R6",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--export-r6")
        {
            Description = "Set to export R6 artifacts from the comparison database.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--xver-version",
        EnvName = "Xver_Artifact_Version",
        ArgArity = "0..1",
        Description = "The version number to use when exporting XVer artifacts.")]
    public string XverArtifactVersion { get; set; } = string.Empty;

    private static ConfigurationOption XverArtifactVersionParameter => new()
    {
        Name = "Xver_Artifact_Version",
        EnvVarName = "Xver_Artifact_Version",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string?>("--xver-version")
        {
            Description = "The version number to use when exporting XVer artifacts.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--xver-generate-npms",
        EnvName = "Xver_Generate_Npms",
        ArgArity = "0..1",
        Description = "Set to generate NPMs for XVer artifacts.")]
    public bool XverGenerateNpms { get; set; } = true;

    private static ConfigurationOption XverGenerateNpmsParameter => new()
    {
        Name = "Xver_Generate_Npms",
        EnvVarName = "Xver_Generate_Npms",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--xver-generate-npms")
        {
            Description = "Set to generate NPMs for XVer artifacts.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--xver-generate-snapshots",
        EnvName = "Xver_Generate_Snapshots",
        ArgArity = "0..1",
        Description = "Set to generate snapshots for XVer artifacts.")]
    public bool XverGenerateSnapshots { get; set; } = false;

    private static ConfigurationOption XverGenerateSnapshotsParameter => new()
    {
        Name = "Xver_Generate_Snapshots",
        EnvVarName = "Xver_Generate_Snapshots",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--xver-generate-snapshots")
        {
            Description = "Set to generate snapshots for XVer artifacts.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--xver-allow-comparison-updates",
        EnvName = "Xver_Allow_Comparison_Updates",
        ArgArity = "0..1",
        Description = "Whether or not to allow updates to the comparison database during processing.")]
    public bool XverAllowComparisonUpdates { get; set; } = true;

    private static ConfigurationOption XverAllowComparisonUpdatesParameter => new()
    {
        Name = "Xver_Allow_Comparison_Updates",
        EnvVarName = "Xver_Allow_Comparison_Updates",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--xver-allow-comparison-updates")
        {
            Description = "Whether or not to allow updates to the comparison database during processing.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--xver-export-for-publisher",
        EnvName = "Xver_Export_For_Publisher",
        ArgArity = "0..1",
        Description = "Set to export XVer artifacts for publisher.")]
    public bool XverExportForPublisher { get; set; } = false;
    private static ConfigurationOption XverExportForPublisherParameter => new()
    {
        Name = "Xver_Export_For_Publisher",
        EnvVarName = "Xver_Export_For_Publisher",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--xver-export-for-publisher")
        {
            Description = "Set to export XVer artifacts for publisher.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--xver-include-scripts",
        EnvName = "Xver_Include_Scripts",
        ArgArity = "0..1",
        Description = "Set to include scripts in the XVer artifacts.")]
    public bool XverIncludeScripts { get; set; } = true;
    private static ConfigurationOption XverIncludeScriptsParameter => new()
    {
        Name = "Xver_Include_Scripts",
        EnvVarName = "Xver_Include_Scripts",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--xver-include-scripts")
        {
            Description = "Set to include scripts in the XVer artifacts.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };


    [ConfigOption(
        ArgName = "--no-output",
        EnvName = "No_Output",
        ArgArity = "0..1",
        Description = "Do not output the comparison result.")]
    public bool NoOutput { get; set; } = false;

    private static ConfigurationOption NoOutputParameter => new()
    {
        Name = "No_Output",
        EnvVarName = "No_Output",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--no-output")
        {
            Description = "Do not output the comparison result.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
               ArgName = "--save-comparison-result",
               EnvName = "Save_Comparison_Result",
               ArgArity = "0..1",
               Description = "Save the comparison result to a file.")]
    public bool SaveComparisonResult { get; set; } = false;
    private static ConfigurationOption SaveComparisonResultParameter => new()
    {
        Name = "Save_Comparison_Result",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--save-comparison-result")
        {
            Description = "Save the comparison result to a file.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--map-destination-path",
        EnvName = "Map_Destination_Path",
        ArgArity = "0..1",
        Description = "Path to directory to save FHIR maps to (e.g., clone of HL7/fhir-cross-version).")]
    public string CrossVersionMapDestinationPath { get; set; } = string.Empty;

    private static ConfigurationOption CrossVersionMapDestinationPathParameter => new()
    {
        Name = "Map_Destination_Path",
        EnvVarName = "Map_Destination_Path",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string?>("--map-destination-path")
        {
            Description = "Path to directory to save FHIR maps to (e.g., clone of HL7/fhir-cross-version).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--use-internal-type-maps",
        EnvName = "Use_Internal_Type_Maps",
        ArgArity = "0..1",
        Description = "Set to use internal type maps for comparison processing.")]
    public bool UseInternalTypeMaps { get; set; } = false;
    public static ConfigurationOption UseInternalTypeMapsParameter => new()
    {
        Name = "Use_Internal_Type_Maps",
        EnvVarName = "Use_Internal_Type_Maps",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--use-internal-type-maps")
        {
            Description = "Set to use internal type maps for comparison processing.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };


    private static readonly ConfigurationOption[] _options =
    [
        ComparePackagesParameter,
        CrossVersionDbPathParameter,
        ReloadDatabaseParameter,
        CrossVersionSourceDbParameter,
        ComparisonPairFilterKeysParameter,
        XverArtifactVersionParameter,
        XverGenerateNpmsParameter,
        XverGenerateSnapshotsParameter,
        XverAllowComparisonUpdatesParameter,
        XverExportForPublisherParameter,
        XverIncludeScriptsParameter,
        NoOutputParameter,
        SaveComparisonResultParameter,
        CrossVersionMapSourcePathParameter,
        CrossVersionMapDestinationPathParameter,
        UseInternalTypeMapsParameter,
        ExportR2Parameter,
        ExportR3Parameter,
        ExportR4Parameter,
        ExportR4BParameter,
        ExportR5Parameter,
        ExportR6Parameter,
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
                case "Compare_Package":
                    ComparePackages = GetOptArray(parseResult, opt, ComparePackages);
                    break;
                case "Comparison_Database_Path":
                    {
                        string? path = GetOpt(parseResult, opt, CrossVersionDbPath);
                        if (string.IsNullOrEmpty(path))
                        {
                            CrossVersionDbPath = string.Empty;
                        }
                        else
                        {
                            CrossVersionDbPath = FileSystemUtils.FindRelativeFile(string.Empty, path ?? string.Empty, false);
                        }
                    }
                    break;
                case "Reload_Comparison_Database":
                    ReloadDatabase = GetOpt(parseResult, opt, ReloadDatabase);
                    break;
                case "Comparison_Source_Database":
                    {
                        string? path = GetOpt(parseResult, opt, CrossVersionSourceDb);
                        if (string.IsNullOrEmpty(path))
                        {
                            CrossVersionSourceDb = string.Empty;
                        }
                        else
                        {
                            CrossVersionSourceDb = FileSystemUtils.FindRelativeFile(string.Empty, path ?? string.Empty, false);
                        }
                    }
                    break;
                case "Comparison_Pair_Filter_Key":
                    ComparisonPairFilterKeys = GetOptHash(parseResult, opt.CliOption, ComparisonPairFilterKeys);
                    break;
                case "Xver_Artifact_Version":
                    XverArtifactVersion = GetOpt(parseResult, opt, XverArtifactVersion);
                    break;
                case "Xver_Generate_Npms":
                    XverGenerateNpms = GetOpt(parseResult, opt, XverGenerateNpms);
                    break;
                case "Xver_Generate_Snapshots":
                    XverGenerateSnapshots = GetOpt(parseResult, opt, XverGenerateSnapshots);
                    break;
                case "Xver_Allow_Comparison_Updates":
                    XverAllowComparisonUpdates = GetOpt(parseResult, opt, XverAllowComparisonUpdates);
                    break;
                case "Xver_Export_For_Publisher":
                    XverExportForPublisher = GetOpt(parseResult, opt, XverExportForPublisher);
                    break;
                case "Xver_Include_Scripts":
                    XverIncludeScripts = GetOpt(parseResult, opt, XverIncludeScripts);
                    break;
                case "No_Output":
                    NoOutput = GetOpt(parseResult, opt, NoOutput);
                    break;
                case "Save_Comparison_Result":
                    SaveComparisonResult = GetOpt(parseResult, opt, SaveComparisonResult);
                    break;
                case "Map_Source_Path":
                    {
                        string? path = GetOpt(parseResult, opt, CrossVersionMapSourcePath);
                        if (string.IsNullOrEmpty(path))
                        {
                            CrossVersionMapSourcePath = string.Empty;
                        }
                        else
                        {
                            CrossVersionMapSourcePath = FileSystemUtils.FindRelativeDir(string.Empty, path ?? string.Empty, false);
                        }
                    }
                    break;
                case "Map_Destination_Path":
                    {
                        string? path = GetOpt(parseResult, opt, CrossVersionMapDestinationPath);
                        if (string.IsNullOrEmpty(path))
                        {
                            CrossVersionMapDestinationPath = string.Empty;
                        }
                        else
                        {
                            CrossVersionMapDestinationPath = FileSystemUtils.FindRelativeDir(string.Empty, path ?? string.Empty, false);
                        }
                    }
                    break;
                case "Use_Internal_Type_Maps":
                    UseInternalTypeMaps = GetOpt(parseResult, opt, UseInternalTypeMaps);
                    break;
                case "Export_R2":
                    ExportR2 = GetOpt(parseResult, opt, ExportR2);
                    break;
                case "Export_R3":
                    ExportR3 = GetOpt(parseResult, opt, ExportR3);
                    break;
                case "Export_R4":
                    ExportR4 = GetOpt(parseResult, opt, ExportR4);
                    break;
                case "Export_R4B":
                    ExportR4B = GetOpt(parseResult, opt, ExportR4B);
                    break;
                case "Export_R5":
                    ExportR5 = GetOpt(parseResult, opt, ExportR5);
                    break;
                case "Export_R6":
                    ExportR6 = GetOpt(parseResult, opt, ExportR6);
                    break;
            }
        }
    }
}
