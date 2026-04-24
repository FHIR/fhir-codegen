// <copyright file="FirelyOptions.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.ComponentModel;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Language.Firely;

/// <summary>Firely generation options.</summary>
public class FirelyGenOptions : ConfigGenerate
{
    /// <summary>
    /// Gets or sets the subset of language exports to make.
    /// </summary>
    [ConfigOption(
        ArgName = "--subset",
        Description = "Which subset of language exports to make.")]
    public CSharpFirelyCommon.GenSubset Subset { get; set; } = CSharpFirelyCommon.GenSubset.Satellite;

    private static ConfigurationOption SubsetParameter { get; } = new()
    {
        Name = "Subset",
        DefaultValue = CSharpFirelyCommon.GenSubset.Satellite,
        CliOption = new System.CommandLine.Option<CSharpFirelyCommon.GenSubset>("--subset")
        {
            Description = "Which subset of language exports to make.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>
    /// Gets or sets a value indicating whether the output should include 5 W's mappings.
    /// </summary>
    [ConfigOption(
        ArgName = "--w5",
        Description = "If the output should include 5W's mappings.")]
    public bool ExportFiveWs { get; set; } = true;

    private static ConfigurationOption ExportFiveWsParameter { get; } = new()
    {
        Name = "ExportFiveWs",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--w5")
        {
            Description = "If the output should include 5W's mappings.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>Gets or sets the cql model.</summary>
    [ConfigOption(
        ArgName = "--cql-model",
        Description = "Name of the Cql model for which metadata attributes should be added to the POCOs. 'Fhir401' is the only valid value at the moment.")]
    public string CqlModel { get; set; } = string.Empty;

    private static ConfigurationOption CqlModelParameter { get; } = new()
    {
        Name = "CqlModel",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--cql-model")
        {
            Description = "Name of the Cql model for which metadata attributes should be added to the pocos. 'Fhir401' is the only valid value at the moment.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    private static readonly ConfigurationOption[] _options =
    [
        SubsetParameter,
        ExportFiveWsParameter,
        CqlModelParameter,
    ];


    /// <summary>
    /// Gets the configuration options for the current instance and its base class.
    /// </summary>
    /// <returns>An array of configuration options.</returns>
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
                case "Subset":
                    Subset = GetOpt(parseResult, opt, Subset);
                    break;
                case "ExportFiveWs":
                    ExportFiveWs = GetOpt(parseResult, opt, ExportFiveWs);
                    break;
                case "CqlModel":
                    CqlModel = GetOpt(parseResult, opt, CqlModel);
                    break;
            }
        }
    }
}
