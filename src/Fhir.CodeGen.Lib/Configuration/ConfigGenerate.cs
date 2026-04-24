// <copyright file="ConfigCli.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Generic;
using Fhir.CodeGen.Lib.Extensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Smart;

namespace Fhir.CodeGen.Lib.Configuration;

/// <summary>Configuration settings when performing a single-pass generation.</summary>
public class ConfigGenerate : ConfigRoot
{

    /// <summary>Gets a value indicating whether the experimental should be included.</summary>
    [ConfigOption(
        ArgAliases = ["--include-experimental", "--experimental"],
        EnvName = "Include_Experimental",
        Description = "If the output should include structures marked experimental.")]
    public bool IncludeExperimental { get; set; } = false;

    private static ConfigurationOption IncludeExperimentalParameter { get; } = new()
    {
        Name = "IncludeExperimental",
        EnvVarName = "Include_Experimental",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--include-experimental", "--experimental")
        {
            Description = "If the output should include structures marked experimental.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>Gets or sets URL of the FHIR server.</summary>
    [ConfigOption(
        ArgName = "--fhir-server-url",
        EnvName = "Fhir_Server_Url",
        Description = "FHIR Server URL to pull a CapabilityStatement (or Conformance) from. Requires application/fhir+json.")]
    public string FhirServerUrl { get; set; } = string.Empty;

    private static ConfigurationOption FhirServerUrlParameter { get; } = new()
    {
        Name = "FhirServerUrl",
        EnvVarName = "Fhir_Server_Url",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--fhir-server-url")
        {
            Description = "FHIR Server URL to pull a CapabilityStatement (or Conformance) from. Requires application/fhir+json.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>Gets or sets URL of the smart configuration.</summary>
    public string SmartConfigUrl { get; set; } = string.Empty;

    /// <summary>Gets the smart configuration URL parameter.</summary>
    private static ConfigurationOption SmartConfigUrlParameter { get; } = new()
    {
        Name = "SmartConfigUrl",
        EnvVarName = "Smart_Config_Url",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--smart-config-url")
        {
            Description = "URL to pull a .well-known/smart-configuration file from, if different from [base]/.well-known/smart-configuration.  Requires application/json (per spec).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>Gets or sets the FHIR server headers.</summary>
    [ConfigOption(
        ArgName = "--fhir-server-header",
        EnvName = "Fhir_Server_Header",
        Description = "FHIR Server headers to use when pulling a CapabilityStatement (or Conformance) from a FHIR server.")]
    public List<string> FhirServerHeaders { get; set; } = [];

    private static ConfigurationOption FhirServerHeadersParameter { get; } = new()
    {
        Name = "FhirServerHeader",
        EnvVarName = "Fhir_Server_Header",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--fhir-server-header")
        {
            Description = "FHIR Server headers to use when pulling a CapabilityStatement (or Conformance) from a FHIR server.  Use <key>=<value> format.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--resolve-server-canonicals",
        EnvName = "Resolve_Server_Canonicals",
        Description = "If canonical URLs used in server Capability Statement should be resolved.")]
    public bool ResolveServerCanonicals { get; set; } = true;

    private static ConfigurationOption ResolveServerCanonicalsParameter { get; } = new()
    {
        Name = "ResolveServerCanonicals",
        EnvVarName = "Resolve_Server_Canonicals",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--resolve-server-canonicals")
        {
            Description = "If canonical URLs used in server Capability Statement should be resolved.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--resolve-external-canonicals",
        EnvName = "Resolve_External_Canonicals",
        Description = "If canonical URLs external to the server should be resolved.")]
    public bool ResolveExternalCanonicals { get; set; } = false;

    private static ConfigurationOption ResolveExternalCanonicalsParameter { get; } = new()
    {
        Name = "ResolveExternalCanonicals",
        EnvVarName = "Resolve_External_Canonicals",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--resolve-external-canonicals")
        {
            Description = "If canonical URLs external to the server should be resolved.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>Gets or sets the server capabilities, retrieved prior to export for version-conversion.</summary>
    public Hl7.Fhir.Model.CapabilityStatement? ServerCapabilities { get; set; }

    /// <summary>Gets or sets the server FHIR sequence.</summary>
    public FhirReleases.FhirSequenceCodes? ServerFhirSequence { get; set; }

    /// <summary>Gets or sets the server smart configuration, retrieved prior to export if possible.</summary>
    public SmartWellKnown? ServerSmartConfig { get; set; }

    /// <summary>(Immutable) Options that get promoted to user-settable options based on this type.</summary>
    private static readonly ConfigurationOption[] _options =
    [
        IncludeExperimentalParameter,
        FhirServerUrlParameter,
        SmartConfigUrlParameter,
        FhirServerHeadersParameter,
        ResolveServerCanonicalsParameter,
        ResolveExternalCanonicalsParameter,
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
                case "IncludeExperimental":
                    IncludeExperimental = GetOpt(parseResult, opt, IncludeExperimental);
                    break;
                case "FhirServerUrl":
                    FhirServerUrl = GetOpt(parseResult, opt, FhirServerUrl);
                    break;
                case "SmartConfigUrl":
                    SmartConfigUrl = GetOpt(parseResult, opt, SmartConfigUrl);
                    break;
                case "FhirServerHeaders":
                    FhirServerHeaders = GetOpt(parseResult, opt, FhirServerHeaders);
                    break;
                case "ResolveServerCanonicals":
                    ResolveServerCanonicals = GetOpt(parseResult, opt, ResolveServerCanonicals);
                    break;
                case "ResolveExternalCanonicals":
                    ResolveExternalCanonicals = GetOpt(parseResult, opt, ResolveExternalCanonicals);
                    break;
            }
        }
    }

}
