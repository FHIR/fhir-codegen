# Export Languages

`fhir-codegen` ships a set of language exporters that each take a
loaded FHIR package set and emit a different output format. This page
gives a tour of what's currently in the box and what each exporter is
optimized for. For the **exact** list of CLI options each exporter
accepts, see the [Command Line Usage](cli.md) page — that page is
generated from the live binary and cannot drift from what the CLI
actually parses.

## The exporter contract

Every exporter implements
[`ILanguage`](xref:Fhir.CodeGen.Lib.Language.ILanguage) and lives under
`src/Fhir.CodeGen.Lib/Language/` either as a single-file exporter
(`TypeScript.cs`) or as its own folder (`Firely/`, `OpenApi/`, etc.).
They are auto-discovered by
[`LanguageManager`](xref:Fhir.CodeGen.Lib.Language.LanguageManager) at
static-init time via reflection over `typeof(ILanguage).Assembly`, so
adding a new language is a pure code-add — there is no registry to
update. See [Extending](extending.md) for the recipe.

Each exporter exposes a nested `*Options` class (e.g.
`TypeScript.TypeScriptOptions`) that carries its language-specific
configuration. Options are surfaced to the CLI via
`[ConfigOption]` attributes plus a matching `ConfigurationOption`
static; both must stay in sync. The CLI page's per-language sections
come straight from the `ConfigurationOption` side of that pair.

## Current exporters

> All exporters target the normalized `DefinitionCollection`
> produced by `Fhir.CodeGen.Lib.Loader.PackageLoader`. That model is
> FHIR-version-agnostic, which means the same exporter can run against
> R3, R4, R4B, R5, or any IG built on top of them.

### TypeScript

Source: `src/Fhir.CodeGen.Lib/Language/TypeScript.cs`.

Emits a single TypeScript file containing interface declarations for
the resources, complex types, and (optionally) inline enums in the
loaded package. The output is FHIR+JSON shape-compatible — there is no
runtime; the file is meant to be consumed by `tsc` for static
type-checking.

This is also the exporter that backs the `@types/fhir` definitions on
DefinitelyTyped (post-processed downstream); changes to the output
shape should be coordinated with the
[fhir-dts-generator](https://github.com/Vermonster/fhir-dts-generator)
maintainers.

### Firely (`CSharpFirely2`)

Source: `src/Fhir.CodeGen.Lib/Language/Firely/`.

Emits the C# classes used by the
[Firely .NET SDK](https://fire.ly/products/firely-net-sdk/). The
exporter has a `subset` option (`all` / `common` / `main`) that splits
the output between the version-specific classes and the
`firely-net-common` package. There is also a `CSharpFirelyIG`
sub-mode that emits IG-specific additions on top of an existing SDK
build.

Changes to this exporter must be approved by the Firely SDK
maintainers, since the SDK consumes the output verbatim.

### OpenApi

Source: `src/Fhir.CodeGen.Lib/Language/OpenApi/`.

Emits an [OpenAPI](https://www.openapis.org/) definition (v2 or v3)
describing a FHIR REST API for the loaded package. Tool-chains that
consume OpenAPI vary widely in what they accept, so this exporter is
the most option-heavy of the bunch — it can include or exclude
`Bundle` / `_history` / search-by-POST endpoints, expand or collapse
schemas, control description generation, etc. See the
[Command Line Usage](cli.md) page for the full list.

### Ruby

Source: `src/Fhir.CodeGen.Lib/Language/Ruby/`.

Emits Ruby class definitions matching the loaded package shape.
Useful when you need a Ruby SDK for an IG that has no published
language binding.

### CQL

Source: `src/Fhir.CodeGen.Lib/Language/Cql/`.

Emits [Clinical Quality Language](https://cql.hl7.org/) model-info
artifacts built from the loaded package. Used by CQL tooling that
needs a model-info file describing a non-standard FHIR profile or IG.

### FHIR Shorthand

Source: `src/Fhir.CodeGen.Lib/Language/Shorthand/`.

Emits [FHIR Shorthand](https://build.fhir.org/ig/HL7/fhir-shorthand/)
(`.fsh`) for the loaded package — useful when you need a Shorthand
representation of an existing IG, for example to feed back into a
SUSHI-driven pipeline.

### SQLite

Source: `src/Fhir.CodeGen.Lib/Language/SQLite/`.

Emits a SQLite database whose schema mirrors the loaded package
structure. Backed by the `Fhir.CodeGen.LangSQLite` project; downstream
tooling that needs to query a FHIR package shape with SQL uses this.

### Info

Source: `src/Fhir.CodeGen.Lib/Language/Info/LangInfo.cs`.

Emits a single text file summarizing the loaded package: every
resource, complex type, primitive, value set, and code system, with
their elements, cardinalities, and bindings. Used both as a
human-readable inspection aid and as a fixture for regression-testing
the loader.

## Choosing an exporter

| If you need… | Use… |
|---|---|
| Static types for a TypeScript / JavaScript app | `TypeScript` |
| C# classes that round-trip via the Firely SDK | `Firely` (`CSharpFirely2`) |
| An OpenAPI spec for a FHIR REST endpoint | `OpenApi` |
| Ruby SDK shapes for a custom IG | `Ruby` |
| CQL model-info for a non-standard profile | `Cql` |
| Shorthand (`.fsh`) round-trip of an IG | `Shorthand` |
| A SQLite schema mirroring a FHIR package | `SQLite` |
| A text summary you can grep | `Info` |

## See also

- [Extending](extending.md) — recipe for adding a new exporter.
- [Command Line Usage](cli.md) — exact CLI flags and defaults for every
  exporter, generated from the live binary.
