# fhir-codegen

A .NET solution for ingesting FHIR specification packages and exporting
them into other languages and formats.

fhir-codegen normalizes the definitional surface of FHIR (core and
implementation guides, R2 through R5) into a single in-memory model and
exposes that model to a pluggable set of language exporters. It ships
both as a library set and as a `System.CommandLine`-based CLI.

## What you can do with it

- Generate strongly typed code for application use:
  TypeScript, Firely C# (`CSharpFirely2`), Ruby, CQL, FHIR Shorthand.
- Generate API surfaces from a FHIR package: OpenAPI definitions.
- Generate database / lookup artifacts: SQLite schemas, the textual
  `Info` summary, and other inspection helpers.
- Compare two sets of FHIR packages and emit Markdown / JSON diffs.
- Run cross-version (`xver`) processing across R2/R3/R4/R4B/R5 to
  produce bridge ValueSets and StructureDefinition extensions.

## Solution layout

| Project | Role |
|---|---|
| `Fhir.CodeGen.Common` | Lightweight POCOs / shared models / polyfills. |
| `Fhir.CodeGen.Packages` | FHIR package cache management (download, resolve, registry lookup). |
| `Fhir.CodeGen.CrossVersionLoader` | Load and reconcile artifacts across FHIR versions. |
| `Fhir.CodeGen.MappingLanguage` | FHIR Mapping Language (FML) parser/abstractions. |
| `Fhir.CodeGen.LangSQLite` | SQLite export backend used by the `Lib` engine. |
| `Fhir.CodeGen.Lib` | Core engine: loader → normalized model → language exporters. |
| `Fhir.CodeGen.Comparison` | Package and artifact diffing. |
| `Fhir.CodeGen.CrossVersionExporter` | Produces cross-version artifacts. |
| `fhir-codegen` | The CLI (`System.CommandLine`-based). |

Internal helpers — `Fhir.CodeGen.SQLiteGenerator` and
`performance-test-cli` — are not part of the published API surface and
are excluded from the API reference on this site.

## Where to go next

- [Introduction](articles/intro.md) — what this project is and the
  history behind it.
- [Export Languages](articles/languages.md) — the current set of
  language exporters and how to think about choosing one.
- [Extending](articles/extending.md) — add a new language exporter
  (`ILanguage` + `LanguageManager` discovery + `[ConfigOption]` /
  `ConfigurationOption` pairing).
- [Command Line Usage](articles/cli.md) — every CLI subcommand and
  option, generated from the live binary on every build.
- [Cross-Version Artifacts](articles/cross-version.md) — the four-phase
  process for producing cross-version ValueSets and extensions.
- [FHIR Sanitization Utilities](articles/sanitization.md) —
  `FhirSanitizationUtils` reference for code-generator authors.
- [FHIR Package Resolution](articles/packages-resolution.md) — how
  package directives are parsed and resolved across registries.
- [API Reference](api/index.md) — XMLDoc-driven API for the
  `Fhir.CodeGen.*` library set.

## Source

- Repository: <https://github.com/FHIR/fhir-codegen>
- Issues: <https://github.com/FHIR/fhir-codegen/issues>
- Discussion: the
  [`#dotnet` stream on chat.fhir.org](https://chat.fhir.org/#narrow/stream/179171-dotnet)
- License: [MIT](https://github.com/FHIR/fhir-codegen/blob/main/LICENSE)
