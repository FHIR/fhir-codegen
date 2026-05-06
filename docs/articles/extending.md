# Extending: Adding a New Language Exporter

`fhir-codegen`'s extension point is the
[`ILanguage`](xref:Fhir.CodeGen.Lib.Language.ILanguage) interface
under `src/Fhir.CodeGen.Lib/Language/`. Adding a new exporter is a
pure code-add: you write a new class, the
[`LanguageManager`](xref:Fhir.CodeGen.Lib.Language.LanguageManager)
discovers it via reflection, and the CLI surfaces it as a
`generate <name>` subcommand automatically.

## The recipe

1. **Pick a name and location.** Single-file exporters live directly
   under `src/Fhir.CodeGen.Lib/Language/<Name>.cs` (see `TypeScript.cs`).
   More complex exporters get their own folder, e.g. `OpenApi/`,
   `Firely/`, `Ruby/`. The folder convention scales better — language
   common types and helpers live next to the exporter.
2. **Implement `ILanguage`.** The required surface is small: a
   `Name` property and an `Export(ICodeGenConfig, DefinitionCollection)`
   method. Look at
   [`Info.LangInfo`](xref:Fhir.CodeGen.Lib.Language.Info.LangInfo) as a
   minimal single-file exporter, and at
   [`OpenApi.LangOpenApi`](xref:Fhir.CodeGen.Lib.Language.OpenApi.LangOpenApi)
   as a "model-builder" exporter that converts the generic normalized
   model into language-specific intermediate types before writing.
3. **Define an options class** that derives from
   [`ConfigGenerate`](xref:Fhir.CodeGen.Lib.Configuration.ConfigGenerate).
   The convention is a nested `<Language>Options` class on the
   exporter, or a sibling `<Language>Options.cs` file. This class
   carries every CLI flag the exporter accepts.
4. **Surface each option twice — once as a `[ConfigOption]` attribute,
   once as a `ConfigurationOption` static.** This is the pattern used
   throughout the existing exporters, e.g.
   `src/Fhir.CodeGen.Lib/Configuration/ConfigSql.cs`.

   ```csharp
   [ConfigOption(
       ArgName = "--my-flag",
       EnvName = "My_Flag",
       Description = "Toggle the thing.")]
   public bool MyFlag { get; set; } = false;

   private static ConfigurationOption MyFlagParameter { get; } = new()
   {
       Name = "MyFlag",
       EnvVarName = "My_Flag",
       DefaultValue = false,
       CliOption = new System.CommandLine.Option<bool>(
           "--my-flag", "Toggle the thing.")
       {
           Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
           IsRequired = false,
       },
   };
   ```

   ### Why two?

   The `[ConfigOption]` attribute is consumed by helper code that
   binds parsed arguments back onto the strongly-typed properties of
   the options class. The `ConfigurationOption` static is what
   [`LaunchUtils.BuildCliOptions`](xref:fhir_codegen_shared.LaunchUtils)
   walks to build the actual `System.CommandLine.Option<T>` tree
   handed to the parser.

   The two-surface pattern is **deliberate** today, but it is also a
   known drift risk: if you add a new flag and forget to define its
   `ConfigurationOption`, the CLI will silently ignore it; if you
   change a default in only one place, the parser and the binder will
   disagree. **Keep them in sync.** Several `*OptionsTests` already
   exist in `Fhir.CodeGen.Lib.Tests` to pin this down for individual
   languages; if you add a new exporter, add a similar pin-test.

5. **You're done.** No registry to update. The next
   `dotnet build fhir-codegen.sln` will pick the new exporter up via
   `LanguageManager.LoadLanguages()`, the next `fhir-codegen --help`
   will list `generate <yourname>` as a subcommand, and the next CI
   docs build will add a `### generate <yourname>` section to
   [Command Line Usage](cli.md) automatically.

## Discovery, in detail

`LanguageManager.LoadLanguages()` walks `typeof(ILanguage).Assembly`
once at static-init time and registers every concrete type that
implements `ILanguage`. The lookup is name-based and case-insensitive,
so `generate TypeScript` and `generate typescript` both resolve.

This means **every exporter must live in the
`Fhir.CodeGen.Lib` assembly** (or be loaded into it before
`LanguageManager` is touched). Out-of-assembly exporters are not
currently supported.

## Output discipline

- Tests do **not** write generated artifacts to disk by default —
  `Fhir.CodeGen.Lib.Tests.GenerationTestBase.WriteGeneratedFiles =
  false`. Don't flip it in committed code; toggle it locally when
  debugging a regression.
- The `Export` method should write into the directory passed to it (or
  build its output in memory and let the caller decide where to put
  it). It must not assume CWD.

## Reference exporters

| Pattern | Look at |
|---|---|
| Minimal single-file exporter | `TypeScript.cs`, `Info/LangInfo.cs` |
| Folder layout with shared helpers | `Firely/`, `OpenApi/`, `Ruby/`, `Cql/` |
| Two-stage (normalize → language-specific model → emit) | `OpenApi/ModelBuilder.cs` |
| Database-backed exporter | `SQLite/LangSQLite.cs` (delegates to `Fhir.CodeGen.LangSQLite`) |

## See also

- [Export Languages](languages.md) — current set and how to choose one.
- [Command Line Usage](cli.md) — generated CLI page; verify your new
  exporter shows up here after a build.
