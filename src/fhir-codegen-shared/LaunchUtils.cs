// <copyright file="LaunchUtils.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Language;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Lib.Extensions;
using Microsoft.Extensions.Primitives;
using System.Collections;
using Hl7.Fhir.Utility;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Fhir.CodeGen.Common.Extensions;


namespace fhir_codegen_shared;

internal static class LaunchUtils
{
    internal static Dictionary<string, LanguageOptionInfo> _configMapsByLang = [];

    private static List<Option> _optsWithEnums = [];

    internal record class LanguageOptionInfo
    {
        public required string Name { get; set; }

        public required Type ConfigType { get; set; }

        public List<PropertyOptionTuple> Properties { get; set; } = [];
    }

    internal record class PropertyOptionTuple
    {
        public required PropertyInfo ConfigProp { get; set; }

        public required Option CommandOpt { get; set; }
    }

    internal record class LaunchCommandRecord
    {
        public required string Literal { get; init; }
        public required string Description { get; init; }
        public required Type ConfigurationType { get; init; }
        public Type? ExcludedConfigurationType { get; init; } = typeof(ConfigRoot);
        public bool IncludeLanguageSubCommands { get; init; } = false;
        public bool Disabled { get; init; } = false;
        public (string literal, string description)[] SubCommands { get; init; } = [];
    }

    private static readonly LaunchCommandRecord[] _commands = [
        new()
        {
            Literal = "generate",
            Description = "Generate output from a FHIR package.",
            ConfigurationType = typeof(ConfigGenerate),
            IncludeLanguageSubCommands = true,
        },
        new()
        {
            Literal = "compare",
            Description = "Compare two sets of packages",
            ConfigurationType = typeof(ConfigCompare),
        },
        new()
        {
            Literal = "xver",
            Description = "Perform FHIR Core Cross-Version processing",
            ConfigurationType = typeof(ConfigXVer),
            SubCommands = [
                ("load", "Load contents for processing (core and maps)"),
                ("load-base", "Load FHIR Core package definitions"),
                ("load-maps", "Load FHIR-Cross-Version-Source maps"),
                ("load-substitutions", "Load and expand extension substitutions"),
                ("compare", "Run a comparison and update the database"),
                ("compare-vs", "Run a comparison of ValueSet data and update the database"),
                ("compare-sd", "Run a comparison of Structure data and update the database"),
                ("outcomes", "Build outcomes from the comparisons and update the database"),
                ("outcomes-vs", "Build outcomes from the comparisons for ValueSets and update the database"),
                ("outcomes-sd", "Build outcomes from the comparisons for Structures and update the database"),

                ("export", "Export outcome data into packages"),

                ("wip", "Work in progress shortcut. TODO: Remove"),
                ("update-vs-maps", "Update the FHIR Cross Version maps for value sets"),

                ],
        },
        new()
        {
            Literal = "sql",
            Description = "Perform SQL on FHIR v2 transformations",
            ConfigurationType = typeof(ConfigSql),
            Disabled = true,
        },
        new()
        {
            Literal = "docs",
            Description = "Documentation tooling.",
            ConfigurationType = typeof(ConfigDocs),
            SubCommands = [
                ("cli", "Generate Markdown describing the CLI surface."),
            ],
        },
    ];

    /// <summary>
    /// Gets the enabled top-level commands in the order the parser exposes them.
    /// </summary>
    /// <remarks>
    /// Matches the filter and ordering at <see cref="BuildCommand(IConfiguration)"/>
    /// (drops <see cref="LaunchCommandRecord.Disabled"/> entries, sorts by
    /// <see cref="LaunchCommandRecord.Literal"/>).
    /// </remarks>
    internal static IEnumerable<LaunchCommandRecord> EnabledCommands =>
        _commands.Where(c => !c.Disabled).OrderBy(c => c.Literal, StringComparer.Ordinal);

    /// <summary>Parses the configuration based on the provided command and parse result.</summary>
    /// <exception cref="Exception">Thrown when the language type cannot be found, the configuration
    ///  object cannot be created, or the configuration type does not implement <see cref="ICodeGenConfig"/>
    ///  or inherit from <see cref="ConfigRoot"/>.</exception>
    /// <param name="pr">           The parse result containing the command line arguments.</param>
    /// <param name="command">      The command to determine the type of configuration to create.</param>
    /// <param name="subCommand">   The sub command.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>
    /// An instance of <see cref="ICodeGenConfig"/> representing the parsed configuration.
    /// </returns>
    internal static ICodeGenConfig ParseConfig(
        System.CommandLine.ParseResult pr,
        string command,
        string? subCommand,
        ILoggerFactory? loggerFactory = null)
    {
        ICodeGenConfig config;

        switch (command)
        {
            case "generate":
                {
                    string languageName = subCommand ?? string.Empty;

                    if (!LanguageManager.TryGetLanguage(languageName, out ILanguage? language))
                    {
                        throw new Exception($"Could not find language type for {languageName}");
                    }

                    // get our language type
                    Type langType = LanguageManager.TypeForLanguage(languageName);

                    // get our language configuration type
                    Type configType = LanguageManager.ConfigTypeForLanguage(language.Name);

                    // create our configuration object
                    object? configGeneric = Activator.CreateInstance(configType)
                        ?? throw new Exception($"Could not create configuration object for {languageName} ({configType.Name})");

                    if (configGeneric is not ICodeGenConfig tc)
                    {
                        throw new Exception($"Config type must implement ICodeGenConfig, {languageName} ({configType.Name})");
                    }

                    if (tc is not ConfigRoot rootConfig)
                    {
                        throw new Exception("Config type must inherit from ConfigRoot");
                    }

                    rootConfig.LaunchCommand = command;
                    if (loggerFactory != null)
                    {
                        rootConfig.LogFactory = loggerFactory;
                    }
                    config = tc;
                }
                break;

            case "compare":
                config = new ConfigCompare()
                {
                    LaunchCommand = command,
                    LogFactory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddConsole()),
                };
                break;

            case "xver":
                config = new ConfigXVer()
                {
                    LaunchCommand = command,
                    LogFactory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddConsole()),
                };
                break;

            case "sql":
                config = new ConfigSql()
                {
                    LaunchCommand = command,
                    LogFactory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddConsole()),
                };
                break;

            case "docs":
                config = new ConfigDocs()
                {
                    LaunchCommand = command,
                    LogFactory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddConsole()),
                };
                break;

            case "help":
            default:
                config = new ConfigRoot()
                {
                    LaunchCommand = command,
                    LogFactory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddConsole()),
                };
                break;
        }

        // parse the arguments into the configuration object
        config.Parse(pr);

        // return our configuration object
        return config;
    }

    /// <summary>
    /// Builds the CLI surface for the application: returns the configured
    /// <see cref="RootCommand"/> together with the per-parser and per-invocation
    /// configurations expected by System.CommandLine 2.0 GA.
    /// </summary>
    /// <param name="envConfig">The environment configuration.</param>
    /// <param name="rc">Optional pre-built root command (used by tests).</param>
    /// <returns>Tuple of root, parser configuration, invocation configuration.</returns>
    internal static (RootCommand Root, ParserConfiguration ParserConfig, InvocationConfiguration InvocationConfig) BuildCli(
        IConfiguration envConfig,
        RootCommand? rc = null)
    {
        RootCommand command = rc ?? BuildCommand(envConfig);

        // Replace the default help action with one that prepends per-enum option detail.
        CustomizeRootHelp(command);

        ParserConfiguration parserConfig = new();
        InvocationConfiguration invocationConfig = new()
        {
            // We catch and report exceptions ourselves in Program.Main to match the
            // beta4 UX (single-line "Error: ..." with exit code 1).
            EnableDefaultExceptionHandler = false,
        };

        return (command, parserConfig, invocationConfig);
    }

    /// <summary>
    /// Replaces the help action on the root command's <see cref="HelpOption"/> with an
    /// <see cref="EnumAwareHelpAction"/> that prints enum option detail before the
    /// standard help layout. <see cref="HelpOption"/> is recursive by default in
    /// System.CommandLine 2.0 GA, so customizing the root suffices for subcommands.
    /// </summary>
    /// <param name="root">The root command to customize.</param>
    private static void CustomizeRootHelp(RootCommand root)
    {
        HelpOption? helpOpt = root.Options.OfType<HelpOption>().FirstOrDefault();
        if (helpOpt != null)
        {
            helpOpt.Action = new EnumAwareHelpAction(_optsWithEnums);
        }
    }

    /// <summary>
    /// Custom help action that prints a description block for every enum-typed option
    /// (mirroring the beta4 <c>UseHelp</c> + <c>HelpBuilder.CustomizeSymbol</c> flow)
    /// before delegating to the stock <see cref="HelpAction"/> for the standard layout.
    /// <see cref="HelpBuilder"/> is internal in System.CommandLine 2.0 GA, so we cannot
    /// per-symbol customize the column rendering directly; emitting the enum block as
    /// preamble preserves the information content with a minor cosmetic shift.
    /// </summary>
    internal sealed class EnumAwareHelpAction : SynchronousCommandLineAction
    {
        private readonly List<Option> _enumOptions;
        private readonly HelpAction _inner = new();

        public EnumAwareHelpAction(List<Option> enumOptions)
        {
            // Same Option<T> instance is added to multiple commands by BuildCommand
            // (root + language subcommands), so it shows up multiple times in
            // _optsWithEnums. Deduplicate by reference here so the help block
            // doesn't repeat.
            _enumOptions = [.. enumOptions.Distinct()];
        }

        public override int Invoke(ParseResult parseResult)
        {
            // Restrict the enum block to options actually present on the command being
            // helped (or any of its ancestors, since recursive options propagate).
            HashSet<Option> visible = CollectVisibleOptions(parseResult.CommandResult.Command);

            foreach (Option option in _enumOptions)
            {
                if (!visible.Contains(option))
                {
                    continue;
                }

                Console.Out.WriteLine(BuildEnumColumn(option));
            }

            return _inner.Invoke(parseResult);
        }

        private static HashSet<Option> CollectVisibleOptions(Command cmd)
        {
            HashSet<Option> result = [];
            Command? cursor = cmd;
            while (cursor != null)
            {
                foreach (Option opt in cursor.Options)
                {
                    result.Add(opt);
                }

                cursor = cursor.Parents.OfType<Command>().FirstOrDefault();
            }

            return result;
        }

        private static string BuildEnumColumn(Option option)
        {
            StringBuilder sb = new();
            if (option.Aliases.Count != 0)
            {
                sb.AppendLine(string.Join(", ", option.Aliases));
            }
            else
            {
                sb.AppendLine(option.Name);
            }

            Type et = option.ValueType;

            if (option.ValueType.IsGenericType)
            {
                et = option.ValueType.GenericTypeArguments.First();
            }

            if (option.ValueType.IsArray)
            {
                et = option.ValueType.GetElementType()!;
            }

            foreach (MemberInfo mem in et.GetMembers(BindingFlags.Public | BindingFlags.Static).Where(m => m.DeclaringType == et).OrderBy(m => m.Name))
            {
                IEnumerable<DescriptionAttribute> attributes = mem.GetCustomAttributes<DescriptionAttribute>(false);

                sb.AppendLine($"  opt: {mem.Name}");
                if (attributes.Any())
                {
                    sb.AppendLine($"       {attributes.First().Description}");
                }
            }

            return sb.ToString();
        }
    }


    /// <summary>Builds command parser.</summary>
    /// <param name="envConfig">  The environment configuration.</param>
    /// <param name="addHandlers">True to add handlers.</param>
    /// <returns>A Parser.</returns>
    internal static RootCommand BuildCommand(IConfiguration envConfig)
    {
        // create our root command
        RootCommand rootCommand = new("A utility for processing FHIR packages into other formats/languages.");
        foreach (Option option in BuildCliOptions(typeof(ConfigRoot), envConfig: envConfig))
        {
            // note that 'global' here is just recursive DOWNWARD
            option.Recursive = true;
            rootCommand.Options.Add(option);
            TrackIfEnum(option);
        }

        // iterate over our configured commands
        foreach (LaunchCommandRecord rec in _commands.OrderBy(c => c.Literal))
        {
            if (rec.Disabled)
            {
                continue;
            }

            // build the command
            Command cmd = new(rec.Literal, rec.Description);

            // add the options for this command
            foreach (Option option in BuildCliOptions(rec.ConfigurationType, rec.ExcludedConfigurationType, envConfig))
            {
                // note that 'global' here is just recursive DOWNWARD
                option.Recursive = true;
                cmd.Options.Add(option);
                TrackIfEnum(option);
            }

            // add language subcommands if needed
            if (rec.IncludeLanguageSubCommands)
            {
                foreach (ILanguage language in LanguageManager.GetLanguages())
                {
                    Command languageCommand = new(language.Name, $"{rec.Literal} {language.Name}");
                    if (language.Name.Any(char.IsUpper))
                    {
                        languageCommand.Aliases.Add(language.Name.ToLowerInvariant());
                    }

                    foreach (Option option in BuildCliOptions(LanguageManager.ConfigTypeForLanguage(language.Name), envConfig: envConfig))
                    {
                        languageCommand.Options.Add(option);
                        TrackIfEnum(option);
                    }

                    foreach (Option option in BuildCliOptions(rec.ConfigurationType, rec.ExcludedConfigurationType, envConfig))
                    {
                        languageCommand.Options.Add(option);
                        TrackIfEnum(option);
                    }

                    cmd.Subcommands.Add(languageCommand);
                }
            }

            // add manual subcommands if needed
            foreach ((string literal, string description) in rec.SubCommands)
            {
                Command subCommand = new(literal, description);
                if (literal.Any(char.IsUpper))
                {
                    subCommand.Aliases.Add(literal.ToLowerInvariant());
                }

                cmd.Subcommands.Add(subCommand);
            }

            // add this command to our root command
            rootCommand.Subcommands.Add(cmd);
        }

        //// create our generate command
        //Command generateCommand = new("generate", "Generate output from a FHIR package and exit.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigGenerate), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    generateCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// iterate through languages and add them as subcommands
        //foreach (ILanguage language in LanguageManager.GetLanguages())
        //{
        //    Command languageCommand = new(language.PackageId, $"Generate {language.PackageId}");
        //    if (language.PackageId.Any(char.IsUpper))
        //    {
        //        languageCommand.AddAlias(language.PackageId.ToLowerInvariant());
        //    }

        //    foreach (Option option in BuildCliOptions(LanguageManager.ConfigTypeForLanguage(language.PackageId), envConfig: envConfig))
        //    {
        //        languageCommand.AddOption(option);
        //        TrackIfEnum(option);
        //    }

        //    generateCommand.AddCommand(languageCommand);
        //}

        //rootCommand.AddCommand(generateCommand);

        //// create our interactive command
        //Command interactiveCommand = new("interactive", "Launch into an interactive console.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigInteractive), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    interactiveCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// TODO(ginoc): Set the command handler
        //rootCommand.AddCommand(interactiveCommand);

        //// create our webserver command
        //Command webCommand = new("web", "Launch into a locally-hosted web UI.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigFluentUi), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    webCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// TODO(ginoc): Set the command handler
        //rootCommand.AddCommand(webCommand);

        //// create our compare command
        //Command compareCommand = new("compare", "CompareValueSets two sets of packages.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigCompare), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    compareCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// TODO(ginoc): Set the command handler
        //rootCommand.AddCommand(compareCommand);

        //// create our XVer command
        //Command xverCommand = new("xver", "Perform FHIR Core Cross-Version processing.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigXVer), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    xverCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// TODO(ginoc): Set the command handler
        //rootCommand.AddCommand(xverCommand);

        //// create our SQL command
        //Command sqlCommand = new("sql", "Perform SQL on FHIR (v2) processing.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigSql), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    sqlCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// TODO(ginoc): Set the command handler
        //rootCommand.AddCommand(sqlCommand);


        ////// create our cross-version interactive command
        ////Command cviCommand = new("cross-version", "Interactively review cross-version definitions.");
        ////foreach (Option option in BuildCliOptions(typeof(ConfigCrossVersionInteractive), typeof(ConfigRoot), envConfig))
        ////{
        ////    // note that 'global' here is just recursive DOWNWARD
        ////    cviCommand.AddGlobalOption(option);
        ////    TrackIfEnum(option);
        ////}

        ////// TODO(ginoc): Set the command handler
        ////rootCommand.AddCommand(cviCommand);

        //// create our UI command
        //Command guiCommand = new("gui", "Launch the GUI.");
        //foreach (Option option in BuildCliOptions(typeof(ConfigGui), typeof(ConfigRoot), envConfig))
        //{
        //    // note that 'global' here is just recursive DOWNWARD
        //    guiCommand.AddGlobalOption(option);
        //    TrackIfEnum(option);
        //}

        //// TODO(ginoc): Set the command handler
        //rootCommand.AddCommand(guiCommand);


        return rootCommand;
    }

    internal static void TrackIfEnum(Option option)
    {
        if (option.ValueType.IsEnum)
        {
            _optsWithEnums.Add(option);
            return;
        }

        if (option.ValueType.IsGenericType)
        {
            if (option.ValueType.GenericTypeArguments.First().IsEnum)
            {
                _optsWithEnums.Add(option);
            }

            return;
        }

        if (option.ValueType.IsArray)
        {
            if (option.ValueType.GetElementType()!.IsEnum)
            {
                _optsWithEnums.Add(option);
            }

            return;
        }
    }


    internal static IEnumerable<Option> BuildCliOptions(
        Type forType,
        Type? excludeFromType = null,
        IConfiguration? envConfig = null)
    {
        HashSet<string> inheritedPropNames = [];

        if (excludeFromType != null)
        {
            PropertyInfo[] exProps = excludeFromType.GetProperties();
            foreach (PropertyInfo exProp in exProps)
            {
                inheritedPropNames.Add(exProp.Name);
            }
        }

        object? configDefault = null;
        if (forType.IsAbstract)
        {
            throw new Exception($"Config type cannot be abstract! {forType.Name}");
        }

        configDefault = Activator.CreateInstance(forType);

        if (configDefault is not ICodeGenConfig config)
        {
            throw new Exception("Config type must implement ICodeGenConfig");
        }

        foreach (ConfigurationOption opt in config.GetOptions())
        {
            // Defaults are no longer surfaced through Option<T>.DefaultValueFactory
            // (System.CommandLine 2.0 GA + D1(b)). Runtime resolution is centralized
            // in ConfigRoot.GetOpt/GetOptArray, which honors:
            //   parsed CLI value > Environment.GetEnvironmentVariable(opt.EnvVarName)
            //   > opt.DefaultValue.
            // The only observable change is that --help no longer prints "[default: ...]"
            // for these options.
            yield return opt.CliOption;
        }
    }


    /// <summary>Executes the browser operation.</summary>
    /// <param name="url">URL of the resource.</param>
    internal static void LaunchBrowser(string url)
    {
        ProcessStartInfo psi = new();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            psi.FileName = "open";
            psi.ArgumentList.Add(url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            psi.FileName = "xdg-open";
            psi.ArgumentList.Add(url);
        }
        else
        {
            psi.FileName = "cmd";
            psi.ArgumentList.Add("/C");
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add(url);
        }

        Process.Start(psi);
    }


    internal static Dictionary<string, List<string>> ParseHttpHeaderArgs(List<string> argValues)
    {
        if (argValues.Count == 0)
        {
            return [];
        }

        Dictionary<string, List<string>> headers = [];

        foreach (string header in argValues)
        {
            int separatorLocation = header.IndexOf('=');

            if (separatorLocation == -1)
            {
                // ignore
                continue;
            }

            string key = header[..separatorLocation].Trim();
            string value = header[(separatorLocation + 1)..].Trim();

            if (headers.TryGetValue(key, out List<string>? parsedValues))
            {
                parsedValues.Append(value);
            }
            else
            {
                headers.Add(key, [value]);
            }
        }

        return headers;
    }
}
