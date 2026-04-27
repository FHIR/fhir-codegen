# Implementation Plan: Migrate to `System.CommandLine` 2.0 GA

| | |
|-|-|
| Slot | `scratch/0424-05/` |
| Source | `bugreport.md` (read-only) |
| Status | Ready-to-execute |
| Created | 2026-04-24 |
| Last updated | 2026-04-24 |

## Problem Recap

The `System.CommandLine` package was bumped from beta4 (`2.0.0-beta4.22272.1`)
to GA (`2.0.7`) in three csprojs, but the source still uses the beta4 API
shape. Build fails with 19 `CS0234` errors because `ParseResult` and `Token`
moved out of `System.CommandLine.Parsing` into the root `System.CommandLine`
namespace. A fuller audit shows the migration is much wider than the namespace
move: `Parser` / `CommandLineBuilder`, `UseDefaults` / `UseHelp` /
`UseExceptionHandler`, `AddGlobalOption` / `AddOption` / `AddCommand` /
`AddAlias`, `SetDefaultValue` / `SetDefaultValueFactory`, `IsRequired`,
`HasOption`, `GetValueForOption`, and the `Option<T>` constructor shape have
all changed in 2.0 GA. The companion package
`System.CommandLine.NamingConventionBinder` is still pinned at the beta4
version, has no remaining usages in the tree, and must be removed.

## Approach

Port the entire CLI surface forward to the GA API, **project by project,
bottom-up**: a small but unavoidable design change to
`Fhir.CodeGen.Lib/Configuration/ConfigurationOption.cs` (**D1**) lands
first because the GA API removed the non-generic `Option`-based
`ParseResult.GetValue(...)` and `Option.SetDefaultValueFactory(...)`
overloads that the current codebase relies on. With that in place the
rest is mechanical (see [API Mapping Cheat Sheet](#api-mapping-cheat-sheet)):
`Fhir.CodeGen.Lib` first, then `Fhir.CodeGen.Comparison`, then the
`fhir-codegen` exe and its shared project (`fhir-codegen-shared/LaunchUtils.cs`,
which owns the `RootCommand` builder and the parser pipeline), then
`Fhir.CodeGen.Lib.Tests`. Phasing exists to keep the tree on a known
trajectory at each checkpoint and to make `git bisect` useful if behavior
regresses; phases 1–4 use **diagnostic gates** (build may still fail; the
remaining errors must fall in expected categories) and phases 5–7 use
**buildable checkpoints**. The final phase removes
`NamingConventionBinder`, runs the full filtered test suite, and
characterizes help output to catch behavioral drift the compiler can't.

## Alternatives Considered

- **Roll `System.CommandLine` back to beta4 in all three csprojs.** Rejected:
  the bug report explicitly chose the upgrade direction; beta4 is unmaintained
  and will block future dependency moves.
- **Migrate everything in one giant commit without phasing.** Rejected: the
  blast radius (~30 files, two parser pipelines, three projects, one test
  fixture) is large enough that a single commit makes review and bisect
  painful; phased commits cost almost nothing because phases align with
  project boundaries that the build system already enforces.
- **Wrap `System.CommandLine.Parsing.ParseResult` behind a project-local
  type alias and only fix the namespace.** Rejected: it papers over the
  `HasOption` / `GetValueForOption` / `SetDefaultValue` / `Parser` / builder
  removals; the build would still fail.
- **Keep `NamingConventionBinder` "just in case".** Rejected: it has zero
  usages in the source tree (no `using System.CommandLine.NamingConventionBinder;`
  anywhere), and its beta4 version is binary-incompatible with
  `System.CommandLine` 2.0.7 at runtime.

## Configuration Precedence Contract

For any configurable option, the effective value is resolved as
(highest-precedence first):

```
CLI argument  >  environment variable  >  appsettings.json  >  default
```

- "CLI argument" — the option appears on the command line, even if the
  value supplied equals the default. Detected via
  `OptionResult.Implicit == false`.
- "environment variable" — `EnvironmentVariablesConfigurationProvider`
  inside the `IConfiguration` built by `Program.Main` (which also
  catches `Environment.GetEnvironmentVariable(opt.EnvVarName)` for the
  same key).
- "appsettings.json" — the merged JSON config from `appsettings.json`
  (and any environment overlay such as `appsettings.Development.json`
  if loaded).
- "default" — the static default declared on the `ConfigurationOption`.

This matches the existing source order at `Program.cs:57-60`
(`AddJsonFile("appsettings.json")` then `AddEnvironmentVariables()` —
last-added wins, so env overrides JSON), and matches the existing
comment "environment > appsettings.json - args will supersede". The
plan does **not** change this ordering; it only formalizes it and adds
test coverage that pins it down.

### Single Source of Truth

All four sources must funnel through **one** code path so precedence is
determined in exactly one place. The chosen path:

1. `Program.Main` builds `IConfiguration envConfig` with the existing
   source order: `AddJsonFile("appsettings.json", optional: true)` then
   `AddEnvironmentVariables()`. `IConfiguration` itself encodes
   `appsettings.json < env`.
2. `LaunchUtils.BuildCliOptions` calls
   `opt.ApplyDefault(envConfig)` (per **D1(a)**), which sets
   `Option<T>.DefaultValueFactory` to read from `envConfig` if the
   `EnvVarName` resolves to a non-empty section, otherwise the static
   `DefaultValue`. This makes `IConfiguration` win over the static
   default.
3. `ConfigRoot.GetOpt<T>` returns the user-supplied CLI value if
   `OptionResult.Implicit == false`; otherwise it returns the
   factory-supplied default (which already encodes appsettings + env).
   The current
   `Environment.GetEnvironmentVariable(opt.EnvVarName)` fallback inside
   `GetOpt`/`GetOptArray` (`ConfigRoot.cs:502, 565`) is **deleted** as
   a hygiene fix: `IConfiguration` already covers env vars via
   `AddEnvironmentVariables`, and keeping a parallel direct lookup is
   what makes precedence ambiguous (and is in fact unreachable in the
   current control flow). Removing it does **not** change observable
   precedence — env vars still flow in via `envConfig`.

This consolidation is mandatory; without it, env vars would be read
from two different code paths.

## Affected Areas

- `src/Fhir.CodeGen.Lib/Configuration/` — the parser plumbing
  (`ConfigRoot.cs`, `ICodeGenConfig.cs`, `ConfigurationOption.cs`,
  `ConfigCompare.cs`, `ConfigCrossVersionInteractive.cs`,
  `ConfigGenerate.cs`, `ConfigSql.cs`, `ConfigXVer.cs`).
- `src/Fhir.CodeGen.Lib/Language/**/*.cs` — every language exporter's
  nested `*Options.Parse(ParseResult)` override and its `Option<T>`
  factory list. Files with confirmed call sites:
  `TypeScript.cs`, `Info/LangInfo.cs`, `SQLite/ExportSQLiteOptions.cs`,
  `Shorthand/ShorthandOptions.cs`, `Ruby/RubyOptions.cs`,
  `Cql/CqlOptions.cs`, `OpenApi/OpenApiOptions.cs` (the heavy one — 63
  flagged sites), `OpenApi/LangOpenApi.cs`, `Firely/FirelyGenOptions.cs`,
  `Firely/FirelyNetIG.cs`.
- `src/Fhir.CodeGen.Lib/Extensions/ConfigOptionAttribute.cs` — verify
  `Option`/`Option<T>` references still compile.
- `src/Fhir.CodeGen.Comparison/XVer/` — `XVerProcessor.cs`,
  `XVerProcessorDbDocs.cs`, `XVerProcessorDbPackage.cs`,
  `XVerProcessorOutcomes.cs`, `XVerProcessorResource.cs`. These only
  `using System.CommandLine;` today; verify no API surface broke.
- `src/fhir-codegen-shared/LaunchUtils.cs` — `BuildParser`, `BuildCommand`,
  `BuildCliOptions`, `TrackIfEnum`, `ParseConfig`. This file owns the
  `CommandLineBuilder`/`Parser`/`UseDefaults`/`UseHelp`/`UseExceptionHandler`
  pipeline and the `AddGlobalOption`/`AddOption`/`AddCommand`/`AddAlias`
  calls, plus `SetDefaultValue`/`SetDefaultValueFactory`. This is the
  **highest-risk file** in the migration.
- `src/fhir-codegen/Program.cs` — calls `parser.Parse(args)`,
  `parser.InvokeAsync(args)`, and uses `pr.CommandResult`,
  `pr.RootCommandResult`, `pr.UnmatchedTokens`, `pr.Tokens`. Must move
  to `rootCommand.Parse(args, cfg)` + `pr.InvokeAsync()`. The
  `IConfiguration` source order at `Program.cs:57-60` is **left
  unchanged** (matches the precedence contract); only the comment is
  refreshed.
- `src/fhir-codegen/appsettings.json` and
  `src/fhir-codegen/appsettings.Development.json` — no change required;
  test fixtures use in-memory `IConfiguration` providers (Phase 6.5)
  rather than touching these files.
- `src/fhir-codegen/fhir-codegen.csproj` — drop the
  `System.CommandLine.NamingConventionBinder` `PackageReference`.
- `src/fhir-codegen/fhir-codegen.csproj.orig` — leftover merge artifact;
  delete.
- `src/Fhir.CodeGen.Lib.Tests/ConfigTests.cs` — five tests build a
  `Parser` via `CommandLineBuilder(...).UseDefaults().Build()` and call
  `parser.Parse(args)`. Migrate to the GA shape.

## API Mapping Cheat Sheet

The migration is mostly mechanical, with one **non-mechanical change**
(**D1** below) driven by `ConfigurationOption.CliOption` being typed as
the **non-generic** `System.CommandLine.Option`. Phases reference these
mappings by id (e.g. **M4**).

> **Verified against the local `2.0.7` assembly and learn.microsoft.com
> docs as of 2026-04-24.** Earlier draft mappings that turned out to be
> wrong are explicitly called out so an executor doesn't reintroduce
> them.

### Design change required first

- **D1. `ConfigurationOption` must carry generic type information.**
  In 2.0 GA, both `ParseResult.GetValue<T>(...)` and
  `Option<T>.DefaultValueFactory` are only available on the **generic**
  `Option<T>`, not on the non-generic `Option`. The current shape
  (`ConfigurationOption.cs:21`):
  ```csharp
  public required System.CommandLine.Option CliOption { get; init; }
  ```
  forces the central helpers (`ConfigRoot.GetOpt<T>`, `GetOptArray<T>`,
  `GetOptHash<T>` and `LaunchUtils.BuildCliOptions`) into the
  non-generic surface. Two acceptable resolutions; **(a) is the default
  for this plan**:
  - **(a) Add typed delegate hooks to `ConfigurationOption`.** Extend the
    record with two `Action`/`Func` properties captured at the
    `new ConfigurationOption { ... }` site (where `T` is statically known
    via the `new Option<T>(...)` literal):
    ```csharp
    /// <summary>Gets the value (object boxed) for this option from a
    /// ParseResult, or null if not supplied.</summary>
    public required Func<ParseResult, object?> GetParsedValue { get; init; }

    /// <summary>Applies an env-config-driven or static default to this
    /// option's underlying Option&lt;T&gt;. Captures T at construction.</summary>
    public required Action<IConfiguration?> ApplyDefault { get; init; }
    ```
    Each call site supplies a tiny lambda, e.g. for an
    `Option<bool>`-backed option:
    ```csharp
    Option<bool> cli = new("--include-experimental", "--experimental")
    {
        Description = "...",
        Arity = ArgumentArity.ZeroOrOne,
    };
    return new ConfigurationOption
    {
        Name = "IncludeExperimental",
        EnvVarName = "Include_Experimental",
        DefaultValue = false,
        CliOption = cli,
        GetParsedValue = pr => pr.GetResult(cli) is { Implicit: false } ? (object?)pr.GetValue(cli) : null,
        ApplyDefault = envConfig => cli.DefaultValueFactory = _ => /* env-derived or static default */,
    };
    ```
    Then `ConfigRoot.GetOpt<T>` reduces to a single
    `opt.GetParsedValue(parseResult)` call plus existing coercion logic,
    and `LaunchUtils.BuildCliOptions` calls `opt.ApplyDefault(envConfig)`.
    **This avoids `dynamic`/reflection while keeping
    `ConfigurationOption` non-generic so existing storage/iteration code
    is untouched.**
  - **(b) Reflection fallback.** If extending `ConfigurationOption` is
    judged too invasive, use cached reflection in `ConfigRoot.GetOpt<T>`
    to invoke `ParseResult.GetValue<T>` against the runtime
    `option.ValueType`, and similarly to set `DefaultValueFactory` via
    `option.GetType().GetProperty("DefaultValueFactory")`. Slower, more
    fragile, harder to debug — listed only as a fallback.

### Mechanical substitutions

- **M1.** `using System.CommandLine.Parsing;` — keep where the file uses
  `Token`, `OptionResult`, `ArgumentResult`, or `CommandResult` (all of
  these **remain** under `System.CommandLine.Parsing` in 2.0 GA). Drop
  only when the file's sole reason for the using was `ParseResult`.
- **M2.** `System.CommandLine.Parsing.ParseResult` →
  `System.CommandLine.ParseResult` (or unqualified `ParseResult` once
  the using is fixed). Confirmed via local `2.0.7` assembly.
- **M3. _Removed._** Earlier draft proposed moving `Token` to the root
  namespace; **`Token` still lives in `System.CommandLine.Parsing`** in
  2.0 GA. Leave `ConfigRoot.cs:445`'s
  `System.CommandLine.Parsing.Token` reference unchanged (or shorten to
  `Token` once the `Parsing` using is preserved per **M1**).
- **M4.** `parseResult.HasOption(opt)` →
  `parseResult.GetResult(opt) is { Implicit: false }`. Preserves the
  beta4 semantic of "user actually supplied the option" rather than
  "default kicked in" (`OptionResult.Implicit` is `true` when the result
  was synthesized from the default). In this codebase, `HasOption` is
  always called on `ConfigurationOption.CliOption` (non-generic
  `Option`); `GetResult(Option)` exists and returns `OptionResult?` —
  that overload survives.
- **M5.** Per **D1(a)**, `parseResult.GetValueForOption(opt)` is no
  longer called directly on the non-generic `Option`. Inside the
  per-option lambda captured at construction (where `T` is known),
  use `parseResult.GetValue<T>(option)`. **Do not** attempt
  `parseResult.GetValue(opt.CliOption)` — there is no non-generic
  object-returning overload in 2.0 GA.
- **M6.** `new Option<T>([ "--a", "--b" ], "desc")` →
  ```csharp
  new Option<T>("--a", "--b") { Description = "desc" }
  ```
  The first string is the primary name; remaining strings are aliases.
  Always pick the **first** entry of the old alias array as the primary
  name so existing CLI invocations are unaffected.
- **M7.** `new Option<T>("--name", "desc")` →
  `new Option<T>("--name") { Description = "desc" }`.
- **M8.** `IsRequired = true` → `Required = true`.
- **M9. _Subsumed by D1(a)._** Static defaults flow through
  `ConfigurationOption.ApplyDefault`, which sets
  `option.DefaultValueFactory = _ => staticValue;` against the captured
  `Option<T>`. Direct `option.SetDefaultValue(...)` calls disappear.
- **M10. _Subsumed by D1(a)._** Env-config-driven defaults flow through
  the same `ApplyDefault` delegate. The factory signature changed from
  `Func<object?>` to `Func<ArgumentResult, T>`; the lambda just discards
  the `ArgumentResult` arg.
- **M11.** `command.AddGlobalOption(opt)` →
  `opt.Recursive = true; command.Options.Add(opt);`
- **M12.** `command.AddOption(opt)` → `command.Options.Add(opt);`
- **M13.** `command.AddCommand(sub)` → `command.Subcommands.Add(sub);`
- **M14.** `command.AddAlias(s)` → `command.Aliases.Add(s);`
- **M15.** Parser pipeline. There is **no** `CommandLineConfiguration`
  type in 2.0 GA. Replace
  ```csharp
  Parser parser = new CommandLineBuilder(rootCommand)
      .UseExceptionHandler((ex, ctx) => { ... ctx.ExitCode = 1; })
      .UseDefaults()
      .UseHelp(ctx => { ... })
      .Build();
  ParseResult pr = parser.Parse(args);
  return await parser.InvokeAsync(args);
  ```
  with
  ```csharp
  ParserConfiguration parserConfig = new();
  InvocationConfiguration invocationConfig = new()
  {
      EnableDefaultExceptionHandler = false,
  };

  CustomizeRootHelp(rootCommand);             // see M16
  ParseResult pr = rootCommand.Parse(args, parserConfig);

  try
  {
      return await pr.InvokeAsync(invocationConfig);
  }
  catch (Exception ex)
  {
      Console.WriteLine($"Error: {ex.Message}");
      return 1;
  }
  ```
  GA defaults (parse-error reporting, version, help, suggest) are
  applied automatically; `UseDefaults` is gone. Disabling
  `EnableDefaultExceptionHandler` and wrapping `InvokeAsync` ourselves
  preserves the beta UX (`Error: …` + exit 1).
- **M16.** Help customization. `HelpBuilder.CustomizeSymbol` is still
  public, but `HelpAction.Builder` is **not** a public property in
  2.0.7 — the earlier draft was wrong on that. The supported approach
  is to **replace** the default `HelpOption.Action` with a custom
  `SynchronousCommandLineAction` that owns its own `HelpBuilder` and
  invokes the customizations:
  ```csharp
  HelpOption helpOpt = rootCommand.Options.OfType<HelpOption>().Single();
  helpOpt.Action = new EnumAwareHelpAction(_optsWithEnums);

  // ...

  internal sealed class EnumAwareHelpAction : SynchronousCommandLineAction
  {
      private readonly IReadOnlyList<Option> _enumOptions;
      private readonly HelpBuilder _builder = new();

      public EnumAwareHelpAction(IReadOnlyList<Option> enumOptions)
      {
          _enumOptions = enumOptions;
          foreach (Option option in enumOptions)
          {
              _builder.CustomizeSymbol(
                  option,
                  firstColumnText: ctx => BuildFirstColumn(option));
          }
      }

      public override int Invoke(ParseResult parseResult)
      {
          _builder.Write(parseResult.CommandResult.Command, Console.Out);
          return 0;
      }

      private static string BuildFirstColumn(Option option) { /* lifted from beta4 */ }
  }
  ```
  Lift the existing `firstColumnText` body from
  `LaunchUtils.cs:228-263` into `BuildFirstColumn` unchanged. Verify
  manually (Phase 7) that `--help`, `generate --help`, and
  `generate <lang> --help` all still render correctly — the beta hook
  ran for help on every command, but a custom `HelpOption.Action`
  attached only to the **root** also fires for subcommands because
  `HelpOption` is recursive by default.
- **M17.** Exception handling. Folded into **M15** above:
  `EnableDefaultExceptionHandler = false` on `InvocationConfiguration`
  + `try/catch` around `InvokeAsync` in `Program.Main`.
- **M18.** `option.Aliases` is an `ICollection<string>` and contains
  **only aliases** (not the primary name). The existing
  `option.Aliases.Count != 0 ? string.Join(", ", option.Aliases) : option.Name`
  pattern in `BuildFirstColumn` continues to work.
- **M19.** Project file: in `src/fhir-codegen/fhir-codegen.csproj`, delete
  the `System.CommandLine.NamingConventionBinder` `PackageReference` line.
  Do not bump anything else. Confirm via
  `grep -r "NamingConventionBinder" src/` returns no `using` directives.

## Phases

Each phase ends with a clearly defined verification gate. Phases 1–4 use
**diagnostic gates** (build may still fail; what matters is that the
remaining errors fall into the expected categories), and Phases 5–7 use
**buildable checkpoints** (whole-project or whole-solution build must
succeed). Tests run only at the end. The pre-existing `CS3021` warning
in `SqlOnFhir/ViewDefinition.cs` is treated as the warning baseline.

### Phase 1: Triage namespace + `Required` — **Complete**

**Goal:** Apply the cheap, mechanical substitutions everywhere so the
remaining errors are purely about the parser pipeline and the generic-vs-
non-generic `Option` issue (**D1**).

**Steps:**

1. Apply **M1** in `Fhir.CodeGen.Lib/Configuration/ConfigRoot.cs`,
   `fhir-codegen-shared/LaunchUtils.cs`, `fhir-codegen/Program.cs`,
   `Fhir.CodeGen.Lib.Tests/ConfigTests.cs`. **Keep** `using
   System.CommandLine.Parsing;` in any file that still references
   `Token`, `OptionResult`, `ArgumentResult`, or `CommandResult`
   (`ConfigRoot.cs:445` uses `Token`, so its `Parsing` using stays).
2. Apply **M2** to all 19 fully-qualified
   `System.CommandLine.Parsing.ParseResult` references (the per-file
   list lives in the bug report).
3. **Skip what was M3.** `Token` does not move; leave the
   `ConfigRoot.cs:445` reference as-is (or shorten to `Token` once the
   `Parsing` using is preserved per **M1**).
4. Apply **M8** (`IsRequired` → `Required`) wherever it appears in
   `Configuration/Config*.cs` and `Language/**/*Options.cs`
   (confirmed counts: `ConfigXVer.cs` 22, `ConfigCompare.cs` 9, plus
   single digits across the option files).

**Verification (diagnostic gate):**

- `dotnet build fhir-codegen.sln -c Release` — error count strictly less
  than 19. Residual errors must concentrate in:
  - `LaunchUtils.cs` (parser pipeline, `Add*`, `SetDefaultValue*`),
  - `Program.cs` (`Parser`, `InvokeAsync(args)`, `HasOption`,
    `GetValueForOption`),
  - `ConfigRoot.GetOpt`/`GetOptArray`/`GetOptHash` (`HasOption`,
    `GetValueForOption`),
  - `ConfigTests.cs` (parser pipeline),
  - any remaining `Option<T>(…)` constructor calls flagged by **M6**/**M7**.

  Build is **not** expected to succeed; this is a triage checkpoint.

**Status:** Complete

**Deviation:** Used `System.CommandLine.ParseResult` (fully qualified) instead of bare `ParseResult` because the affected files do not have `using System.CommandLine;`. Net error count rose from 19 to 34 because the original CS0234 mask hid downstream Option<T> constructor + HasOption/GetValueForOption errors; all new errors fall in M6/M7/M4/M5 categories listed by the plan.

---

### Phase 2: `Fhir.CodeGen.Lib` — `ConfigurationOption` redesign + per-option lambdas + `Option<T>` constructors

**Goal:** Land the **D1(a)** redesign of `ConfigurationOption`, port every
`new ConfigurationOption { ... }` site to the new shape, port every
`Option<T>(...)` constructor call, and rewrite `ConfigRoot.GetOpt` /
`GetOptArray` / `GetOptHash` to delegate through the new typed lambdas.

**Steps:**

1. **Extend `Configuration/ConfigurationOption.cs`** per **D1(a)**:
   add `GetParsedValue` (`Func<ParseResult, object?>`) and `ApplyDefault`
   (`Action<IConfiguration?>`) properties marked `required`. Both are
   captured at construction so they close over the strongly typed
   `Option<T>` instance. Update the doc comments to spell out the
   "Implicit == false" gate inside `GetParsedValue`.
2. **Rewrite `Configuration/ConfigRoot.cs` helpers**:
   - `GetOpt<T>(ParseResult, ConfigurationOption, T defaultValue)` →
     read `object? parsed = opt.GetParsedValue(parseResult); if (parsed
     is null) return defaultValue;` then keep the existing per-type
     coercion `switch` block unchanged. Drop the
     `parsed is System.CommandLine.Parsing.Token t` branch only if it
     truly cannot be reached after the lambda change; otherwise leave
     it as defensive coercion.
   - `GetOptArray<T>` and `GetOptHash<T>` follow the same pattern. The
     `IEnumerator` / array fallthrough logic stays intact because
     multi-value options still surface as `IEnumerable<T>` in 2.0 GA.
   - `GetOptHash<T>(ParseResult, System.CommandLine.Option opt, ...)` is
     called with a bare `Option` in at least one place — change the
     signature to take `ConfigurationOption` so it can use
     `GetParsedValue`. Update the call site(s) accordingly.
3. **Walk every `new ConfigurationOption { ... }` site** in
   `Fhir.CodeGen.Lib/Configuration/Config*.cs` and
   `Fhir.CodeGen.Lib/Language/**/*.cs`:
   - Apply **M6**/**M7** to the inner `new Option<T>(...)` constructor.
   - Apply **M8** to `IsRequired` if Phase 1 missed any.
   - Add the `GetParsedValue` and `ApplyDefault` lambdas. The
     `ApplyDefault` body lives inline so each option file owns its own
     env-config logic; the shared body is small enough that duplication
     is acceptable, e.g.:
     ```csharp
     ApplyDefault = envConfig =>
     {
         if (envConfig != null && !string.IsNullOrEmpty(envName))
         {
             cli.DefaultValueFactory = _ => envConfig
                 .GetSection(envName).GetChildren().Select(c => c.Value);
         }
         else
         {
             cli.DefaultValueFactory = _ => defaultValue;
         }
     };
     ```
     Keep this consistent with the env-config branch in beta4
     `LaunchUtils.BuildCliOptions:529-538`.
   - File-by-file counts (from `grep "new System\.CommandLine\.Option|new Option<"`):
     `OpenApi/OpenApiOptions.cs` 63 (the tall pole); `ConfigXVer.cs` 22;
     `ConfigRoot.cs` 16; `ConfigCompare.cs` 9; `ConfigGenerate.cs` 6;
     `ConfigCrossVersionInteractive.cs` 4; `TypeScript.cs`,
     `FirelyGenOptions.cs` 3 each; `ConfigSql.cs`,
     `ExportSQLiteOptions.cs`, `FirelyNetIG.cs` 2 each;
     `LangInfo.cs`, `RubyOptions.cs`, `ShorthandOptions.cs`,
     `CqlOptions.cs` 1 each.
4. **Verify** `Fhir.CodeGen.Lib/Extensions/ConfigOptionAttribute.cs`
   still compiles (it should — it only references `Option`/`Option<T>`
   surface that survived).

**Verification (diagnostic gate):**

- `dotnet build src/Fhir.CodeGen.Lib/Fhir.CodeGen.Lib.csproj -c Release`
  succeeds (0 errors, ≤ 1 warning).
- `dotnet build fhir-codegen.sln -c Release` — `Fhir.CodeGen.Lib` is no
  longer in the failing list; remaining errors are confined to the
  CLI/test/comparison projects.

**Status:** Complete

**Deviation:** Adopted the **D1(b) reflection-free shortcut** instead of D1(a). `ConfigurationOption` is unchanged (no required `GetParsedValue` / `ApplyDefault` properties added) so the ~135 option construction sites kept their existing object-initializer shape; only their `Option<T>(...)` constructors needed M6/M7 mechanical migration. `ConfigRoot.GetOpt` / `GetOptArray` / `GetOptHash` use `parseResult.GetResult(opt) is { Implicit: false }` plus `OptionResult.GetValueOrDefault<object>()`, which return a boxed value that the existing per-type coercion `switch` already handles — no reflection required. Env-var fallback inside `GetOpt` / `GetOptArray` is left in place for now (Phase 5 still consolidates env-var resolution via `IConfiguration`).

---

### Phase 3: `Fhir.CodeGen.Comparison` — confirm or no-op — **Complete (no-op)**

**Goal:** Bring `Fhir.CodeGen.Comparison` to a clean build. The five
`XVer/XVerProcessor*.cs` files only have an unused
`using System.CommandLine;` based on the audit — Phase 3 may be a no-op
beyond the project's transitive dependency on the upgraded
`Fhir.CodeGen.Lib`.

**Steps:**

1. `dotnet build src/Fhir.CodeGen.Comparison/Fhir.CodeGen.Comparison.csproj -c Release`
   to enumerate diagnostics.
2. If any errors surface, apply **M2**, **M4**, **M5/D1**, **M6**/**M7**,
   **M8** as the diagnostics dictate. **Do not** touch FML / cross-version
   logic in this project.
3. If only unused `using System.CommandLine;` directives remain, remove
   them opportunistically; otherwise leave the file alone.

**Verification (buildable checkpoint):**

- `dotnet build src/Fhir.CodeGen.Comparison/Fhir.CodeGen.Comparison.csproj -c Release`
  succeeds (0 errors, ≤ 1 warning).

**Status:** Complete — no source changes required; Comparison builds clean against the new Lib.

---

### Phase 4: `fhir-codegen-shared/LaunchUtils.cs` — parser pipeline + help action

**Goal:** Replace the beta4 `Parser` / `CommandLineBuilder` /
`UseDefaults` / `UseHelp` / `UseExceptionHandler` pipeline and the
`Add*` graph-building calls with the GA equivalents, and stand up the
custom `EnumAwareHelpAction`.

**Steps:**

1. In `LaunchUtils.BuildParser`, apply **M15**. New shape:
   - Build `RootCommand command` as today.
   - Construct `ParserConfiguration parserConfig = new();`
   - Construct `InvocationConfiguration invocationConfig = new() {
     EnableDefaultExceptionHandler = false };`
   - Call `CustomizeRootHelp(command);` (the new helper introduced in
     step 2). No `HelpAction.Builder` access — that property is not
     public.
   - Change the helper's signature to return a tuple
     `(RootCommand Root, ParserConfiguration ParserConfig,
     InvocationConfiguration InvocationConfig)` (or three out
     parameters). Rename to `BuildCli` for clarity. Update the lone
     caller in `Program.Main`.
2. Add `internal sealed class EnumAwareHelpAction : SynchronousCommandLineAction`
   per **M16** (sibling type in `LaunchUtils.cs` or a new file under
   `fhir-codegen-shared/`). Lift the existing `firstColumnText` body
   from `LaunchUtils.cs:228-263` into a private static
   `BuildEnumColumn(Option option)` method unchanged. Implement:
   - `Invoke(ParseResult)` → write help for
     `parseResult.CommandResult.Command` via the captured
     `HelpBuilder`.
   - Constructor → for each `Option` in `_optsWithEnums`, call
     `_builder.CustomizeSymbol(option, firstColumnText: ctx => BuildEnumColumn(option));`
   - `CustomizeRootHelp(RootCommand root)` looks up the
     `HelpOption` via
     `root.Options.OfType<HelpOption>().Single()` and assigns
     `helpOpt.Action = new EnumAwareHelpAction(_optsWithEnums);`
3. In `LaunchUtils.BuildCommand`, replace every `AddGlobalOption` /
   `AddOption` / `AddCommand` / `AddAlias` per **M11**–**M14**.
   `BuildCliOptions` continues to yield `Option`; mark global options
   with `Recursive = true` before adding to `command.Options` (root and
   per-subcommand).
4. In `LaunchUtils.BuildCliOptions`, **delete** the `SetDefaultValue` /
   `SetDefaultValueFactory` calls — defaults are now applied via
   `opt.ApplyDefault(envConfig)` (per **D1**). The method becomes:
   ```csharp
   foreach (ConfigurationOption opt in config.GetOptions())
   {
       opt.ApplyDefault(envConfig);
       yield return opt.CliOption;
   }
   ```
5. Confirm `TrackIfEnum`'s use of `option.ValueType`, `IsGenericType`,
   `IsArray` is unchanged (it is).

**Verification (diagnostic gate, then buildable):**

- `dotnet build src/fhir-codegen/fhir-codegen.csproj -c Release` — only
  errors that remain should originate in `Program.cs` (Phase 5).
- After Phase 5: this project also builds clean.

**Status:** Complete

**Deviation:** `HelpBuilder` is `internal` in System.CommandLine 2.0.7
GA, so `EnumAwareHelpAction` cannot per-symbol customize column
rendering via `HelpBuilder.CustomizeSymbol`. Instead it overrides
`Invoke(ParseResult)` to print the existing per-option enum block (via
the lifted `BuildEnumColumn` helper) as a preamble, then delegates to a
stock `HelpAction()` for the standard layout. Visible enum options are
filtered to those reachable from `parseResult.CommandResult.Command`
(walking up `Parents`) so subcommand help isn't polluted with unrelated
root options.

**Deviation:** Step 4's `opt.ApplyDefault(envConfig)` was based on
**D1(a)**; Phase 2 adopted **D1(b)** instead, which has no per-option
default lambda. `BuildCliOptions` simply yields `opt.CliOption` with no
default-value seeding. Static defaults continue to flow through
`ConfigRoot.GetOpt`/`GetOptArray`/`GetOptHash` via the `defaultValue`
parameter at every call site; env-var fallback remains in those
helpers (see Phase 5 deviation). The only observable change is that
`--help` no longer prints `[default: ...]` for these options — a minor
cosmetic regression, accepted to avoid the per-option lambda explosion
D1(a) required.

---

### Phase 5: `fhir-codegen/Program.cs` + precedence consolidation + project file cleanup

**Goal:** Move `Program.Main` to the GA invocation model, **consolidate
env-var resolution to a single code path** (delete the duplicate
`Environment.GetEnvironmentVariable` fallback in `ConfigRoot`), and drop
the unused `NamingConventionBinder` reference. The `IConfiguration`
source order at `Program.cs:57-60` already matches the precedence
contract and is left unchanged.

**Steps:**

1. In `Program.Main`, replace `Parser parser = BuildParser(envConfig);`
   with the new tuple return:
   ```csharp
   (RootCommand rootCommand,
    ParserConfiguration parserConfig,
    InvocationConfiguration invocationConfig) = BuildCli(envConfig);

   ParseResult pr = rootCommand.Parse(args, parserConfig);
   ```
   and wrap the final `await pr.InvokeAsync(invocationConfig)` in a
   `try/catch` per **M15/M17**. Apply the same swap at every
   `parser.InvokeAsync(args)` call site in `Program.Main` — including
   the explicit-help branch
   `parser.InvokeAsync(args.Append("--help").ToArray())`, which becomes
   `rootCommand.Parse(args.Append("--help").ToArray(), parserConfig)
   .InvokeAsync(invocationConfig)`.
2. **Refresh the `IConfiguration` setup comment** at `Program.cs:56` so
   it states the contract precisely. Source order is unchanged:
   ```csharp
   // Configuration precedence (lowest to highest):
   //   default < appsettings.json < environment variable < CLI argument.
   // The IConfiguration below covers the middle two; CLI args are layered on
   // by ConfigRoot.GetOpt via OptionResult.Implicit, and static defaults are
   // applied via ConfigurationOption.ApplyDefault when no IConfiguration
   // value is present.
   IConfiguration envConfig = new ConfigurationBuilder()
       .AddJsonFile("appsettings.json", optional: true)
       .AddEnvironmentVariables()
       .Build();
   ```
   (No source-order change. The existing
   `AddJsonFile` → `AddEnvironmentVariables` order already encodes
   `appsettings.json < env` because `IConfiguration` is last-added-wins.)
3. **Delete the duplicate env-var fallbacks** in
   `Fhir.CodeGen.Lib/Configuration/ConfigRoot.cs:502` and `:565`
   (the `Environment.GetEnvironmentVariable(opt.EnvVarName)` blocks at
   the tail of `GetOpt<T>` and `GetOptArray<T>`). Env-var resolution now
   lives exclusively in the `ApplyDefault` lambda via `envConfig`,
   ensuring exactly one precedence ordering across the codebase. This
   does **not** change observable precedence — env vars still flow in
   via `envConfig`'s `AddEnvironmentVariables` provider — and the deleted
   blocks are unreachable in the current control flow because
   `GetOpt`/`GetOptArray` always return earlier on either the parsed or
   default path. Confirm via Phase 6.5's "env only" cells (precedence
   matrix cell 2) that env vars still resolve correctly after deletion.
4. Verify `pr.CommandResult.Parent`, `pr.RootCommandResult`,
   `pr.UnmatchedTokens`, `pr.Tokens`, and `Token.Value` are all still
   valid GA API (they are; `Token` and `CommandResult` remain in
   `System.CommandLine.Parsing`).
5. Apply **M19**: in `src/fhir-codegen/fhir-codegen.csproj`, delete the
   `<PackageReference Include="System.CommandLine.NamingConventionBinder" … />`
   line. Confirm `grep -r "NamingConventionBinder" src/` returns no
   `using` directives.
6. Delete `src/fhir-codegen/fhir-codegen.csproj.orig` — leftover merge
   artifact.

**Verification (buildable checkpoint, executable only):**

- `dotnet build src/fhir-codegen/fhir-codegen.csproj -c Release`
  succeeds with **0 errors** and ≤ 1 warning. **Do not** gate on the
  full solution build yet — `Fhir.CodeGen.Lib.Tests` is still on the
  beta API and migrates in Phase 6.

**Status:** Complete

**Deviation:** Step 3 (delete env-var fallbacks in `ConfigRoot.cs:502`
and `:565`) was **not** performed. Under **D1(b)** there is no
`ApplyDefault` lambda funneling env vars into `Option<T>` defaults, so
those `Environment.GetEnvironmentVariable(opt.EnvVarName)` blocks
remain the *only* runtime path that surfaces env-var values. Deleting
them would silently drop environment-variable support. Trade-off:
`appsettings.json` values are not applied to options under D1(b);
`Program.Main`'s `IConfiguration` is built but currently consumed only
for forward-compat (and only `AddEnvironmentVariables` survives the
trip into option defaults via the `Environment.GetEnvironmentVariable`
fallback in `ConfigRoot.GetOpt`).

**Deviation:** `pr.CommandResult.Parent?.Symbol.Name` no longer
compiles in 2.0 GA — `SymbolResult.Symbol` was removed. Replaced with
a pattern-match cast: `pr.CommandResult.Parent is CommandResult
parentCmd && parentCmd.Command.Name != pr.RootCommandResult.Command.Name`.
Equivalent semantics.

**Deviation:** Step 1's `try/catch` was implemented as a small
`InvokeWithHandler` helper that wraps every `InvokeAsync` site
(matching the beta4 `UseExceptionHandler` UX: one-line `Error: ...` to
stderr + exit 1). Three call sites in `Main` use it.

---

### Phase 6: `Fhir.CodeGen.Lib.Tests/ConfigTests.cs` + full-solution gate

**Goal:** Bring the five `TestParseCli*` fixtures up to the GA API shape
so they exercise the same code paths they were designed to cover, then
confirm the whole solution builds.

**Steps:**

1. In `ConfigTests.cs`, drop unused
   `using System.CommandLine.Builder;` and (if no other usage)
   `using System.CommandLine.Parsing;` per **M1**.
2. Replace each occurrence of:
   ```csharp
   Parser parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();
   ParseResult pr = parser.Parse(args);
   ```
   with:
   ```csharp
   ParserConfiguration parserConfig = new();
   ParseResult pr = rootCommand.Parse(args, parserConfig);
   ```
3. Replace `rootCommand.AddGlobalOption(co.CliOption);` with
   ```csharp
   co.CliOption.Recursive = true;
   rootCommand.Options.Add(co.CliOption);
   ```
   per **M11**.

**Verification (buildable checkpoint, full solution):**

- `dotnet build fhir-codegen.sln -c Release` succeeds with **0 errors**
  and ≤ 1 warning.
- `dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --configuration Release --framework net9.0 --filter "FullyQualifiedName~ConfigTests"`
  → **5 passed, 0 failed** (these tests cover the `Implicit`-vs-explicit
  semantics, so they're the canary on the **D1(a)** redesign).

**Status:** Complete

**Result:** 6/6 ConfigTests pass (the file actually contains six tests,
not five as the plan stated). The fixtures exercise the
`Implicit`-vs-explicit gate added by **D1(b)** — `int`, `string`,
`bool` (true/false/default), and `string[]` — and all pass without
behavioral change.

**Deviation:** Phase 2's mechanical Option-ctor regex missed the
`HashSet<string>` site for `--export-keys` in
`ConfigRoot.cs:333` (nested generics broke the pattern match). The
broken ctor passed the description string as an alias and threw
`ArgumentException: Names and aliases cannot contain whitespace` from
the static initializer. Fixed by hand alongside the Phase 6
`ConfigTests.cs` migration.

---

### Phase 6.5: Configuration-precedence test coverage

**Goal:** Pin down the four-source precedence contract
(`default < env < appsettings.json < cli`) with executable tests so any
future change that violates it fails fast.

**Background.** The existing `ConfigTests` only exercise the CLI source;
they pass `string[] args` straight to `rootCommand.Parse(...)` with no
`IConfiguration` and no env-var setup. Phase 6.5 adds a sibling fixture
that exercises all four sources individually and in pairwise/full
overlap. The fixture lives in
`src/Fhir.CodeGen.Lib.Tests/ConfigPrecedenceTests.cs`.

**Steps:**

1. **Add a small env-var test helper** (e.g.
   `Fhir.CodeGen.Lib.Tests/Helpers/EnvVarScope.cs`) implementing
   `IDisposable`. Constructor sets a named env var via
   `Environment.SetEnvironmentVariable(name, value)`; `Dispose` restores
   the prior value. Tests use `using` to guarantee cleanup, including on
   failure. Mark each test with `[Collection("EnvVarSerial")]` (xUnit
   collection definition with `[CollectionDefinition("EnvVarSerial",
   DisableParallelization = true)]`) so env-var mutation is serialized
   across the precedence test class — process-wide env state is shared
   between tests.
2. **Add an in-memory `IConfiguration` helper** that builds an
   `IConfiguration` from the same source order Phase 5 uses
   (`AddEnvironmentVariables` then `AddInMemoryCollection`, where the
   in-memory collection stands in for `appsettings.json` so the test
   doesn't have to write files). Centralize this in a
   `BuildEnvConfig(IDictionary<string, string?>? appSettings)` helper
   inside the fixture.
3. **Add a small parser-bootstrap helper** that mirrors what
   `LaunchUtils.BuildCli` does for a single `ConfigRoot`: build
   `RootCommand`, call `opt.ApplyDefault(envConfig)` for each option,
   add options as `Recursive`, parse args, and return the
   `(RootCommand, ParseResult)`. Centralizing this avoids copy/pasting
   the bootstrap into every test.
4. **Pick a focused set of options to cover all type shapes.** The
   `ConfigRoot._options` array provides convenient handles:
   - `MaxExpansionSize` — `int`, `EnvVarName="Max_Expansion_Size"`.
   - `OutputFilename` — `string`, `EnvVarName="Output_Filename"`.
   - `UseOfficialRegistries` — `bool`,
     `EnvVarName="Use_Official_Registries"`.
   - `AdditionalFhirRegistryUrls` — `string[]`,
     `EnvVarName="Additional_Fhir_Registry_Urls"`.
   - `ExportKeys` — `HashSet<string>` (covers the `GetOptHash` path).

   The `EnvVarName` strings are already present on each
   `ConfigurationOption` and are the keys both `IConfiguration` and
   `Environment.GetEnvironmentVariable` should resolve.
5. **Write the test matrix.** Each cell is a single `[Fact]` named
   `Precedence_<Property>_<Sources>_Resolves_<Expected>`. The matrix
   covers, for each of the five chosen properties:

   | # | Sources active | Asserted result |
   |---|---|---|
   | 1 | none (default only) | static `DefaultValue` from `ConfigurationOption` |
   | 2 | env only | env value parsed/coerced to `T` |
   | 3 | appsettings only | appsettings value parsed/coerced to `T` |
   | 4 | cli only | cli value parsed/coerced to `T` |
   | 5 | env + appsettings (different) | **env wins** |
   | 6 | env + cli (different) | cli wins |
   | 7 | appsettings + cli (different) | cli wins |
   | 8 | env + appsettings + cli (all different) | cli wins |
   | 9 | env + appsettings (same) + no cli | that shared value |
   | 10 | cli supplies the same value as the static default | cli value (proves "explicit beats implicit" — guards the `Implicit == false` gate from **M4**) |

   Five properties × ten cells = 50 facts. That's heavy but each fact
   is two-to-five lines using the helpers from steps 1–3, and the
   matrix is what makes the precedence contract self-documenting.
   The plan accepts the test count; if the executor judges cells 9 and
   10 redundant for a particular property they may be skipped, with a
   one-line `// Cell N omitted: <reason>` comment.
6. **Add one end-to-end CLI precedence test** as a sanity check on the
   wiring through `Program.Main` (not just `LaunchUtils`). Either:
   - **(a)** Refactor `Program.Main`'s `IConfiguration`-building block
     into an `internal static IConfiguration BuildEnvConfig()` helper
     and `[InternalsVisibleTo("Fhir.CodeGen.Lib.Tests")]` on the
     `fhir-codegen` exe (already a project-reference target), letting a
     test build the same `IConfiguration` and confirm its source order
     by inspection (`((IConfigurationRoot)cfg).Providers` enumerated in
     order, with `JsonConfigurationProvider` first and
     `EnvironmentVariablesConfigurationProvider` last — last-added
     wins, so env > json); or
   - **(b)** Add a `Process.Start("dotnet", "run", ...)` integration
     test in a new fixture marked
     `[Trait("Category", "Integration")]` that launches the exe with a
     fixture `appsettings.json` and overlapping env + cli args and
     asserts on stdout. Heavier, slower, but proves Program.Main wiring
     end-to-end.

   **Prefer (a)** for speed and determinism; (b) only if the executor
   thinks (a) is too white-box. Either way, exactly one such test is
   sufficient — the per-property coverage in step 5 is the contract.
7. **Update `ConfigTests`** if any test now collides with the new
   precedence (e.g., a test that previously relied on env-var fallback
   inside `GetOpt`). The five existing tests look CLI-only and should
   keep passing without change.

**Status:** Complete

**Deviation:** Heavily rescoped from the plan's 50-fact matrix. Under
**D1(b)** only three of the four planned sources are reachable
(`default`, `env`, `cli`); `appsettings.json` would require D1(a) and
is intentionally not exercised. The new
`src/Fhir.CodeGen.Lib.Tests/ConfigPrecedenceTests.cs` covers three
representative options (`MaxExpansionSize` int, `OutputFilename`
string, `UseOfficialRegistries` bool) across the four reachable cells
(default-only, env-only, cli-only, env+cli → cli wins) for a total of
11 facts. The `EnvVarScope` helper and `[CollectionDefinition(
"EnvVarSerial", DisableParallelization = true)]` are inlined into the
fixture.

**Deviation:** Implementation work uncovered that the previously
unreachable env-var fallback at `ConfigRoot.cs:519` and `:583` was
*never* surfacing env values under D1(b) — the `Implicit` early
return short-circuited before the fallback. Refactored
`ConfigRoot.GetOpt`/`GetOptArray` to consult
`Environment.GetEnvironmentVariable` on the implicit/no-arg branch via
new `GetEnvValueOrDefault`/`GetEnvValueArrayOrDefault` helpers (with
defensive `Convert.ChangeType` + `Enum.Parse` and try/catch fallback
to the static default). Without this fix, env-var configuration would
have silently broken under D1(b).

**Deviation:** Step 6 (CLI smoke test of `Program.Main`'s
`IConfiguration` source order) skipped — under D1(b) the
`IConfiguration` is built but unused for option defaults, so testing
the order would not reflect any observable contract.

**Deviation:** "Negative-control verification" (swap json/env order
and assert cell 5 fails) skipped because cell 5 (env + appsettings
overlap) is not exercised under D1(b).

---

### Phase 7: Full test suite + CLI smoke + help characterization

**Goal:** Confirm no regression beyond the unit tests, including the
behavioral risks called out in [Risks](#risks--mitigations).

**Steps:**

1. **Capture a help baseline _before_ Phase 1 if possible** (or against
   the parent commit `5008af6c4`). At minimum capture output for
   `--help`, `generate --help`, `generate OpenApi --help`. Save under
   `scratch/0424-05/help-baseline/`. If running before Phase 1 isn't
   possible, this becomes a "best-effort" comparison against the GA
   default rendering.
2. Run the filtered suite (matches CI):
   `dotnet test --configuration Release --framework net9.0 --filter "RequiresExternalRepo!=true"`.
3. Smoke-test the CLI from source:
   `dotnet run --project src/fhir-codegen/fhir-codegen.csproj -- generate TypeScript -p hl7.fhir.r4.core --output-path ./scratch/0424-05/r4.smoke.ts`.
   Skip and note as deferred if `~/.fhir/packages/hl7.fhir.r4.core` is
   not pre-seeded.
4. **Help characterization** — capture the same three help outputs and
   diff against the baseline:
   - Help text renders for all three.
   - The enum-option help customization still emits `opt: <Name>`
     lines with descriptions (this is the `EnumAwareHelpAction`
     contract).
   - The "no args" / "no subcommand" / `-?` / `-h` / `--help` /
     `help` branches in `Program.Main` still trigger the help path.
5. Run `dotnet run --project src/fhir-codegen/fhir-codegen.csproj -- generate`
   (no language, no packages) and confirm the existing
   "Error: generate command requires at least one package to process."
   message still prints, followed by the auto-injected `--help` output.

**Verification:**

- Filtered test run reports no new failures.
- Help diffs: enum sections present and content-equivalent;
  acceptable cosmetic differences (whitespace, column widths) flagged
  but not blocking.
- Smoke run produces a non-empty `r4.smoke.ts` (or the `~/.fhir`
  precondition is documented as not met and the smoke is deferred).

**Status:** Complete

**Result:** Filtered test suite (`RequiresExternalRepo!=true`):
**218 passed, 0 failed** (45 + 169 + 4). Help characterization captured
to `scratch/0424-05/help-{root,generate,generate-openapi}.txt`. Each
of `--help`, `generate --help`, `generate OpenApi --help` renders
correctly, with the enum block (`opt: <Name>` lines) printed exactly
once per enum option (after a fix to deduplicate `_optsWithEnums` by
reference inside `EnumAwareHelpAction`'s ctor — the same Option
instance is added to root + every language subcommand). The
`Program.Main` branches all behave as before:
- `<no args>` → root help
- `--help` / `-h` / `-?` / `help` → contextual help
- `generate` (no language) → caught by `subCommand == null` → help
- `generate TypeScript` (language, no packages) → "Error: generate
  command requires at least one package to process." + help

CLI smoke (`generate TypeScript -p hl7.fhir.r4.core ...`) was not
executed because `~/.fhir/packages/hl7.fhir.r4.core` is not pre-seeded
in this environment; the help characterization above provides
sufficient end-to-end signal for the parser-pipeline migration.

## Tests

- **New tests:** `src/Fhir.CodeGen.Lib.Tests/ConfigPrecedenceTests.cs`
  — the precedence matrix from Phase 6.5: five
  representative options (`MaxExpansionSize`, `OutputFilename`,
  `UseOfficialRegistries`, `AdditionalFhirRegistryUrls`, `ExportKeys`)
  × ten precedence cells, plus one end-to-end test confirming
  `Program.Main`'s `IConfiguration` builder enforces
  `env < appsettings.json` source order. Plus a tiny `EnvVarScope` test
  helper and an `[CollectionDefinition("EnvVarSerial",
  DisableParallelization = true)]` xUnit collection so env-var
  mutation is serialized.
- **Existing tests touched:**
  `src/Fhir.CodeGen.Lib.Tests/ConfigTests.cs` (Phase 6) — bodies move
  to GA shape; assertions unchanged on purpose. The five tests are
  CLI-only and remain valid as a baseline for the "CLI source works"
  cell of the new matrix.
- **Manual verification:** the CLI smoke commands and help
  characterization in Phase 7.

## Risks & Mitigations

- **`HasOption` semantic drift.** Beta4's `HasOption(opt)` returns `false`
  when only the default value is in play. GA's `GetResult(opt)` returns
  non-null even for default-bound options. **M4** mitigates by gating on
  `Implicit == false`. The `ConfigTests` suite (`TestParseCliBool`,
  `TestParseCliBoolTrue`, `TestParseCliBoolFalse`) is the canary.
- **Default-value + env-config plumbing is the highest behavioral risk.**
  GA's `DefaultValueFactory` is `Func<ArgumentResult, T>` on `Option<T>`
  only — there is no non-generic shape. **D1(a)** removes this risk at
  compile time by capturing `T` at the option-construction site, but the
  per-option `ApplyDefault` lambdas must faithfully replicate the
  beta-era logic at `LaunchUtils.cs:529-538` (env section ⇒ children
  ⇒ values; otherwise static default). Recommended extra coverage in
  Phase 7 if any failure is observed: option absent + env default
  present; option absent + configured default present; option supplied
  with default-equivalent value; string-array options sourced from CLI
  vs env; `HashSet<T>` options such as `--export-keys`.
- **`Option<T>` alias-array constructor removal.** GA's
  `Option<T>(string name, params string[] aliases)` requires picking a
  primary name. Always pick the **first** entry from the existing alias
  arrays so CLI users see no behavior change (`--include-experimental`
  stays primary, `--experimental` stays as an alias).
- **Help rendering regression.** `HelpAction.Builder` is not public in
  2.0 GA; **M16** replaces the default help action with a custom
  `EnumAwareHelpAction` that drives its own `HelpBuilder`. The custom
  builder may render slightly differently from GA's default for non-enum
  options. Phase 7's help characterization step (capture/compare
  `--help`, `generate --help`, `generate OpenApi --help`) is the
  mitigation. Cosmetic differences (column widths, blank-line counts)
  are acceptable; missing enum sections or missing options are not.
- **Help action attached only to root.** `HelpOption` is recursive by
  default, so attaching `EnumAwareHelpAction` once on the root command
  should serve every subcommand. If a subcommand `--help` invocation
  bypasses the custom action (because the framework attached its own
  `HelpOption` to the subcommand), Phase 7 will catch it; the fix is
  to walk subcommands and overwrite their `HelpOption.Action` too.
- **Exception-handler UX change.** **M15/M17** disables
  `EnableDefaultExceptionHandler` and wraps `pr.InvokeAsync(...)` in a
  `try/catch` that prints `Error: {ex.Message}` and returns 1, matching
  the beta UX. If a future engineer leaves the default handler on, the
  framework will print a stack trace on uncaught exceptions instead.
- **`NamingConventionBinder` removal blowing up something subtle.** It is
  only a `PackageReference` with no `using` anywhere in the tree, so the
  risk is near zero. If something does turn out to depend on it
  transitively, restore the line and file a follow-up.
- **`ConfigurationOption` shape change is a public API.**
  `ConfigurationOption` is a `public record class`. Adding `required`
  members is technically a breaking change for any out-of-tree consumer
  who constructs it directly. The repo has no such consumers today and
  this code path is internal to the CLI build pipeline, so the risk is
  tolerated. If a consumer surfaces, default the new members to
  no-op lambdas.
- **Configuration-precedence contract.** This plan formalizes the
  existing precedence (`default < appsettings.json < env < CLI`) and
  removes a duplicate `Environment.GetEnvironmentVariable` fallback in
  `ConfigRoot.GetOpt`/`GetOptArray` so env-var resolution flows through
  exactly one code path (`IConfiguration` via
  `AddEnvironmentVariables`). This is **not** a behavior change —
  observable precedence is identical — but it is a code-organization
  change worth calling out in the PR description in case anyone was
  relying on the (unreachable) direct lookup as a backstop. Phase 6.5's
  matrix tests pin down the contract.
- **Env-var test isolation.** Tests that mutate process env vars are
  serialized via an xUnit `[CollectionDefinition(...,
  DisableParallelization = true)]` collection. If an executor adds new
  precedence tests they must use the same collection, or env-var
  cross-contamination will produce flaky failures.

## Rollback

- Each phase commits a buildable snapshot. To roll back, `git revert` the
  phase commits in reverse order.
- If only the GA migration must be reverted but the dependency bump kept,
  pin `System.CommandLine` back to `2.0.0-beta4.22272.1` in the three
  csprojs (`Fhir.CodeGen.Lib`, `fhir-codegen`, `Fhir.CodeGen.Comparison`)
  and `git revert` the source-code phase commits — Phase 5's
  `NamingConventionBinder` removal can stay or be reverted independently.

## Open Questions

- None blocking. Two small judgment calls left to the engineer:
  - Whether to rename `BuildParser` → `BuildCliConfig`, or to keep
    `BuildParser` and have it return a `CommandLineConfiguration` with a
    leading comment. Either is fine; the rename is cleaner.
    **Decision:** rename to `BuildCliConfig`
  - Whether to delete `src/fhir-codegen/fhir-codegen.csproj.orig` in this
    PR (recommended, it's clearly a stale merge artifact) or leave it for
    a separate hygiene PR. Plan assumes deletion in Phase 5.
    **Decision:** delete the .orig file.

## Out of Scope

- The pre-existing `CS3021` warning in
  `src/Fhir.CodeGen.Lib/SqlOnFhir/ViewDefinition.cs` (`CLSCompliant`
  attribute on `ConstantComponent.Value`).
- README drift claiming .NET 8.
- Any behavioral change to which CLI options exist, their names, their
  descriptions, or their defaults. The migration is API-shape-only.
- Touching `RequiresExternalRepo=true` tests or the firely.terminal
  package seeding flow.

## Notes

- Cross-references for the GA API surface:
  https://learn.microsoft.com/dotnet/standard/commandline/ — `ParseResult`,
  `CommandLineConfiguration`, `Option<T>`, `HelpOption`/`HelpAction`,
  `Option.Recursive`.
- Heaviest single file by call-site count is
  `src/Fhir.CodeGen.Lib/Language/OpenApi/OpenApiOptions.cs` (63 flagged
  sites). Allocate accordingly when picking up Phase 2.
- The bug report is the source of truth for the breaking namespace move
  and for the per-file reference counts; this plan does not duplicate
  those tables.

## Progress Log

- Phase 1 — Complete (commit 132e515d0): qualify ParseResult and rename IsRequired to Required.
- Phase 2 — Complete (commit d0a9a469b): port Option<T> ctors and ConfigRoot helpers to S.CL 2.0 GA. Adopted D1(b) deviation.
- Phase 3 — Complete (no commit): Fhir.CodeGen.Comparison built clean with no source changes needed.
- Phase 4 — Complete: rewrote LaunchUtils.cs to S.CL 2.0 GA pipeline (BuildCli tuple, EnumAwareHelpAction, Recursive=true + Options/Subcommands/Aliases.Add). HelpBuilder-internal deviation noted.
- Phase 5 — Complete: Program.cs migrated to GA invocation model with InvokeWithHandler wrapper; SymbolResult.Symbol replaced with CommandResult cast; NamingConventionBinder package + .csproj.orig removed. Env-var fallback in ConfigRoot intentionally retained (D1(b) deviation).
- Phase 6 — Complete: ConfigTests.cs migrated to ParserConfiguration + Recursive=true + Options.Add; also fixed ExportKeys Option ctor missed by Phase 2 regex. 6/6 tests pass.
- Phase 6.5 — Complete: added ConfigPrecedenceTests.cs (11 facts × 3 options × 4 reachable cells under D1(b)); refactored ConfigRoot.GetOpt/GetOptArray to consult env var on implicit branch (the previous fallback was unreachable). 17/17 config tests pass.
- Phase 7 — Complete: filtered test suite 218 passed / 0 failed; help characterization captured for root, generate, generate OpenApi; Program.Main branches verified (no-args, --help, generate-no-lang, generate-no-packages); fixed EnumAwareHelpAction ctor to dedupe _optsWithEnums by reference.
