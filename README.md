# fhir-codegen

[![Docs](https://github.com/FHIR/fhir-codegen/actions/workflows/docs.yaml/badge.svg)](https://github.com/FHIR/fhir-codegen/actions/workflows/docs.yaml)

A .Net application, library, and related utilities to work with FHIR specifications.

## FHIR Foundation Project Statement
* Maintainers: Gino Canessa
* Issues / Discussion: Any issues should be submitted on [GitHub](https://github.com/FHIR/fhir-codegen/issues). Discussion can be performed here on GitHub, or on the [dotnet stream on chat.fhir.org](https://chat.fhir.org/#narrow/stream/179171-dotnet).
* License: This software is offered under the [MIT License](LICENSE).
* Contribution Policy: See [Contributing](#contributing).
* Security Information: See [Security](#security).


## Contributing

There are many ways to contribute:
* [Submit bugs](https://github.com/FHIR/fhir-codegen/issues) and help us verify fixes as they are checked in.
* Review the [source code changes](https://github.com/FHIR/fhir-codegen/pulls).
* Engage with users and developers on the [dotnet stream on FHIR Zulip](https://chat.fhir.org/#narrow/stream/179171-dotnet)
* Contribute features or bug fixes - see [Contributing](CONTRIBUTING.MD) for details.

To ensure a welcoming environment, we follow the [HL7 Code of Conduct](https://www.hl7.org/legal/code-of-conduct.cfm) and expect contributors to do the same.

# Documentation

Documentation is published at **<https://fhir.github.io/fhir-codegen/>**
and includes an introduction to the project, a guide to the supported
output languages, the cross-version mapping pipeline, the auto-generated
command-line reference, and the XMLDoc-driven API reference for the
`Fhir.CodeGen.*` library set.

The site is rebuilt from `main` on every push by
[`.github/workflows/docs.yaml`](.github/workflows/docs.yaml). Local docs
authoring lives under [`docs/`](docs/); the build configuration lives
under [`docfx/`](docfx/).


# Trademarks

FHIR&reg; is the registered trademark of HL7 and is used with the permission of HL7. 
