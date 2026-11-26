# API Documentation

This section contains API documentation automatically generated from
the XMLDoc comments on the `Fhir.CodeGen.*` library set:

- `Fhir.CodeGen.Common` — shared models and POCOs.
- `Fhir.CodeGen.CrossVersionLoader` — load and reconcile artifacts
  across FHIR versions.
- `Fhir.CodeGen.MappingLanguage` — FHIR Mapping Language (FML) parser.
- `Fhir.CodeGen.LangSQLite` — SQLite export backend.
- `Fhir.CodeGen.Lib` — core engine: package acquisition → loader →
  normalized model → language exporters.
- `Fhir.CodeGen.Comparison` — package and artifact diffing.
- `Fhir.CodeGen.CrossVersionExporter` — cross-version artifact emission.

Use the navigation pane on the left to browse by namespace and type.

FHIR package acquisition is exposed through the codegen-owned
`Fhir.CodeGen.Lib.Packaging` seam over `fhir-pkg-lib`.
`Fhir.CodeGen.Packages` no longer ships.

The CLI itself (`fhir-codegen.exe`) is documented on the **Command
Line Usage** page rather than here; its internal types are
implementation detail.
