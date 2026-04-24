// <copyright file="Program.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics;
using System.Runtime.InteropServices;
//using fhir_codegen.Components;
using Fhir.CodeGen.Lib.Configuration;
//using Microsoft.AspNetCore.Hosting.StaticWebAssets;
//using Microsoft.FluentUI.AspNetCore.Components;
using Fhir.CodeGen.Lib.Language;
using System.Reflection;
using Fhir.CodeGen.Common.Models;
using System.CommandLine;
using Fhir.CodeGen.Lib.Extensions;
using System.CommandLine.Builder;
using System.Text;
using Microsoft.Extensions.Primitives;
using System.CommandLine.Parsing;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Collections;
using Hl7.Fhir.Utility;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using System.Resources;
using Microsoft.Extensions.Configuration;
using Fhir.CodeGen.Lib.Net;
using Fhir.CodeGen.Common.Smart;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.XVer;
using static fhir_codegen_shared.LaunchUtils;
using Fhir.CodeGen.Lib.SqlOnFhir;
using Fhir.CodeGen.Comparison.Models;

namespace fhir_codegen;

/// <summary>A program.</summary>
public class Program
{
    private static HashSet<string> _packageAliases =
    [
        "--package", "--load-package", "-p"
    ];

    /// <summary>Main entry-point for this application.</summary>
    /// <param name="args">An array of command-line argument strings.</param>
    /// <returns>
    /// An asynchronous result that yields exit-code for the process - 0 for success, else an error
    /// code.
    /// </returns>
    public static async Task<int> Main(string[] args)
    {
        // setup our configuration defaults (environment > appsettings.json) - args will supersede
        IConfiguration envConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // in order to process help correctly we have to build a parser independent of the command
        Parser parser = BuildParser(envConfig);

        // attempt a parse
        System.CommandLine.ParseResult pr = parser.Parse(args);

        string command;
        string? subCommand;

        if ((pr.CommandResult.Parent != null) &&
            (pr.CommandResult.Parent?.Symbol.Name != pr.RootCommandResult.Symbol.Name))
        {
            command = pr.CommandResult.Parent!.Symbol.Name;
            subCommand = pr.CommandResult.Command.Name;
        }
        else
        {
            command = pr.CommandResult.Command.Name;
            subCommand = null;
        }

        // check for invalid arguments, help, a generate command with no subcommand, or a generate with no packages to trigger the nicely formatted help
        if (pr.UnmatchedTokens.Any() ||
            !pr.Tokens.Any() ||
            (!pr.CommandResult.Command.Parents?.Any() ?? false) ||
            pr.Tokens.Any(t => t.Value.Equals("-?", StringComparison.Ordinal)) ||
            pr.Tokens.Any(t => t.Value.Equals("-h", StringComparison.Ordinal)) ||
            pr.Tokens.Any(t => t.Value.Equals("--help", StringComparison.Ordinal)) ||
            pr.Tokens.Any(t => t.Value.Equals("help", StringComparison.Ordinal)) ||
            ((command == "generate") && (subCommand == null)))
        {
            return await parser.InvokeAsync(args);
        }

        // check for a generate command with no packages
        if ((command == "generate") &&
            (!pr.Tokens.Any(t => _packageAliases.Contains(t.Value))))
        {
            Console.WriteLine("Error: generate command requires at least one package to process.");

            return await parser.InvokeAsync(args.Append("--help").ToArray());
        }

        return command switch
        {
            "generate" => await DoGenerate(pr, command, subCommand),
            "compare" => await DoCompare(pr, command, subCommand),
            "xver" => await DoXVer(pr, command, subCommand),
            //"cross-version" => await CrossVersionInteractive.DoCrossVersionReview(pr, command, subCommand),
            //"interactive" => await DoInteractive(pr, command, subCommand);
            //"web" => await DoWeb(pr, command, subCommand);
            "sql" => await DoSql(pr, command, subCommand),
            _ => await parser.InvokeAsync(args),
        };
    }

    public static async Task<int> DoGenerate(System.CommandLine.ParseResult pr, string command, string? subCommand)
    {
        try
        {
            if (subCommand == null)
            {
                throw new Exception("Generation requires a language to be specified");
            }

            ICodeGenConfig config = ParseConfig(pr, command, subCommand);

            if (config is not ConfigRoot rootConfig)
            {
                throw new Exception("Config type must inherit from ConfigRoot");
            }

            if (config is not ConfigGenerate genConfig)
            {
                throw new Exception("Config type must inherit from ConfigGenerate");
            }

            if (!LanguageManager.TryGetLanguage(subCommand, out ILanguage? iLang))
            {
                throw new Exception($"Language type must implement ILanguage, {subCommand}");
            }

            PackageLoader loader = new(rootConfig, new()
            {
                JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
            });

            DefinitionCollection? loaded = await loader.LoadPackages(rootConfig.Packages, null, rootConfig.FhirVersion)
                ?? throw new Exception($"Could not load packages: {string.Join(',', rootConfig.Packages)}");

            // check for a FHIR server URL
            if (!string.IsNullOrEmpty(genConfig.FhirServerUrl))
            {
                // parse any HTTP headers the caller has provided
                Dictionary<string, List<string>> headers = ParseHttpHeaderArgs(genConfig.FhirServerHeaders);

                // create our server connector
                ServerConnector serverConnector = new(genConfig.FhirServerUrl, genConfig.SmartConfigUrl, headers, loader);

                // try to get the capability statement
                if (serverConnector.TryGetCapabilities(out _, out _, out Hl7.Fhir.Model.CapabilityStatement? capStatement, out FhirReleases.FhirSequenceCodes? serverFhirVersion))
                {
                    genConfig.ServerCapabilities = capStatement;

                    if (genConfig.ResolveServerCanonicals)
                    {
                        serverConnector.TryResolveCanonicals(capStatement, loaded, genConfig.ResolveExternalCanonicals, out _);
                    }
                }

                // try to get SMART config
                if (serverConnector.TryGetSmartConfig(out _, out _, out SmartWellKnown? smartConfig))
                {
                    genConfig.ServerSmartConfig = smartConfig;
                }
                else if ((capStatement != null) && serverConnector.TryBuildSmartConfig(capStatement, out smartConfig))
                {
                    genConfig.ServerSmartConfig = smartConfig;
                }
            }

            // call the export method on the language object
            iLang.Export(config, loaded);
        }
        catch (Exception ex)
        {
            if (ex.InnerException != null)
            {
                Console.WriteLine($"RunGenerate <<< caught: {ex.Message}::{ex.InnerException.Message}");
            }
            else
            {
                Console.WriteLine($"RunGenerate <<< caught: {ex.Message}");
            }
        }

        return 0;
    }

    public static async Task<int> DoCompare(System.CommandLine.ParseResult pr, string command, string? subCommand)
    {
        try
        {
            // create our configuration object
            ConfigCompare config = new();

            // parse the arguments into the configuration object
            config.Parse(pr);

            PackageLoader loaderLeft = new(config, new()
            {
                JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
            });


            DefinitionCollection? loadedLeft = await loaderLeft.LoadPackages(config.Packages)
                ?? throw new Exception($"Could not load left-hand-side packages: {string.Join(',', config.Packages)}");

            PackageLoader loaderRight = new(config, new()
            {
                JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
            });

            DefinitionCollection? loadedRight = await loaderLeft.LoadPackages(config.ComparePackages)
                ?? throw new Exception($"Could not load right-hand-side packages: {string.Join(',', config.Packages)}");

            PackageComparer comparer = new(config, loadedLeft, loadedRight);

            if (comparer.Compare() is PackageComparison pc)
            {
                if (config.SaveComparisonResult)
                {
                    comparer.WriteComparisonResultJson(pc);
                }

                if (config.NoOutput != true)
                {
                    comparer.WriteMarkdownFiles(pc);
                    comparer.WriteCrossVersionExtensionArtifacts(pc);
                }

                if (config.MapSaveStyle != ConfigCompare.ComparisonMapSaveStyle.None)
                {
                    comparer.WriteMapFiles(pc);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex.InnerException != null)
            {
                Console.WriteLine($"DoCompare <<< caught: {ex.Message}::{ex.InnerException.Message}");
            }
            else
            {
                Console.WriteLine($"DoCompare <<< caught: {ex.Message}");
            }
        }

        return 0;
    }

    public static Task<int> DoXVer(System.CommandLine.ParseResult pr, string command, string? subCommand)
    {
        try
        {
            // create our configuration object
            ConfigXVer config = new();

            // parse the arguments into the configuration object
            config.Parse(pr);

            XVerProcessor xVerProcessor = new(config);
            xVerProcessor.ProcessCommand(subCommand);
        }
        catch (Exception ex)
        {
            if (ex.InnerException != null)
            {
                Console.WriteLine($"DoXVer <<< caught: {ex.Message}::{ex.InnerException.Message}");
            }
            else
            {
                Console.WriteLine($"DoXVer <<< caught: {ex.Message}");
            }
        }

        return Task.FromResult(0);
    }


    public static async Task<int> DoSql(System.CommandLine.ParseResult pr, string command, string? subCommand)
    {
        try
        {
            // create our configuration object
            ConfigSql config = new();

            // parse the arguments into the configuration object
            config.Parse(pr);

            // create a loader because these are all different FHIR core versions
            PackageLoader loader = new(config, new()
            {
                JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
            });

            DefinitionCollection dc = await loader.LoadPackages(config.Packages)
                ?? throw new Exception($"Could not load packages: {string.Join(", ", config.Packages)}");

            SqlOnFhirProcessor processor = new(config);
            processor.Process(dc);
        }
        catch (Exception ex)
        {
            if (ex.InnerException != null)
            {
                Console.WriteLine($"DoCompare <<< caught: {ex.Message}::{ex.InnerException.Message}");
            }
            else
            {
                Console.WriteLine($"DoCompare <<< caught: {ex.Message}");
            }
        }

        return 0;
    }

    ///// <summary>web UI.</summary>
    ///// <param name="config">           The configuration.</param>
    ///// <param name="cancellationToken">A token that allows processing to be cancelled.</param>
    ///// <returns>An asynchronous result that yields an int.</returns>
    //public static async Task<int> RunServer(ConfigFluentUi config, CancellationToken cancellationToken)
    //{
    //    try
    //    {
    //        WebApplicationBuilder builder = null!;

    //        // when packaging as a dotnet tool, we need to do some directory shenanigans for the static content root
    //        string root = Contents.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location ?? AppContext.BaseDirectory) ?? string.Empty;
    //        if (!string.IsNullOrEmpty(root))
    //        {
    //            string webRoot = FindRelativeDir(root, "staticwebassets", false);

    //            if ((!string.IsNullOrEmpty(webRoot)) && Directory.Exists(webRoot))
    //            {
    //                builder = WebApplication.CreateBuilder(new WebApplicationOptions()
    //                {
    //                    WebRootPath = webRoot,
    //                });
    //            }
    //        }

    //        if (builder == null)
    //        {
    //            builder = WebApplication.CreateBuilder();
    //        }

    //        StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

    //        builder.WebHost.UseStaticWebAssets();

    //        // add our configuration before starting things up
    //        builder.Services.AddSingleton(config);

    //        // add any locally-defined we want injected
    //        //builder.Services.AddSingleton<IExample, Example>();
    //        //builder.Services.AddHostedService<IExample>(sp => sp.GetRequiredService<IExample>());

    //        // Add services - do individually for control over middleware order
    //        builder.Services.AddCors();
    //        builder.Services.AddControllers();
    //        builder.Services.AddRazorComponents()
    //                .AddInteractiveServerComponents();

    //        //builder.Services.AddFluentUIComponents();

    //        // use the requested default port
    //        string localUrl = $"http://*:{config.UiListenPort}";

    //        builder.WebHost.UseUrls(localUrl);

    //        WebApplication app = builder.Build();

    //        // we want to essentially disable CORS
    //        app.UseCors(builder => builder
    //            .AllowAnyOrigin()
    //            .AllowAnyMethod()
    //            .AllowAnyHeader()
    //            .WithExposedHeaders(new[] { "Content-Location", "Location", "Etag", "Last-Modified" }));

    //        app.UseStaticFiles();
    //        app.UseRouting();
    //        app.MapControllers();

    //        // this is developer tooling - always respond with as much detail as we can
    //        app.UseDeveloperExceptionPage();

    //        // add UI components
    //        app.MapRazorComponents<App>()
    //            .AddInteractiveServerRenderMode();

    //        app.MapFallbackToPage("/_Host");

    //        // perform initialization of services
    //        //IExample example = app.Services.GetRequiredService<IExample>();
    //        //ExampleScenario.Init();

    //        // start the webserver
    //        _ = app.StartAsync();

    //        // perform any post-startup actions
    //        AfterServerStart(app, config);

    //        // wait for the server to shutdown
    //        await app.WaitForShutdownAsync(cancellationToken);

    //        // return success
    //        return 0;
    //    }
    //    catch (OperationCanceledException)
    //    {
    //        return 0;
    //    }
    //    catch (Exception ex)
    //    {
    //        Console.WriteLine($"fhir-candle <<< caught exception: {ex.Message}");
    //        return -1;
    //    }
    //}

    ///// <summary>Actions to take after the server has started.</summary>
    ///// <param name="app">   The application.</param>
    ///// <param name="config">The configuration.</param>
    //private static void AfterServerStart(WebApplication app, ConfigFluentUi config)
    //{
    //    Console.WriteLine("Press CTRL+C to exit");

    //    if (config.OpenBrowser == true)
    //    {
    //        string url = $"http://localhost:{config.UiListenPort}";

    //        LaunchBrowser(url);
    //    }
    //}
}
