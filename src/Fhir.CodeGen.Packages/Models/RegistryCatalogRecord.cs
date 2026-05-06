using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Fhir.CodeGen.Packages.Converters;

namespace Fhir.CodeGen.Packages.Models;

/// <summary>Record for a FHIR registry catalog entry.</summary>
/// <remarks>
/// This class is a union of the properties and a custom JSON converter is used to handle the differences in casing/naming.
/// Catalog search includes the root packages and FHIR-version-specific ones.
/// Standard API does not include version information, so Registry CacheDirective Information must be
/// retrieved for each package name in order to discover versions.
/// Note that the registries use different casing conventions for property names and include different properties.
/// </remarks>
/// <example>
/// Source: http://packages.fhir.org/catalog?op=find&amp;name=hl7.fhir.uv.subscriptions&amp;pkgcanonical=&amp;canonical=&amp;fhirversion
/// <code>
/// [
///     {
///         "Name": "hl7.fhir.uv.subscriptions-backport",
///         "Description": "The Subscription R5 Backport Implementation Guide enables servers running versions of FHIR earlier than R5 to implement a subset of R5 Subscriptions in a standardized way. (built Wed, Jan 11, 2023 15:34+1100+11:00)",
///         "FhirVersion": "R4B"
///     },
///     {
///         "Name": "hl7.fhir.uv.subscriptions-backport.r4",
///         "Description": "The Subscription R5 Backport Implementation Guide enables servers running versions of FHIR earlier than R5 to implement a subset of R5 Subscriptions in a standardized way. (built Wed, Jan 11, 2023 15:34+1100+11:00)",
///         "FhirVersion": "R4"
///     },
///     {
///         "Name": "hl7.fhir.uv.subscriptions-backport.r4b",
///         "Description": "The Subscription R5 Backport Implementation Guide enables servers running versions of FHIR earlier than R5 to implement a subset of R5 Subscriptions in a standardized way. (built Wed, Jan 11, 2023 15:34+1100+11:00)",
///         "FhirVersion": "R4B"
///     }
/// ]
/// </code>
///
/// Source: http://packages2.fhir.org/catalog?op=find&amp;name=hl7.fhir.uv.subscriptions&amp;pkgcanonical=&amp;canonical=&amp;fhirversion=
/// <code>
/// [
///     {
///         "name": "hl7.fhir.uv.subscriptions-backport",
///         "date": "2023-01-11T03:34:12-00:00",
///         "version": "1.1.0",
///         "fhirVersion": "R4B",
///         "count": "40",
///         "canonical": "http://hl7.org/fhir/uv/subscriptions-backport",
///         "description": "The Subscription R5 Backport Implementation Guide enables servers running versions of FHIR earlier than R5 to implement a subset of R5 Subscriptions in a standardized way. (built Wed, Jan 11, 2023 15:34+1100+11:00)",
///         "kind": "IG",
///         "url": "http://packages2.fhir.org/packages/hl7.fhir.uv.subscriptions-backport/1.1.0"
///     },
///     {
///         "name": "hl7.fhir.uv.subscriptions-backport.r4",
///         "date": "2023-01-11T03:34:12-00:00",
///         "version": "1.1.0",
///         "fhirVersion": "R4",
///         "count": "18",
///         "canonical": "http://hl7.org/fhir/uv/subscriptions-backport",
///         "description": "The Subscription R5 Backport Implementation Guide enables servers running versions of FHIR earlier than R5 to implement a subset of R5 Subscriptions in a standardized way. (built Wed, Jan 11, 2023 15:34+1100+11:00)",
///         "kind": "IG",
///         "url": "http://packages2.fhir.org/packages/hl7.fhir.uv.subscriptions-backport.r4/1.1.0"
///     },
///     {
///         "name": "hl7.fhir.uv.subscriptions-backport.r4b",
///         "date": "2023-01-11T03:34:12-00:00",
///         "version": "1.1.0",
///         "fhirVersion": "R4B",
///         "count": "85",
///         "canonical": "http://hl7.org/fhir/uv/subscriptions-backport",
///         "description": "The Subscription R5 Backport Implementation Guide enables servers running versions of FHIR earlier than R5 to implement a subset of R5 Subscriptions in a standardized way. (built Wed, Jan 11, 2023 15:34+1100+11:00)",
///         "kind": "IG",
///         "url": "http://packages2.fhir.org/packages/hl7.fhir.uv.subscriptions-backport.r4b/1.1.0"
///     }
/// ]
/// </code>
/// </example>
[JsonConverter(typeof(RegistryCatalogJsonConverter))]
public record class RegistryCatalogRecord
{
    public string? Name { get; init; } = null;

    public string? Description { get; init; } = null;

    public string? FhirVersion { get; init; } = null;

    public DateTime? PublicationDate { get; init; } = null;

    public string? Version { get; init; } = null;

    public int? ResourceCount { get; init; } = null;

    public string? Canonical { get; init; } = null;

    public string? Kind { get; init; } = null;

    public string? Url { get; init; } = null;

    public string? Scope { get; init; } = null;

    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; init; } = null;

    [JsonIgnore]
    internal bool? UpperCaseNames { get; init; } = null;

    [JsonIgnore]
    public string? GitHubOrg { get; init; } = null;
    [JsonIgnore]
    public string? GitHubProject { get; init; } = null;
    [JsonIgnore]
    public string? GitHubBranch { get; init; } = null;
}
