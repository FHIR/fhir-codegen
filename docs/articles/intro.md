# Introduction

`fhir-codegen` is a .NET solution for ingesting FHIR specification
packages and exporting them into other languages and formats.

The FHIR specification (both core and implementation guides) is itself
defined in FHIR — every resource, profile, value set, and search
parameter is delivered as a FHIR resource inside a `.tgz` package. That
makes the specification computable, but it also makes it inconvenient
for downstream consumers: a TypeScript or Ruby application doesn't want
to walk a `StructureDefinition` graph at runtime; it wants to use plain
classes / records / hashes that match the resource shape.

This project bridges that gap. It loads FHIR packages, normalizes their
definitional content into a consistent in-memory model
([`Fhir.CodeGen.Common.Models`](xref:Fhir.CodeGen.Common.Models)), and
hands the normalized model to a pluggable set of language exporters
that emit code, schemas, or summaries.

## High-level pipeline

```
                   .tgz packages from ~/.fhir
                              │
                              ▼
         Fhir.CodeGen.Packages  (cache + registry lookup)
                              │
                              ▼
            Fhir.CodeGen.Lib.Loader.PackageLoader
                              │
                              ▼
         normalized DefinitionCollection
              (Fhir.CodeGen.Common.Models)
                              │
            ┌─────────────────┼─────────────────┐
            ▼                 ▼                 ▼
        ILanguage         ILanguage         ILanguage
       (TypeScript)         (Ruby)           (Cql) ...
            │                 │                 │
            ▼                 ▼                 ▼
        files on disk (or in-memory output)
```

The same pipeline backs three top-level CLI commands:

- `generate <language>` — emit one language's output for one or more
  packages.
- `compare` — diff two sets of packages.
- `xver` — produce cross-version artifacts (ValueSets and
  StructureDefinition extensions) across FHIR R2–R5.

A fourth command, `docs cli`, generates the
[Command Line Usage](cli.md) page on this site directly from the live
parser.

## Multi-version FHIR is built in

The Firely .NET SDK ships separate `Hl7.Fhir.STU3`, `Hl7.Fhir.R4`,
`Hl7.Fhir.R4B`, and `Hl7.Fhir.R5` assemblies. Their type names collide
(every version has its own `Patient`), so this solution uses MSBuild
extern-alias targets to load them side-by-side. See the
`AddPackageAliases` target in `src/fhir-codegen/fhir-codegen.csproj`
and `src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj` for the
exact mapping.

This is the mechanism that lets `Fhir.CodeGen.CrossVersionLoader` and
`Fhir.CodeGen.Comparison` reason about R3 and R5 in the same process.

## Why C#?

When you build tooling that targets multiple language pipelines you
have to pick a host language. C# was chosen because it is performant,
cross-platform, and pleasant to work in; the toolchain (`dotnet`,
xUnit, Shouldly) is also enough to keep contributors productive without
extra glue.

## Documentation site

This documentation site is built with
[DocFX](https://dotnet.github.io/docfx/) on every push to `main` and
served from [GitHub Pages](https://pages.github.com/) at
<https://fhir.github.io/fhir-codegen/>. The full source for the site
lives under `docs/` in the repository; the
[Command Line Usage](cli.md) page is generated at build time by the
`fhir-codegen docs cli` subcommand and is the only article that is not
hand-authored.

For details on adding a new exporter, see [Extending](extending.md).
For the current set of exporters and how to choose one, see
[Export Languages](languages.md).
