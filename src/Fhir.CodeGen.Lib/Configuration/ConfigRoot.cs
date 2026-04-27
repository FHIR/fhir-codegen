// <copyright file="ConfigRoot.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using Fhir.CodeGen.Lib.Extensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Utils;

#if NETSTANDARD2_0
using Fhir.CodeGen.Common.Polyfill;
#endif

namespace Fhir.CodeGen.Lib.Configuration;

/// <summary>
/// Root configuration with common settings.
/// </summary>
public class ConfigRoot : ICodeGenConfig
{
    public const int DefaultMaxExpansionSize = 1000;

    /// <summary>Gets or sets the initial command passed at launch.</summary>
    public string? LaunchCommand { get; set; } = null;

    /// <summary>Gets the logger factory instance.</summary>
    /// <value>The logger factory instance used for logging.</value>
    public ILoggerFactory LogFactory { get; set; } = LoggerFactory.Create(builder => builder.AddConsole());

    /// <summary>
    /// Gets or sets the pathname of the FHIR cache directory.
    /// </summary>
    [ConfigOption(
        ArgName = "--fhir-cache",
        EnvName = "Fhir_Cache",
        Description = "Location of the FHIR cache (none specified defaults to user .fhir directory).")]
    public string? FhirCacheDirectory { get; set; } = null;

    /// <summary>
    /// Gets or sets the configuration option for the FHIR cache directory.
    /// </summary>
    private static ConfigurationOption FhirCacheDirectoryParameter { get; } = new()
    {
        Name = "FhirCache",
        EnvVarName = "Fhir_Cache",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string?>("--fhir-cache")
        {
            Description = "Location of the FHIR cache (none specified defaults to user .fhir directory).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--use-official-registries",
        EnvName = "Use_Official_Registries",
        Description = "Use official FHIR registries to resolve packages.")]
    public bool UseOfficialRegistries { get; set; } = true;

    private static ConfigurationOption UseOfficialRegistriesParameter { get; } = new()
    {
        Name = "UseOfficialRegistries",
        EnvVarName = "Use_Official_Registries",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--use-official-registries")
        {
            Description = "Use official FHIR registries to resolve packages.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--additional-fhir-registry-urls",
        EnvName = "Additional_FHIR_Registry_Urls",
        ArgArity = "0..*",
        Description = "Additional FHIR registry URLs to use.")]
    public string[] AdditionalFhirRegistryUrls { get; set; } = Array.Empty<string>();

    private static ConfigurationOption AdditionalFhirRegistryUrlsParameter { get; } = new()
    {
        Name = "AdditionalFhirRegistryUrls",
        EnvVarName = "Additional_FHIR_Registry_Urls",
        DefaultValue = Array.Empty<string>(),
        CliOption = new System.CommandLine.Option<string[]>("--additional-fhir-registry-urls")
        {
            Description = "Additional FHIR registry URLs to use.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    [ConfigOption(
    ArgName = "--additional-npm-registry-urls",
    EnvName = "Additional_NPM_Registry_Urls",
    ArgArity = "0..*",
    Description = "Additional NPM registry URLs to use.")]
    public string[] AdditionalNpmRegistryUrls { get; set; } = Array.Empty<string>();

    private static ConfigurationOption AdditionalNpmRegistryUrlsParameter { get; } = new()
    {
        Name = "AdditionalNpmRegistryUrls",
        EnvVarName = "Additional_NPM_Registry_Urls",
        DefaultValue = Array.Empty<string>(),
        CliOption = new System.CommandLine.Option<string[]>("--additional-npm-registry-urls")
        {
            Description = "Additional NPM registry URLs to use.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    /// <summary>
    /// Gets or sets the pathname of the output directory.
    /// </summary>
    [ConfigOption(
        ArgAliases = ["--output-path", "--output-directory", "--output-dir"],
        EnvName = "Output_Path",
        Description = "File or directory to write output.")]
    public string OutputDirectory { get; set; } = ".";

    /// <summary>
    /// Gets or sets the configuration option for the output directory.
    /// </summary>
    private static ConfigurationOption OutputDirectoryParameter { get; } = new()
    {
        Name = "OutputPath",
        EnvVarName = "Output_Path",
        DefaultValue = ".",
        CliOption = new System.CommandLine.Option<string>("--output-path", "--output-directory", "--output-dir")
        {
            Description = "File or directory to write output.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>Gets or sets the filename of the output file.</summary>
    [ConfigOption(
        ArgAliases = ["--output-filename", "--output-file"],
        EnvName = "Output_Filename",
        Description = "Filename to write output.")]
    public string OutputFilename { get; set; } = string.Empty;

    private static ConfigurationOption OutputFilenameParameter { get; } = new()
    {
        Name = "OutputFilename",
        EnvVarName = "Output_Filename",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--output-filename", "--output-file")
        {
            Description = "Filename to write output.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>
    /// Gets or sets the packages to load.
    /// </summary>
    [ConfigOption(
        ArgAliases = ["--package", "--load-package", "-p"],
        EnvName = "Load_Package",
        ArgArity = "0..*",
        Description = "Package to load, either as directive ([name]#[version/literal]) or URL.")]
    public string[] Packages { get; set; } = [];

    /// <summary>
    /// Gets or sets the configuration option for the packages to load.
    /// </summary>
    private static ConfigurationOption PackagesParameter { get; } = new()
    {
        Name = "Packages",
        DefaultValue = Array.Empty<string>(),
        CliOption = new System.CommandLine.Option<string[]>("--package", "--load-package", "-p")
        {
            Description = "Package to load, either as directive ([name]#[version/literal]) or URL.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--auto-load-expansions",
        EnvName = "Auto_Load_Expansions",
        Description = "When loading core packages, load the expansions packages automatically.")]
    public bool AutoLoadExpansions { get; set; } = true;

    private static ConfigurationOption AutoLoadExpansionsParameter { get; } = new()
    {
        Name = "AutoLoadExpansions",
        EnvVarName = "Auto_Load_Expansions",
        DefaultValue = true,
        CliOption = new System.CommandLine.Option<bool>("--auto-load-expansions")
        {
            Description = "When loading core packages, load the expansions packages automatically.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--resolve-dependencies",
        EnvName = "Resolve_Dependencies",
        Description = "Resolve package dependencies.")]
    public bool ResolvePackageDependencies { get; set; } = false;

    private static ConfigurationOption ResolvePackageDependenciesParameter { get; } = new()
    {
        Name = "ResolvePackageDependencies",
        EnvVarName = "Resolve_Dependencies",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--resolve-dependencies")
        {
            Description = "Resolve package dependencies.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--max-expansion-size",
        EnvName = "Max_Expansion_Size",
        Description = "Maximum number of concepts to include in a value set expansion before limiting.")]
    public int MaxExpansionSize { get; set; } = DefaultMaxExpansionSize;
    public static ConfigurationOption MaxExpansionSizeParameter { get; } = new()
    {
        Name = "MaxExpansionSize",
        EnvVarName = "Max_Expansion_Size",
        DefaultValue = DefaultMaxExpansionSize,
        CliOption = new System.CommandLine.Option<int>("--max-expansion-size")
        {
            Description = "Maximum number of concepts to include in a value set expansion before limiting.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    private static readonly FhirArtifactClassEnum[] _defaultLoadStructures =
    [
        FhirArtifactClassEnum.PrimitiveType,
        FhirArtifactClassEnum.ComplexType,
        FhirArtifactClassEnum.Resource,
        FhirArtifactClassEnum.Interface,
        FhirArtifactClassEnum.Extension,
        FhirArtifactClassEnum.Operation,
        FhirArtifactClassEnum.SearchParameter,
        FhirArtifactClassEnum.CodeSystem,
        FhirArtifactClassEnum.ValueSet,
        FhirArtifactClassEnum.Profile,
        FhirArtifactClassEnum.LogicalModel,
        FhirArtifactClassEnum.Compartment,
        FhirArtifactClassEnum.ConceptMap,
        FhirArtifactClassEnum.NamingSystem,
        FhirArtifactClassEnum.StructureMap,
        FhirArtifactClassEnum.ImplementationGuide,
        FhirArtifactClassEnum.CapabilityStatement,
    ];

    /// <summary>Gets or sets the FHIR structures to load, default is all.</summary>
    [ConfigOption(
        ArgName = "--load-structures",
        EnvName = "Load_Structures",
        Description = "Types of FHIR structures to load.",
        ArgArity = "0..*")]
    public FhirArtifactClassEnum[] LoadStructures { get; set; } = _defaultLoadStructures;

    private static ConfigurationOption LoadStructuresParameter { get; } = new()
    {
        Name = "LoadStructures",
        EnvVarName = "Load_Structures",
        DefaultValue = _defaultLoadStructures,
        CliOption = new System.CommandLine.Option<FhirArtifactClassEnum[]>("--load-structures")
        {
            Description = "Types of FHIR structures to load.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    private static readonly FhirArtifactClassEnum[] _defaultExportStructures =
    [
        FhirArtifactClassEnum.PrimitiveType,
        FhirArtifactClassEnum.ComplexType,
        FhirArtifactClassEnum.Resource,
        FhirArtifactClassEnum.Interface,
        FhirArtifactClassEnum.Extension,
        FhirArtifactClassEnum.Operation,
        FhirArtifactClassEnum.SearchParameter,
        FhirArtifactClassEnum.CodeSystem,
        FhirArtifactClassEnum.ValueSet,
        FhirArtifactClassEnum.Profile,
        FhirArtifactClassEnum.LogicalModel,
        FhirArtifactClassEnum.Compartment,
    ];

    /// <summary>Gets or sets the FHIR structures to load, default is all.</summary>
    [ConfigOption(
        ArgName = "--export-structures",
        EnvName = "Export_Structures",
        Description = "Types of FHIR structures to export.",
        ArgArity = "0..*")]
    public FhirArtifactClassEnum[] ExportStructures { get; set; } = _defaultExportStructures;

    //public HashSet<FhirArtifactClassEnum> ProcessStructures { get; set; } = new();

    private static ConfigurationOption ExportStructuresParameter { get; } = new()
    {
        Name = "ExportStructures",
        EnvVarName = "Export_Structures",
        DefaultValue = _defaultExportStructures,
        CliOption = new System.CommandLine.Option<FhirArtifactClassEnum[]>("--export-structures")
        {
            Description = "Types of FHIR structures to export.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };

    /// <summary>Gets or sets the export keys.</summary>
    [ConfigOption(
        ArgName = "--export-keys",
        EnvName = "Export_Keys",
        Description = "Keys of FHIR structures to export (e.g., Patient), empty means all.",
        ArgArity = "0..*")]
    public HashSet<string> ExportKeys { get; set; } = [];

    private static ConfigurationOption ExportKeysParameter { get; } = new()
    {
        Name = "ExportKeys",
        EnvVarName = "Export_Keys",
        DefaultValue = new HashSet<string>(),
        CliOption = new System.CommandLine.Option<HashSet<string>>("--export-keys")
        {
            Description = "Keys of FHIR structures to export (e.g., Patient), empty means all.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrMore,
            Required = false,
        },
    };


    [ConfigOption(
        ArgName = "--load-canonical-examples",
        EnvName = "Load_Canonical_Examples",
        Description = "Load canonical examples from packages.")]
    public bool LoadCanonicalExamples { get; set; } = false;

    private static ConfigurationOption LoadCanonicalExamplesParameter { get; } = new()
    {
        Name = "LoadCanonicalExamples",
        EnvVarName = "Load_Canonical_Examples",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--load-canonical-examples")
        {
            Description = "Load canonical examples from packages.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>
    /// Gets or sets a value indicating whether the offline mode.
    /// </summary>
    [ConfigOption(
        ArgName = "--offline",
        EnvName = "Offline",
        Description = "Offline mode (will not download missing packages).")]
    public bool OfflineMode { get; set; } = false;

    /// <summary>
    /// Gets or sets the configuration option for the offline mode.
    /// </summary>
    private static ConfigurationOption OfflineModeParameter { get; } = new()
    {
        Name = "OfflineMode",
        EnvVarName = "Offline",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--offline")
        {
            Description = "Offline mode (will not download missing packages).",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--fhir-version",
        EnvName = "Fhir_Version",
        Description = "FHIR version to use.")]
    public string FhirVersion { get; set; } = string.Empty;

    private static ConfigurationOption FhirVersionParameter { get; } = new()
    {
        Name = "FhirVersion",
        EnvVarName = "Fhir_Version",
        DefaultValue = string.Empty,
        CliOption = new System.CommandLine.Option<string>("--fhir-version")
        {
            Description = "FHIR version to use.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    /// <summary>
    /// Gets all the configuration options.
    /// </summary>
    private static readonly ConfigurationOption[] _options =
    [
        FhirCacheDirectoryParameter,
        UseOfficialRegistriesParameter,
        AdditionalFhirRegistryUrlsParameter,
        AdditionalNpmRegistryUrlsParameter,
        OutputDirectoryParameter,
        OutputFilenameParameter,
        PackagesParameter,
        AutoLoadExpansionsParameter,
        MaxExpansionSizeParameter,
        ResolvePackageDependenciesParameter,
        LoadStructuresParameter,
        ExportKeysParameter,
        LoadCanonicalExamplesParameter,
        OfflineModeParameter,
        FhirVersionParameter,
    ];

    /// <summary>
    /// Gets the array of configuration options.
    /// </summary>
    public virtual ConfigurationOption[] GetOptions() => _options;

    internal T GetOpt<T>(
        System.CommandLine.ParseResult parseResult,
        ConfigurationOption opt,
        T defaultValue)
    {
        System.CommandLine.Parsing.OptionResult? optResult = parseResult.GetResult(opt.CliOption);
        if (optResult is null || optResult.Implicit)
        {
            return GetEnvValueOrDefault(opt.EnvVarName, defaultValue);
        }

        object? parsed = optResult.GetValueOrDefault<object>();

        if (typeof(T).IsEnum == true)
        {
            string? strVal = parsed?.ToString();

            if (strVal == null)
            {
                return defaultValue;
            }

            return (T)Enum.Parse(typeof(T), strVal, true) ?? defaultValue;
        }

        if (parsed is System.CommandLine.Parsing.Token t)
        {
            switch (defaultValue)
            {
                case bool:
                    return (T)((object?)Convert.ToBoolean(t.Value) ?? defaultValue);
                case int:
                    return (T)((object?)Convert.ToInt32(t.Value) ?? defaultValue);
                case long:
                    return (T)((object?)Convert.ToInt64(t.Value) ?? defaultValue);
                case float:
                    return (T)((object?)Convert.ToSingle(t.Value) ?? defaultValue);
                case double:
                    return (T)((object?)Convert.ToDouble(t.Value) ?? defaultValue);
                case decimal:
                    return (T)((object?)Convert.ToDecimal(t.Value) ?? defaultValue);
                case string:
                    return (T)((object?)Convert.ToString(t.Value) ?? defaultValue);
                default:
                    {
                        if ((t.Value != null) &&
                            (t.Value is T typed))
                        {
                            return typed;
                        }
                    }
                    break;
            }
        }

        switch (parsed)
        {
            case bool:
                return (T)((object?)Convert.ToBoolean(parsed) ?? defaultValue!);
            case int:
                return (T)((object?)Convert.ToInt32(parsed) ?? defaultValue!);
            case long:
                return (T)((object?)Convert.ToInt64(parsed) ?? defaultValue!);
            case float:
                return (T)((object?)Convert.ToSingle(parsed) ?? defaultValue!);
            case double:
                return (T)((object?)Convert.ToDouble(parsed) ?? defaultValue!);
            case decimal:
                return (T)((object?)Convert.ToDecimal(parsed) ?? defaultValue!);
            case string:
                return (T)((object?)Convert.ToString(parsed) ?? defaultValue!);
            default:
                {
                    if ((parsed != null) &&
                        (parsed is T typed))
                    {
                        return typed;
                    }
                }
                break;
        }

        return defaultValue;
    }

    /// <summary>
    /// Resolves an environment variable into <typeparamref name="T"/>, falling back to
    /// <paramref name="defaultValue"/> when the variable is unset, empty, or unconvertible.
    /// Centralizes the env-var path used by <see cref="GetOpt{T}"/> on the implicit/no-arg
    /// branch under the System.CommandLine 2.0 GA + D1(b) shape, where Option&lt;T&gt; defaults
    /// are no longer seeded with env-config values.
    /// </summary>
    private static T GetEnvValueOrDefault<T>(string envVarName, T defaultValue)
    {
        if (string.IsNullOrEmpty(envVarName))
        {
            return defaultValue;
        }

        string? envValue = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(envValue))
        {
            return defaultValue;
        }

        try
        {
            if (typeof(T).IsEnum)
            {
                return (T)Enum.Parse(typeof(T), envValue, ignoreCase: true);
            }

            return (T)Convert.ChangeType(envValue, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    internal T[] GetOptArray<T>(
        System.CommandLine.ParseResult parseResult,
        ConfigurationOption opt,
        T[] defaultValue)
    {
        System.CommandLine.Parsing.OptionResult? optResult = parseResult.GetResult(opt.CliOption);
        if (optResult is null || optResult.Implicit)
        {
            return GetEnvValueArrayOrDefault(opt.EnvVarName, defaultValue);
        }

        object? parsed = optResult.GetValueOrDefault<object>();

        if (parsed != null)
        {
            List<T> values = [];

            if (parsed is T[] array)
            {
                return array;
            }
            else if (parsed is IEnumerator genericEnumerator)
            {
                // use the enumerator to add values to the array
                while (genericEnumerator.MoveNext())
                {
                    if (genericEnumerator.Current is T tValue)
                    {
                        values.Add(tValue);
                    }
                    else
                    {
                        throw new Exception("Should not be here!");
                    }
                }
            }
            else if (parsed is IEnumerator<T> enumerator)
            {
                // use the enumerator to add values to the array
                while (enumerator.MoveNext())
                {
                    values.Add(enumerator.Current);
                }
            }
            else
            {
                throw new Exception("Should not be here!");
            }

            if (values.Count != 0)
            {
                return [.. values];
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Resolves a comma-separated environment variable into <typeparamref name="T"/>[],
    /// falling back to <paramref name="defaultValue"/> when the variable is unset, empty,
    /// or contains any unconvertible token.
    /// </summary>
    private static T[] GetEnvValueArrayOrDefault<T>(string envVarName, T[] defaultValue)
    {
        if (string.IsNullOrEmpty(envVarName))
        {
            return defaultValue;
        }

        string? envValue = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(envValue))
        {
            return defaultValue;
        }

        string[] tokens = envValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return defaultValue;
        }

        List<T> values = [];
        try
        {
            foreach (string token in tokens)
            {
                if (typeof(T).IsEnum)
                {
                    values.Add((T)Enum.Parse(typeof(T), token, ignoreCase: true));
                }
                else
                {
                    values.Add((T)Convert.ChangeType(token, typeof(T)));
                }
            }
        }
        catch
        {
            return defaultValue;
        }

        return [.. values];
    }

    internal HashSet<T> GetOptHash<T>(
        System.CommandLine.ParseResult parseResult,
        System.CommandLine.Option opt,
        HashSet<T> defaultValue)
    {
        System.CommandLine.Parsing.OptionResult? optResult = parseResult.GetResult(opt);
        if (optResult is null || optResult.Implicit)
        {
            return defaultValue;
        }

        object? parsed = optResult.GetValueOrDefault<object>();

        if (parsed == null)
        {
            return defaultValue;
        }

        HashSet<T> values = [];

        if (parsed is IEnumerator<T> typed)
        {
            // use the enumerator to add values to the array
            while (typed.MoveNext())
            {
                values.Add(typed.Current);
            }
        }
        else if (parsed is IEnumerator generic)
        {
            // use the enumerator to add values to the array
            while (generic.MoveNext())
            {
                if (generic.Current is T tValue)
                {
                    values.Add(tValue);
                }
                else
                {
                    throw new Exception("Should not be here!");
                }
            }
        }
        else
        {
            throw new Exception("Should not be here!");
        }

        // if no values were added, return the default - parser cannot tell the difference between no values and default values
        if (values.Count == 0)
        {
            return defaultValue;
        }

        return values;
    }
    /// <summary>Parses the given parse result.</summary>
    /// <param name="parseResult">The parse result.</param>
    public virtual void Parse(System.CommandLine.ParseResult parseResult)
    {
        foreach (ConfigurationOption opt in _options)
        {
            switch (opt.Name)
            {
                case "FhirCache":
                    {
                        string? dir = GetOpt(parseResult, opt, FhirCacheDirectory);

                        if ((dir != null) && dir.EndsWith(".fhir"))
                        {
                            dir = Path.Combine(dir, "packages");
                        }

                        if (string.IsNullOrEmpty(dir))
                        {
                            dir = FileSystemUtils.FindRelativeDir(string.Empty, "~/.fhir/packages");
                        }
                        else if (!Path.IsPathRooted(dir))
                        {
                            dir = FileSystemUtils.FindRelativeDir(string.Empty, dir!);
                        }

                        FhirCacheDirectory = string.IsNullOrEmpty(dir) ? null : dir;
                    }
                    break;
                case "UseOfficialRegistries":
                    UseOfficialRegistries = GetOpt(parseResult, opt, UseOfficialRegistries);
                    break;
                case "AdditionalFhirRegistryUrls":
                    AdditionalFhirRegistryUrls = GetOptArray(parseResult, opt, AdditionalFhirRegistryUrls);
                    break;
                case "AdditionalNpmRegistryUrls":
                    AdditionalNpmRegistryUrls = GetOptArray(parseResult, opt, AdditionalNpmRegistryUrls);
                    break;
                case "OutputPath":
                    {
                        string dir = GetOpt(parseResult, opt, OutputDirectory);

                        if (string.IsNullOrEmpty(dir))
                        {
                            dir = FileSystemUtils.FindRelativeDir(string.Empty, ".");
                        }
                        else if (!Path.IsPathRooted(dir))
                        {
                            dir = FileSystemUtils.FindRelativeDir(string.Empty, dir);
                        }

                        OutputDirectory = dir;
                    }
                    break;
                case "OutputFilename":
                    OutputFilename = GetOpt(parseResult, opt, OutputFilename);
                    break;
                case "Packages":
                    Packages = GetOptArray(parseResult, opt, Packages);
                    break;
                case "AutoLoadExpansions":
                    AutoLoadExpansions = GetOpt(parseResult, opt, AutoLoadExpansions);
                    break;
                case "MaxExpansionSize":
                    MaxExpansionSize = GetOpt(parseResult, opt, MaxExpansionSize);
                    break;
                case "LoadStructures":
                    LoadStructures = GetOptArray(parseResult, opt, LoadStructures);
                    break;
                case "ExportStructures":
                    ExportStructures = GetOptArray(parseResult, opt, ExportStructures);
                    break;
                case "ExportKeys":
                    ExportKeys = GetOptHash(parseResult, opt.CliOption, ExportKeys);
                    break;
                case "LoadCanonicalExamples":
                    LoadCanonicalExamples = GetOpt(parseResult, opt, LoadCanonicalExamples);
                    break;
                case "OfflineMode":
                    OfflineMode = GetOpt(parseResult, opt, OfflineMode);
                    break;
                case "ResolvePackageDependencies":
                    ResolvePackageDependencies = GetOpt(parseResult, opt, ResolvePackageDependencies);
                    break;
                case "FhirVersion":
                    FhirVersion = GetOpt(parseResult, opt, FhirVersion);
                    break;
            }
        }
    }

    //[ConfigOption(
    //    ArgAliases = new[] { "--terminology-server", "--tx" },
    //    EnvName = "Terminology_Server",
    //    ArgArity = "0..*",
    //    Description = "FHIR URL for a terminology server to use")]
    //public string[] TxServers { get; set; } = Array.Empty<string>();
}
