
### CURRENT

* Migrated from Microsoft/fhir-codegen to FHIR/fhir-codegen.
* Documentation site restored at https://fhir.github.io/fhir-codegen/.
* FHIR package management (download, cache, resolve, registry lookup) now comes from the `fhir-pkg-lib` NuGet package, version `2026.803.800`, consumed behind a codegen-owned seam in `Fhir.CodeGen.Lib.Packaging`.
* **Breaking:** the `Fhir.CodeGen.Packages` package is removed. Its types — `FhirCache`, `PackageManifest`, `PackageIndex`, `PackageDirective`, `FhirSemVer`, and the registry clients — no longer ship. `DefinitionCollection.Manifests` and `DefinitionCollection.ContentListings` are re-keyed from `(string id, FhirSemVer version)` onto `Fhir.CodeGen.Lib.Packaging.PackageIdentity`, and their values are now `CodeGenPackageManifest` and `CodeGenPackageIndex`. Downstream NuGet consumers of `Fhir.CodeGen.*` must update. Generated output is unchanged: `PackageIdentity.ToString()` still renders `(id, version)`.

---

### 1.0.0

* Initial release
