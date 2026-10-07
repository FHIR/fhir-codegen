
### CURRENT

* Migrated from Microsoft/fhir-codegen to FHIR/fhir-codegen.
* Documentation site restored at https://fhir.github.io/fhir-codegen/.
* FHIR package management (download, cache, resolve, registry lookup) now comes from the `fhir-pkg-lib` NuGet package, version `2026.901.1609`, consumed behind a codegen-owned seam in `Fhir.CodeGen.Lib.Packaging`.
* **Breaking:** the `Fhir.CodeGen.Packages` package is removed. Its types — `FhirCache`, `PackageManifest`, `PackageIndex`, `PackageDirective`, `FhirSemVer`, and the registry clients — no longer ship. `DefinitionCollection.Manifests` and `DefinitionCollection.ContentListings` are re-keyed from `(string id, FhirSemVer version)` onto `Fhir.CodeGen.Lib.Packaging.PackageIdentity`, and their values are now `CodeGenPackageManifest` and `CodeGenPackageIndex`. Downstream NuGet consumers of `Fhir.CodeGen.*` must update. Generated output is unchanged: `PackageIdentity.ToString()` still renders `(id, version)`.
* Updated `fhir-pkg-lib` from `2026.803.800` to `2026.901.1609`.
* Updated Firely FHIR packages from `5.13.3` to `5.13.4` and `brianpos.Fhir.Base.FhirPath.Validator` from `5.12.2-rc2` to `5.13.4-rc2`.
* Updated `Microsoft.Extensions.*` and `Microsoft.Data.Sqlite` from `10.0.11` to `10.0.12`, `System.CommandLine` from `2.0.11` to `2.0.12`, and `Microsoft.OpenApi` from `1.6.29` to `1.6.31`.
* Updated `Microsoft.NET.Test.Sdk` from `18.9.0` to `18.10.0`.

---

### 1.0.0

* Initial release
