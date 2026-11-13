# `docs/` — fhir-codegen documentation source

This directory is the **single source of truth** for fhir-codegen's
authored articles and specs. The API landing page is maintained separately
in `docfx/api/index.md`. The published site lives at
<https://fhir.github.io/fhir-codegen/>.

## What lives where

| Path | Purpose |
|---|---|
| `docs/index.md`                  | Site landing page. |
| `docs/toc.yml`                   | Top-level table of contents. |
| `docs/articles/`                 | Long-form, hand-authored articles. |
| `docs/specs/`                    | Internal design / process specs. |
| `docs/articles/cli.md`           | Built by `fhir-codegen docs cli`; gitignored. |
| `docfx/`                         | DocFX build configuration, API landing page, and build outputs. |
| `docfx/api/*.yml`                | Built by DocFX `metadata`; gitignored. |
| `docfx/api/index.md`             | Hand-authored API landing page; committed. |
| `docfx/_site/`                   | Built site output; not committed. |

## Authoring policy

- Top-level `docs/` is the single source of truth. Per-project `docs/`
  folders (e.g. the old `src/Fhir.CodeGen.Packages/docs/`) are retired.
  If a project needs documentation, add it under `docs/articles/`.
- `docfx/` holds build configuration (`docfx.json`, `.gitignore`),
  the hand-authored `api/index.md`, and generated build outputs.
- The CLI usage page (`articles/cli.html` on the site) is generated at
  build time from the live System.CommandLine surface by the
  `fhir-codegen docs cli` subcommand and written to
  `docs/articles/cli.md`. **Do not hand-edit or commit it.** That path is
  ignored by `docs/.gitignore`.
  It is included by the `docfx.json` content entry rooted at `docs/`.

## Building locally

Use the **.NET 11 SDK**. The projects target `net11.0`, except for the
SQLite source generator (`netstandard2.0`); C# remains set to `14.0`.

```powershell
# 1. Build the solution (needed for DocFX metadata + the CLI emitter).
dotnet build fhir-codegen.sln -c Release

# 2. Generate the live CLI page before building the site.
dotnet run --no-build --configuration Release `
    --project src\fhir-codegen\fhir-codegen.csproj -- `
    docs cli --output docs\articles\cli.md

# 3. Install DocFX (one-time per machine) and build the site.
dotnet tool install -g docfx
docfx .\docfx\docfx.json --serve
```

The CI workflow `.github/workflows/docs.yaml` selects SDK `11` and is
configured to run these steps for source, documentation, or DocFX
configuration changes in pull requests and pushes to `main`. It builds
with `--warningsAsErrors` and deploys qualifying pushes to GitHub Pages.

## Important: build order matters

DocFX generates API metadata from the projects listed in
`docfx/docfx.json`. Build the solution first to restore dependencies and
provide the compiled CLI used by `dotnet run --no-build`, then generate
`docs/articles/cli.md` before running DocFX so the CLI links resolve.
