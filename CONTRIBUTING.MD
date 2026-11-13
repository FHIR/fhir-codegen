# Contributing

This document describes guidelines for contributing to this repo.

To ensure a welcoming environment, we follow the [HL7 Code of Conduct](https://www.hl7.org/legal/code-of-conduct.cfm) and expect contributors to do the same.

Before making a contribution, please familiarize yourself with this document, as well as our [LICENSE](LICENSE) and [README](README.md).

## Building and Testing

Install the **.NET 11 SDK**. The solution targets `net11.0`, except for
`Fhir.CodeGen.SQLiteGenerator`, which targets `netstandard2.0`.
`fhir-codegen.props` explicitly sets **C# 14.0**; the SDK upgrade does
not change that setting.

Run these commands from the repository root:

```powershell
dotnet build fhir-codegen.sln -c Release
dotnet test --configuration Release --framework net11.0 --filter "RequiresExternalRepo!=true"
```

Tests use xUnit and Shouldly. Populate the FHIR package cache before
running package-dependent tests; see
[Required setup](AGENTS.md#required-setup--the-fhir-package-cache).
Keep the `RequiresExternalRepo!=true` filter to skip tests that clone
external HL7 repositories. For focused test commands, current dependency
versions, and CI settings, see [AGENTS.md](AGENTS.md).
For documentation builds, see [the docs guide](docs/README.md).


## Submitting Pull Requests

- **DO** submit all changes via pull requests (PRs). They will be reviewed and potentially be merged by maintainers after a peer review that includes at least one of the team members.
- **DO** give PRs short but descriptive names.
- **DO** write a useful but brief description of what the PR is for.
- **DO** refer to any relevant issues and use [keywords](https://help.github.com/articles/closing-issues-using-keywords/) that automatically close issues when the PR is merged.
- **DO** ensure each commit successfully builds. The entire PR must pass all checks before it will be merged.
- **DO** address PR feedback in additional commits instead of amending.
- **DO** assume that [Squash and Merge](https://blog.github.com/2016-04-01-squash-your-commits/) will be used to merge the commits unless specifically requested otherwise.
- **DO NOT** submit "work in progress" PRs. A PR should only be submitted when it is considered ready for review.
- **DO NOT** mix independent and unrelated changes in one PR.

## Coding Style

The coding style is defined in [.editorconfig](.editorconfig) and
[stylecop.json](stylecop.json). These are conventions, not build-enforced
checks: `StyleCop.Analyzers` is not referenced, and there is no separate
lint step. Contributors should follow these guidelines when making submissions.

- **DO** follow the [.editorconfig](.editorconfig) and [stylecop.json](stylecop.json) settings.

## Creating Issues

- **DO** use a descriptive title that identifies the issue or the requested feature.
- **DO** write a detailed description of the issue or the requested feature.
- **DO** provide details for issues you create:
  - Describe the expected and actual behavior.
  - Provide any relevant exception message or OperationOutcome.
- **DO** subscribe to notifications for created issues in case there are any follow-up questions.
