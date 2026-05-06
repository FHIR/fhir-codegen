using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Fhir.CodeGen.Packages.Converters;
using Fhir.CodeGen.Packages.Extensions;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;

namespace Fhir.CodeGen.Packages.Models;

/// <summary>
/// A package manifest as found in the package.json file in a package or as a version
/// in the 'Full' package manifest returned by registry API calls.
/// </summary>
/// <remarks>
/// Note this class is a union of the NPM specification and FHIR-specific extensions.
/// FHIR package manifest documentation is at:
///     https://confluence.hl7.org/pages/viewpage.action?pageId=35718629#NPMPackageSpecification-Packagemanifest
/// NPM package manifest documentation is at:
///     https://docs.npmjs.com/creating-a-package-json-file
///     https://docs.npmjs.com/cli/configuring-npm/package-json
/// NPM package manifest properties were based on the Verdaccio source code:
///     https://github.com/verdaccio/verdaccio/blob/1a551daa38311bf5170333e78b0502c8a48ff2ed/packages/core/types/src/manifest.ts
/// </remarks>
public record class PackageManifest
{
    public record class PeerDependencyMetadataRecord
    {
        [JsonPropertyName("optional")]
        public bool? Optional { get; init; } = null;
    }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    private string versionLiteral = null!;

    [JsonPropertyName("version")]
    public required string Version
    {
        get => versionLiteral;
        init
        {
            versionLiteral = value;
            parsedVersion = new(value);
        }
    }

    private FhirSemVer parsedVersion = null!;

    [JsonIgnore]
    public FhirSemVer ParsedVersion => parsedVersion;

    /// <summary>
    /// Directories in the package that contain specific types of files.
    /// </summary>
    /// <remarks>
    /// Key-value pairs where keys are directory types and values are directory paths.
    /// FHIR package directory keys are described at:
    ///     https://confluence.hl7.org/pages/viewpage.action?pageId=35718629#NPMPackageSpecification-Packagemanifest
    /// </remarks>
    [JsonPropertyName("directories")]
    public Dictionary<string, string?>? Directories { get; init; } = null;

    [JsonPropertyName("dist")]
    public NpmManifestDistRecord? Distribution { get; init; } = null;

    [JsonPropertyName("author")]
    [JsonConverter(typeof(NpmPersonRecordConverter))]
    public IReadOnlyList<NpmPersonRecord>? Authors { get; init; } = null;

    // No need for this currently and need to resolve differences between 'main' and 'exports'
    //[JsonPropertyName("main")]
    //public string? MainEntryPoint { get; init; } = null;

    /// <summary>
    /// SPDX-convention license name
    /// </summary>
    /// <remarks>
    /// If multiple licenses are allowed, they can be listed as an SPDX expression
    ///     e.g., `(ISC OR GPL-3.0)`
    ///     See: https://spdx.dev/specifications/
    /// If the license is not listed in the SPDX license list, it can be a link to a license file
    ///     i.e., `SEE LICENSE IN &lt;filename&gt;`
    /// If the package is not licensed, use `UNLICENSED`.
    /// </remarks>
    [JsonPropertyName("license")]
    public string? License { get; init; } = null;

    [JsonPropertyName("readme")]
    public string? Readme { get; init; } = null;

    [JsonPropertyName("readmeFilename")]
    public string? ReadmeFilename { get; init; } = null;


    [JsonPropertyName("description")]
    public string? Description { get; init; } = null;

    [JsonPropertyName("files")]
    public List<string>? Files { get; init; } = null;

    [JsonPropertyName("maintainers")]
    [JsonConverter(typeof(NpmPersonRecordConverter))]
    public IReadOnlyList<NpmPersonRecord>? Maintainers { get; init; } = null;

    [JsonPropertyName("contributors")]
    [JsonConverter(typeof(NpmPersonRecordConverter))]
    public IReadOnlyList<NpmPersonRecord>? Contributors { get; init; } = null;

    [JsonPropertyName("repository")]
    [JsonConverter(typeof(NpmRepositoryConverter))]
    public IReadOnlyList<NpmRepositoryRecord>? Repositories { get; init; } = null;

    [JsonPropertyName("homepage")]
    public string? HomepageUrl { get; init; } = null;

    [JsonPropertyName("etag")]
    public string? ETag { get; set; } = null;

    /// <summary>
    /// Other packages that the contents of this packages depend on.
    /// </summary>
    /// <remarks>
    /// Keys are package names, values are package versions
    /// </remarks>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string?>? Dependencies { get; init; } = null;

    [JsonPropertyName("peerDependencies")]
    public Dictionary<string, string>? PeerDependencies { get; init; } = null;

    [JsonPropertyName("peerDependenciesMeta")]
    public Dictionary<string, PeerDependencyMetadataRecord>? PeerDependencyMeta { get; init; } = null;

    /// <summary>
    /// Other packages necessary during development of this package.
    /// </summary>
    [JsonPropertyName("devDependencies")]
    public Dictionary<string, string>? DevDependencies { get; init; } = null;

    [JsonPropertyName("optionalDependencies")]
    public Dictionary<string, string>? OptionalDependencies { get; init; } = null;

    [JsonPropertyName("bundleDependencies")]
    public List<string>? BundleDependencies { get; init; } = null;

    [JsonPropertyName("acceptDependencies")]
    public Dictionary<string, string?>? AcceptDependencies { get; init; } = null;

    [JsonPropertyName("keywords")]
    [JsonConverter(typeof(JsonStringOrArrayConverter))]
    public IReadOnlyList<string>? Keywords { get; init; } = null;

    [JsonPropertyName("nodeVersion")]
    public string? NodeVersion { get; init; } = null;

    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; init; } = null;

    [JsonPropertyName("bugs")]
    [JsonConverter(typeof(NpmBugReportingRecordConverter))]
    public IReadOnlyList<NpmBugReportingRecord>? BugReporting { get; init; } = null;

    [JsonPropertyName("funding")]
    [JsonConverter(typeof(NpmFundingRecordConverter))]
    public IReadOnlyList<NpmFundingRecord>? Funding { get; init; } = null;

    [JsonPropertyName("engines")]
    public Dictionary<string, string>? Engines { get; init; } = null;

    [JsonPropertyName("hasInstallScript")]
    public bool? HasInstallScript { get; init; } = null;

    [JsonPropertyName("cpu")]
    public List<string>? Architectures { get; init; } = null;

    [JsonPropertyName("os")]
    public List<string>? OperatingSystems { get; init; } = null;


    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("title")]
    public string? Title { get; init; } = null;

    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("fhirVersion")]
    [JsonConverter(typeof(JsonStringOrArrayConverter))]
    public IReadOnlyList<string>? FhirVersion { get; init; } = null;

    /// <summary>
    /// Versions of the FHIR standard used in artifacts within this package.
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// Largely obsolete, and replaced by actual dependencies on the
    /// core packages.
    /// </remarks>
    [JsonPropertyName("fhirVersions")]
    [JsonConverter(typeof(JsonStringOrArrayConverter))]
    public IReadOnlyList<string>? FhirVersions { get; init; } = null;

    /// <summary>
    /// Versions of the FHIR standard used in artifacts within this package.
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// It seems this is mistakenly generated in the core packages
    /// published by HL7 and should be the same as <see cref="FhirVersions"/> above.
    /// </remarks>
    [JsonPropertyName("fhir-version-list")]
    [JsonConverter(typeof(JsonStringOrArrayConverter))]
    public IReadOnlyList<string>? FhirVersionList { get; init; } = null;

    /// <summary>Gets the FHIR versions from FhirVersions, FhirVersionList, or dependencies.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? AnyFhirVersions =>
        FhirVersions?.Count > 0
        ? FhirVersions
        : FhirVersionList?.Count > 0
            ? FhirVersionList
            : FhirVersion?.Count > 0
                ? FhirVersion
                : VersionExtensions.FhirVersionsFromPackages(Dependencies);

    /// <summary>
    /// An optional value to indicate the type of package generated by the IG build tool
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// It's fairly random, so please do NOT depend on it. Also note that the HL7 IG build tool
    /// creates template and tool packages and publishes them as a FHIR package
    /// even though they have little to do with FHIR packages.
    /// Known types are (case-insensitive):
    /// - Conformance: a set of Conformance Resources in the base package folder (/package)
    /// - IG: a FHIR implementation guide package(has an ImplementationGuide resource in /package,
    ///       along with conformance resources, and also contains example resources in
    ///       /package/example)
    /// - Core: contains the conformance related resources for the main FHIR
    ///         specification(effectively, this is a special type of "conformance" that marks it as
    ///         the core specification, which could also be inferred from it's name such as
    ///         hl7.fhir.r4.core, but other branches / ballots etc may vary, so this is simpler than
    ///         inferring from the name)
    /// - fhir.core: older format of 'core' package (deprecated)
    /// - Examples : contains the example resources found in the main FHIR specification in /package.
    /// - Group: a package that only includes (e.g.depends on) other packages (won't contain FHIR
    ///          resources directly). The versions listed in this package must include all the
    ///          versions found in the included packages. Note that this is is used for the set of
    ///          packages that represent a full core specification
    /// - Tool: A package that contains tool specific files to support specific tools (won't contain
    ///         FHIR resources or specify a FHIR version)
    /// - IG-Template: an IG template for use by IG publishing tools (won't contain FHIR resources or
    ///                specify a version)
    /// </remarks>
    [JsonPropertyName("type")]
    public string? Type { get; init; } = null;


    /// <summary>
    /// For IG packages: The canonical url of the IG (equivalent to ImplementationGuide.url).
    /// JSON: 'canonical'
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("canonical")]
    public string? CanonicalUrl { get; init; } = null;

    /// <summary>
    /// For IG packages: Where the human readable representation (e.g. IG) is published on the web.
    /// JSON: 'url'
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("url")]
    public string? WebPublicationUrl { get; init; } = null;

    /// <summary>
    /// Country code for the jurisdiction under which this package is published.
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// Formatted as an urn specifying the code system and code, e.g. "urn:iso:std:iso:3166#US".
    /// Typically from CommonJurisdictionCodes (http://hl7.org/fhir/ValueSet/jurisdiction-common) from the
    /// fhir.tx.support.* package (fhir.tx.support.r3, fhir.tx.support.r4, ...)
    /// </remarks>
    [JsonPropertyName("jurisdiction")]
    public string? Jurisdiction { get; init; } = null;

    /// <summary>The date the package was published.</summary>
    /// <remarks>
    /// FHIR-specific
    /// TODO: not documented at https://confluence.hl7.org/pages/viewpage.action?pageId=35718629#NPMPackageSpecification-Packagemanifest
    /// </remarks>
    [JsonPropertyName("date")]
    [JsonConverter(typeof(NpmDateConverter))]
    public DateTime? PublicationDate { get; init; } = null;

    /// <summary>Gets or sets the tools version.</summary>
    /// <remarks>
    /// FHIR-specific
    /// TODO: not documented at https://confluence.hl7.org/pages/viewpage.action?pageId=35718629#NPMPackageSpecification-Packagemanifest
    /// </remarks>
    [JsonPropertyName("tools-version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ToolsVersion { get; init; } = null;

    /// <summary>
    /// Flag if this version SHOULD NOT be published
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("notForPublication")]
    public bool? NotForPublication { get; init; } = null;

    /// <summary>
    /// For packages that have forced version literals (e.g., dev), the underlying version
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("original-version")]
    public string? OriginalVersion { get; init; } = null;

    /// <summary>
    /// If a package is unlisted, it should no longer be used except for
    /// backward compatible installations.
    /// The "unlisted" field is currently a string, but we expect to transform it to a boolean "true" / "false".
    /// </summary>
    /// <remarks>
    /// FHIR-specific
    /// </remarks>
    [JsonPropertyName("unlisted")]
    public string? Unlisted;

    [JsonIgnore]
    public bool? IsUnlisted => Unlisted == null ? null : Unlisted.Equals("true", StringComparison.OrdinalIgnoreCase);
}
