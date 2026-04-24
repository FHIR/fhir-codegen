// <copyright file="ConfigCompare.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>


using System.ComponentModel;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Configuration;

public class ConfigCompare : ConfigRoot
{
    [ConfigOption(
        ArgAliases = ["--compare", "--compare-package", "-c"],
        EnvName = "Compare_Package",
        ArgArity = "0..*",
        Description = "Comparison package to load, either as directive ([name]#[version/literal]) or URL.")]
    public string[] ComparePackages { get; set; } = [];

    /// <summary>
    /// Gets or sets the configuration option for the packages to load.
    /// </summary>
    private static ConfigurationOption ComparePackagesParameter => new()
    {
        Name = "Compare_Package",
        DefaultValue = Array.Empty<string>(),
        CliOption = new System.CommandLine.Option<string[]>("--compare", "--compare-package", "-c")
        {
            Description = "Comparison package to load, either as directive ([name]#[version/literal]) or URL.",
            Arity = System.CommandLine.ArgumentArity.OneOrMore,
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
        ArgName = "--map-source-path",
        EnvName = "Map_Source_Path",
        ArgArity = "0..1",
        Description = "Path to FHIR maps to load (e.g., clone of HL7/fhir-cross-version).")]
    public string CrossVersionMapSourcePath { get; set; } = string.Empty;

    private static ConfigurationOption CrossVersionMapSourcePathParameter => new()
    {
        Name = "Map_Source_Path",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--map-source-path")
        {
            Description = "Path to FHIR maps to load (e.g., clone of HL7/fhir-cross-version).",
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
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--map-destination-path")
        {
            Description = "Path to directory to save FHIR maps to (e.g., clone of HL7/fhir-cross-version).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    public enum ComparisonMapSaveStyle
    {
        [Description("Official maps in the style of HL7/fhir-cross-version")]
        Official,

        [Description("Source maps in the internally-defined style")]
        Source,

        [Description("Do not save maps")]
        None,
    }

    [ConfigOption(
        ArgName = "--map-save-style",
        EnvName = "Map_Save_Style",
        ArgArity = "0..1",
        Description = "Style of saving the comparison maps.")]
    public ComparisonMapSaveStyle MapSaveStyle { get; set; } = ComparisonMapSaveStyle.Official;

    public static ConfigurationOption MapSaveStyleParameter => new()
    {
        Name = "Map_Save_Style",
        DefaultValue = ComparisonMapSaveStyle.Official,
        CliOption = new System.CommandLine.Option<ComparisonMapSaveStyle>("--map-save-style")
        {
            Description = "Style of saving the comparison maps.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    //[ConfigOption(
    //    ArgName = "--known-change-path",
    //    EnvName = "Known_Change_Path",
    //    ArgArity = "0..1",
    //    Description = "Source path for known version changes.")]
    //public string KnownChangePath { get; set; } = "./renames";

    //private static ConfigurationOption KnownChangePathParameter => new()
    //{
    //    Name = "Known_Change_Path",
    //    DefaultValue = "./known-changes",
    //    CliOption = new System.CommandLine.Option<string[]>("--known-change-path", "Source path for known version changes.")
    //    {
    //        Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
    //        IsRequired = false,
    //    },
    //};

    //[ConfigOption(
    //    ArgName = "--ollama-url",
    //    EnvName = "Ollama_Url",
    //    ArgArity = "0..1",
    //    Description = "Base URL for Ollama evaluation.")]
    public string OllamaUrl { get; set; } = string.Empty;

    //private static ConfigurationOption OllamaUrlParameter => new()
    //{
    //    Name = "Ollama_Url",
    //    DefaultValue = string.Empty,
    //    CliOption = new System.CommandLine.Option<string>("--ollama-url", "Base URL for Ollama evaluation.")
    //    {
    //        Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
    //        IsRequired = false,
    //    },
    //};

    //[ConfigOption(
    //    ArgName = "--ollama-model",
    //    EnvName = "Ollama_Model",
    //    ArgArity = "0..1",
    //    Description = "Model name for Ollama evaluation.")]
    public string OllamaModel { get; set; } = string.Empty;
    //private static ConfigurationOption OllamaModelParameter => new()
    //{
    //    Name = "Ollama_Model",
    //    DefaultValue = string.Empty,
    //    CliOption = new System.CommandLine.Option<string>("--ollama-model", "Model name for Ollama evaluation.")
    //    {
    //        Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
    //        IsRequired = false,
    //    },
    //};


    private static readonly ConfigurationOption[] _options =
    [
        ComparePackagesParameter,
        NoOutputParameter,
        SaveComparisonResultParameter,
        CrossVersionMapSourcePathParameter,
        CrossVersionMapDestinationPathParameter,
        MapSaveStyleParameter,
        //KnownChangePathParameter,
        //OllamaUrlParameter,
        //OllamaModelParameter,
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
                case "No_Output":
                    NoOutput = GetOpt(parseResult, opt, NoOutput);
                    break;
                case "Save_Comparison_Result":
                    SaveComparisonResult = GetOpt(parseResult, opt, SaveComparisonResult);
                    break;
                case "Map_Source_Path":
                    CrossVersionMapSourcePath = GetOpt(parseResult, opt, CrossVersionMapSourcePath);
                    break;
                case "Map_Destination_Path":
                    CrossVersionMapDestinationPath = GetOpt(parseResult, opt, CrossVersionMapDestinationPath);
                    break;
                case "Map_Save_Style":
                    MapSaveStyle = GetOpt(parseResult, opt, MapSaveStyle);
                    break;
                //case "Known_Change_Path":
                //    KnownChangePath = GetOpt(parseResult, opt, KnownChangePath);
                //    break;
                //case "Ollama_Url":
                //    OllamaUrl = GetOpt(parseResult, opt, OllamaUrl);
                //    break;
                //case "Ollama_Model":
                //    OllamaModel = GetOpt(parseResult, opt, OllamaModel);
                //    break;
            }
        }
    }

}
