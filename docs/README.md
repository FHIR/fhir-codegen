# `docs/` — fhir-codegen documentation source

This directory is the **single source of truth** for fhir-codegen's
authored documentation. The published site lives at
<https://fhir.github.io/fhir-codegen/>.

## What lives where

| Path | Purpose |
|---|---|
| `docs/index.md`                  | Site landing page. |
| `docs/toc.yml`                   | Top-level table of contents. |
| `docs/articles/`                 | Long-form, hand-authored articles. |
| `docs/specs/`                    | Internal design / process specs. |
| `docfx/`                         | DocFX **build configuration only** (no narrative content here). |
| `docfx/cli-generated/cli.md`     | Built by `fhir-codegen docs cli`; not committed. |
| `docfx/api/`                     | Built by DocFX `metadata`; not committed. |
| `docfx/_site/`                   | Built site output; not committed. |

## Authoring policy

- Top-level `docs/` is the single source of truth. Per-project `docs/`
  folders (e.g. the old `src/Fhir.CodeGen.Packages/docs/`) are retired.
  If a project needs documentation, add it under `docs/articles/`.
- `docfx/` holds build configuration (`docfx.json`, `.gitignore`) and
  build outputs only — no narrative `.md` files.
- The CLI usage page (`articles/cli.html` on the site) is generated at
  build time from the live System.CommandLine surface by the
  `fhir-codegen docs cli` subcommand and written to
  `docfx/cli-generated/cli.md`. **Do not hand-edit it.** It is
  `.gitignore`d and surfaced into the site by a `docfx.json` content
  entry.

## Building locally

```powershell
# 1. Build the solution (needed for DocFX metadata + the CLI emitter).
dotnet build fhir-codegen.sln -c Release

# 2. Generate the live CLI page (optional — without it, articles/cli.html
#    will 404 in the local preview but the rest of the site still builds).
dotnet run --no-build --configuration Release `
    --project src/fhir-codegen/fhir-codegen.csproj -- `
    docs cli --output docfx/cli-generated/cli.md

# 3. Install DocFX (one-time per machine) and build the site.
dotnet tool install -g docfx
docfx ./docfx/docfx.json --serve
```

The CI workflow `.github/workflows/docs.yaml` runs the same three steps
on every push to `main` and deploys the result to GitHub Pages.

## Important: build order matters

`docfx` reads metadata from compiled `.dll`s. The
`dotnet build fhir-codegen.sln -c Release` step **must** run before
`docfx ./docfx/docfx.json`. If you ever rearrange the workflow, keep the
build step ahead of the `docfx` step.
