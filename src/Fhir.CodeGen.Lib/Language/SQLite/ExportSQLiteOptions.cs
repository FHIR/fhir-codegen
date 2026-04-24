using System;
using System.Collections.Generic;
using System.Text;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Extensions;

namespace Fhir.CodeGen.Lib.Language.SQLite;

public class ExportSQLiteOptions : ConfigGenerate
{
    [ConfigOption(
        ArgName = "--include-extended-structures",
        Description = "If extended structures (e.g., profiles, extensions) should be included.")]
    public bool IncludeExtendedStructures { get; set; } = false;

    private static ConfigurationOption IncludeExtendedStructuresParameter { get; } = new()
    {
        Name = "IncludeExtendedStructures",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--include-extended-structures")
        {
            Description = "If extended structures (e.g., profiles, extensions) should be included.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    [ConfigOption(
        ArgName = "--drop-tables",
        Description = "If tables should be dropped before processing")]
    public bool DropTables { get; set; } = false;

    private static ConfigurationOption DropTablesParameter { get; } = new()
    {
        Name = "DropTables",
        DefaultValue = false,
        CliOption = new System.CommandLine.Option<bool>("--drop-tables")
        {
            Description = "If tables should be dropped before processing",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
            Required = false,
        },
    };

    private static readonly ConfigurationOption[] _options = [
        IncludeExtendedStructuresParameter,
        DropTablesParameter,
        ];

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
                case "IncludeExtendedStructures":
                    IncludeExtendedStructures = GetOpt(parseResult, opt, IncludeExtendedStructures);
                    break;
                case "DropTables":
                    DropTables = GetOpt(parseResult, opt, DropTables);
                    break;
            }
        }
    }
}
