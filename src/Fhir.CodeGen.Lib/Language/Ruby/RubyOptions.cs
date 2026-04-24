// <copyright file="RubyOptions.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Language.Ruby;

public class RubyOptions : ConfigGenerate
{
    private const string DefaultModule = "FHIR";

    /// <summary>Gets or sets the module.</summary>
    [ConfigOption(
        ArgName = "--module",
        Description = "Base module for Ruby files, default is 'FHIR'")]
    public string Module { get; set; } = DefaultModule;

    private static ConfigurationOption ModuleParameter { get; } = new()
    {
        Name = "Module",
        DefaultValue = DefaultModule,
        CliOption = new System.CommandLine.Option<string>("--module")
        {
            Description = "Base module for Ruby files, default is 'FHIR'.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };


    /// <summary>(Immutable) Options for controlling the operation.</summary>
    private static readonly ConfigurationOption[] _options =
    [
        ModuleParameter,
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
                case "Module":
                    Module = GetOpt(parseResult, opt, Module);
                    break;
            }
        }
    }
}
