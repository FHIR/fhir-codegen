// <copyright file="ServerConnector.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Hl7.Fhir.Language.Debugging;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Smart;
using static Hl7.Fhir.Model.VerificationResult;

#if NETSTANDARD2_0
using Fhir.CodeGen.Common.Polyfill;
#endif

namespace Fhir.CodeGen.Lib.Net;

/// <summary>
/// Represents a connector for interacting with a FHIR server.
/// </summary>
public class ServerConnector : IDisposable
{
    private string _fhirUrl;
    private string _smartUrl;
    private Dictionary<string, List<string>> _headers;
    private PackageLoader _packageLoader;
    private HttpClient _client;
    private FhirReleases.FhirSequenceCodes? _serverFhirVersion = null;

    private bool _disposedValue = false;

    /// <summary>Initializes a new instance of the <see cref="ServerConnector"/> class.</summary>
    /// <exception cref="ArgumentException">Thrown when one or more arguments have unsupported or
    ///  illegal values.</exception>
    /// <param name="fhirUrl">       The FHIR server URL.</param>
    /// <param name="smartConfigUrl">Override URL for smart configuration.</param>
    /// <param name="headers">       The headers to be included in the HTTP requests.</param>
    /// <param name="packageLoader"> The package loader used for parsing capability statements.</param>
    public ServerConnector(
        string fhirUrl,
        string smartConfigUrl,
        Dictionary<string, List<string>> headers,
        PackageLoader packageLoader)
    {
        if (string.IsNullOrEmpty(fhirUrl))
        {
            throw new ArgumentException("FHIR URL is required", nameof(fhirUrl));
        }

        _fhirUrl = fhirUrl;
        _smartUrl = smartConfigUrl;
        _headers = headers;
        _client = new();
        _packageLoader = packageLoader;
    }

    /// <summary>
    /// Tries to get the capabilities of the server.
    /// </summary>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <param name="json">The JSON response from the server.</param>
    /// <param name="capabilities">The parsed capability statement.</param>
    /// <returns><c>true</c> if the capabilities were successfully retrieved; otherwise, <c>false</c>.</returns>
    public bool TryGetCapabilities(
        out HttpStatusCode statusCode,
        out string json,
        [NotNullWhen(true)] out CapabilityStatement? capabilities,
        [NotNullWhen(true)] out FhirReleases.FhirSequenceCodes? serverFhirVersion)
    {
        try
        {
            // build the metadata url
            string url;

            if (_fhirUrl.EndsWith("metadata", StringComparison.OrdinalIgnoreCase))
            {
                url = _fhirUrl;
            }
            else if (_fhirUrl.EndsWith('/'))
            {
                url = _fhirUrl + "metadata";
            }
            else
            {
                url = _fhirUrl + "/metadata";
            }

            if ((!TryGetFhirJson(url, out statusCode, out json)) ||
                (statusCode.IsSuccessful() == false))
            {
                Console.WriteLine($"Request to {url} failed! Returned: {statusCode}");
                json = string.Empty;
                capabilities = null;
                serverFhirVersion = null;
                return false;
            }

            if (string.IsNullOrEmpty(json))
            {
                Console.WriteLine($"Request to {url} returned empty body!");
                json = string.Empty;
                capabilities = null;
                serverFhirVersion = null;
                return false;
            }

            string fhirVersion;
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                fhirVersion = doc.RootElement.GetProperty("fhirVersion").GetString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(fhirVersion))
            {
                Console.WriteLine($"Could not determine the FHIR version for {_fhirUrl}!");
                capabilities = null;
                serverFhirVersion = null;
                return false;
            }

            Console.WriteLine($"Connected to {_fhirUrl}, FHIR version: {fhirVersion}");

            serverFhirVersion = FhirReleases.FhirVersionToSequence(fhirVersion);
            _serverFhirVersion = serverFhirVersion;

            object? r;
            capabilities = null;

            switch (serverFhirVersion)
            {
                case FhirReleases.FhirSequenceCodes.DSTU2:
                    {
                        r = _packageLoader.ParseContents20("application/fhir+json", content: json);
                        if (r is CapabilityStatement cs)
                        {
                            capabilities = cs;
                        }
                    }
                    break;

                case FhirReleases.FhirSequenceCodes.STU3:
                    {
                        r = _packageLoader.ParseContents30("application/fhir+json", content: json);
                        if (r is CapabilityStatement cs)
                        {
                            capabilities = cs;
                        }
                    }
                    break;

                case FhirReleases.FhirSequenceCodes.R4:
                case FhirReleases.FhirSequenceCodes.R4B:
                    {
                        r = _packageLoader.ParseContents43("application/fhir+json", content: json);
                        if (r is CapabilityStatement cs)
                        {
                            capabilities = cs;
                        }
                    }
                    break;

                case FhirReleases.FhirSequenceCodes.R5:
                default:
                    {
                        r = _packageLoader.ParseContentsSystemTextStream("application/fhir+json", typeof(CapabilityStatement), content: json);
                        if (r is CapabilityStatement cs)
                        {
                            capabilities = cs;
                        }
                    }
                    break;
            }

            if (capabilities == null)
            {
                Console.WriteLine($"Failed to parse server capabilities for {_fhirUrl}!");
                capabilities = null;
                return false;
            }

            // check for a missing or relative URL
            if (string.IsNullOrEmpty(capabilities.Url) ||
                capabilities.Url.StartsWith('/') ||
                capabilities.Url.StartsWith("metadata", StringComparison.Ordinal))
            {
                // check for an implementation URL
                if (!string.IsNullOrEmpty(capabilities.Implementation?.Url))
                {
                    capabilities.Url = capabilities.Implementation!.Url;
                }
                else
                {
                    // use the FHIR server URL as the canonical URL
                    capabilities.Url = _fhirUrl;
                }
            }

            // print out the server information
            Console.WriteLine($"Server Information from {_fhirUrl}:");
            Console.WriteLine($"\t    FHIR Version: {capabilities.FhirVersion}");
            Console.WriteLine($"\t   Software Name: {capabilities.Software.Name}");
            Console.WriteLine($"\tSoftware Version: {capabilities.Software.Version}");
            Console.WriteLine($"\t    Release Date: {capabilities.Software.ReleaseDate}");
            Console.WriteLine($"\t     Description: {capabilities.Implementation?.Description}");
            Console.WriteLine($"\t       Resources: {capabilities.Rest.FirstOrDefault()?.Resource.Count}");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get metadata from: {_fhirUrl}, {ex.Message}");
            statusCode = HttpStatusCode.InternalServerError;
            json = string.Empty;
            capabilities = null;
            serverFhirVersion = null;
            return false;
        }
    }

    /// <summary>
    /// Attempts to build smart configuration a SmartWellKnown from the given CapabilityStatement.
    /// </summary>
    /// <param name="cs">         The capability statement.</param>
    /// <param name="smartConfig">[out] The SMART configuration.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    public bool TryBuildSmartConfig(CapabilityStatement cs, [NotNullWhen(true)] out SmartWellKnown? smartConfig)
    {
        // traverse each rest component in the capability statement
        foreach (CapabilityStatement.RestComponent rest in cs.Rest)
        {
            // check for the security component
            if (rest.Security == null)
            {
                continue;
            }

            // check for the SMART on FHIR extension
            Extension? smartExt = rest.Security.GetExtension(CommonDefinitions.ExtUrlSmartOAuth);

            if (smartExt == null)
            {
                continue;
            }

            smartConfig = new()
            {
                AuthorizationEndpoint = smartExt.GetExtensionValue<FhirUri>("authorize")?.ToString() ?? string.Empty,
                TokenEndpoint = smartExt.GetExtensionValue<FhirUri>("token")?.ToString() ?? string.Empty,
                RegistrationEndpoint = smartExt.GetExtensionValue<FhirUri>("register")?.ToString() ?? string.Empty,
                ManagementEndpoint = smartExt.GetExtensionValue<FhirUri>("manage")?.ToString() ?? string.Empty,
                IntrospectionEndpoint = smartExt.GetExtensionValue<FhirUri>("introspect")?.ToString() ?? string.Empty,
                RevocationEndpoint = smartExt.GetExtensionValue<FhirUri>("revoke")?.ToString() ?? string.Empty,
            };

            return true;
        }

        smartConfig = null;
        return false;
    }

    /// <summary>Tries to get the SMART configuration from the server.</summary>
    /// <param name="statusCode"> [out] The HTTP status code of the response.</param>
    /// <param name="json">       [out] The JSON response from the server.</param>
    /// <param name="smartConfig">[out] The parsed SMART configuration.</param>
    /// <returns>
    /// <c>true</c> if the SMART configuration was successfully retrieved; otherwise, <c>false</c>.
    /// </returns>
    public bool TryGetSmartConfig(
        out HttpStatusCode statusCode,
        out string json,
        [NotNullWhen(true)] out SmartWellKnown? smartConfig)
    {
        HttpRequestMessage? request = null;
        Uri requestUri;

        try
        {
            if (!string.IsNullOrEmpty(_smartUrl))
            {
                requestUri = new Uri(_smartUrl);
            }
            else if (_fhirUrl.EndsWith("metadata", StringComparison.OrdinalIgnoreCase))
            {
                requestUri = new Uri(string.Concat(_fhirUrl[(_fhirUrl.Length - 8)..], "/.well-known/smart-configuration"));
            }
            else if (_fhirUrl.EndsWith('/'))
            {
                Uri serverUri = new Uri(_fhirUrl);
                requestUri = new Uri(serverUri, ".well-known/smart-configuration");
            }
            else
            {
                requestUri = new Uri(_fhirUrl + "/.well-known/smart-configuration");
            }

            request = new HttpRequestMessage()
            {
                Method = HttpMethod.Get,
                RequestUri = requestUri,
                Headers =
                    {
                        Accept =
                        {
                            new MediaTypeWithQualityHeaderValue("application/json"),
                        },
                    },
            };

            Console.WriteLine($"Requesting SMART configuration from {request.RequestUri}...");

            HttpResponseMessage response = _client.SendAsync(request).Result;
            statusCode = response.StatusCode;

            if (statusCode != System.Net.HttpStatusCode.OK)
            {
                Console.WriteLine($"Request to {request.RequestUri} failed! Returned: {response.StatusCode}");
                json = string.Empty;
                smartConfig = null;
                return false;
            }

            json = response.Content.ReadAsStringAsync().Result;
            if (string.IsNullOrEmpty(json))
            {
                Console.WriteLine($"Request to {request.RequestUri} returned empty body!");
                json = string.Empty;
                smartConfig = null;
                return false;
            }

            smartConfig = JsonSerializer.Deserialize<SmartWellKnown>(json);

            if (smartConfig != null)
            {
                Console.WriteLine($"SMART Configuration from {_fhirUrl}:");
                Console.WriteLine($"\t     Authorization Endpoint: {smartConfig.AuthorizationEndpoint}");
                Console.WriteLine($"\t             Token Endpoint: {smartConfig.TokenEndpoint}");
                Console.WriteLine($"\tToken Endpoint Auth Methods: {smartConfig.TokenEndpointAuthMethods}");
                Console.WriteLine($"\t      Registration Endpoint: {smartConfig.RegistrationEndpoint}");
                Console.WriteLine($"\t         App State Endpoint: {smartConfig.AppStateEndpoint}");
                Console.WriteLine($"\t        Management Endpoint: {smartConfig.ManagementEndpoint}");
                Console.WriteLine($"\t     Introspection Endpoint: {smartConfig.IntrospectionEndpoint}");
                Console.WriteLine($"\t        Revocation Endpoint: {smartConfig.RevocationEndpoint}");

                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get SMART configuration from: {_fhirUrl}, {ex.Message}");
            statusCode = HttpStatusCode.InternalServerError;
            json = string.Empty;
            smartConfig = null;
            return false;
        }
        finally
        {
            if (request != null)
            {
                request.Dispose();
            }
        }

        return false;
    }

    public bool TryResolveCanonicals(
        CapabilityStatement capabilities,
        DefinitionCollection definitionCollection,
        bool resolveExternalReferences,
        out List<string> unresolvedCanonicals)
    {
        unresolvedCanonicals = [];

        // check instantiates for canonical CapabilityStatement resources
        foreach (string canonical in capabilities.Instantiates)
        {
            if (!TryResolveCanonical(canonical, definitionCollection, resolveExternalReferences, "CapabilityStatement"))
            {
                unresolvedCanonicals.Add(canonical);
            }
        }

        // check imports for canonical CapabilityStatement resources
        foreach (string canonical in capabilities.Imports)
        {
            if (!TryResolveCanonical(canonical, definitionCollection, resolveExternalReferences, "CapabilityStatement"))
            {
                unresolvedCanonicals.Add(canonical);
            }
        }

        // check implementationGuide for canonical ImplementationGuide resources
        foreach (string canonical in capabilities.ImplementationGuide)
        {
            if (!TryResolveCanonical(canonical, definitionCollection, resolveExternalReferences, "ImplementationGuide"))
            {
                unresolvedCanonicals.Add(canonical);
            }
        }

        // iterate over the rest array
        foreach (CapabilityStatement.RestComponent rest in capabilities.Rest)
        { 
            // iterate over each resource
            foreach (CapabilityStatement.ResourceComponent resource in rest.Resource)
            { 
                // check the profile for canonical StructureDefinition resources
                if (!string.IsNullOrEmpty(resource.Profile) &&
                    !TryResolveCanonical(resource.Profile, definitionCollection, resolveExternalReferences, "StructureDefinition"))
                {
                    unresolvedCanonicals.Add(resource.Profile);
                }

                // check each supportedProfile for canonical StructureDefinition resources
                foreach (string canonical in resource.SupportedProfile)
                {
                    if (!TryResolveCanonical(canonical, definitionCollection, resolveExternalReferences, "StructureDefinition"))
                    {
                        unresolvedCanonicals.Add(canonical);
                    }
                }

                // iterate over the resource searchParam array
                foreach (CapabilityStatement.SearchParamComponent searchParam in resource.SearchParam)
                {
                    // check definition for canonical SearchParameter resources
                    if (!string.IsNullOrEmpty(searchParam.Definition) &&
                        !TryResolveCanonical(searchParam.Definition, definitionCollection, resolveExternalReferences, "SearchParameter"))
                    {
                        unresolvedCanonicals.Add(searchParam.Definition);
                    }
                }

                // iterate over the resource operation array
                foreach (CapabilityStatement.OperationComponent operation in resource.Operation)
                {
                    // check definition for canonical OperationDefinition resources
                    if (!string.IsNullOrEmpty(operation.Definition) &&
                        !TryResolveCanonical(operation.Definition, definitionCollection, resolveExternalReferences, "OperationDefinition"))
                    {
                        unresolvedCanonicals.Add(operation.Definition);
                    }
                }
            }

            // iterate over the rest searchParam array
            foreach (CapabilityStatement.SearchParamComponent searchParam in rest.SearchParam)
            {
                // check definition for canonical SearchParameter resources
                if (!string.IsNullOrEmpty(searchParam.Definition) &&
                    !TryResolveCanonical(searchParam.Definition, definitionCollection, resolveExternalReferences, "SearchParameter"))
                {
                    unresolvedCanonicals.Add(searchParam.Definition);
                }
            }

            // iterate over the rest operation array
            foreach (CapabilityStatement.OperationComponent operation in rest.Operation)
            {
                // check definition for canonical OperationDefinition resources
                if (!string.IsNullOrEmpty(operation.Definition) &&
                    !TryResolveCanonical(operation.Definition, definitionCollection, resolveExternalReferences, "OperationDefinition"))
                {
                    unresolvedCanonicals.Add(operation.Definition);
                }
            }

            // check each compartment for canonical CompartmentDefinition resources
            foreach (string canonical in rest.Compartment)
            {
                if (!TryResolveCanonical(canonical, definitionCollection, resolveExternalReferences, "CompartmentDefinition"))
                {
                    unresolvedCanonicals.Add(canonical);
                }
            }
        }

        // TODO(ginoc): we cannot currently convert MessageDefinition resources between versions
        //// iterate over the messaging array
        //foreach (CapabilityStatement.MessagingComponent messaging in capabilities.Messaging)
        //{
        //    // iterate over the supportedMessage array
        //    foreach (CapabilityStatement.SupportedMessageComponent message in messaging.SupportedMessage)
        //    {
        //        // check definition for canonical MessageDefinition resources
        //        if (!string.IsNullOrEmpty(message.Definition) &&
        //            !TryResolveCanonical(message.Definition, definitionCollection, resolveExternalReferences))
        //        {
        //            unresolvedCanonicals.Add(message.Definition);
        //        }
        //    }
        //}

        // iterate over the document array
        foreach (CapabilityStatement.DocumentComponent document in capabilities.Document)
        {
            // check profile for canonical StructureDefinition resources
            if (!string.IsNullOrEmpty(document.Profile) &&
                !TryResolveCanonical(document.Profile, definitionCollection, resolveExternalReferences, "StructureDefinition"))
            {
                unresolvedCanonicals.Add(document.Profile);
            }
        }

        return unresolvedCanonicals.Count == 0;
    }

    public bool TryResolveCanonical(
        string canonicalUrl,
        DefinitionCollection definitionCollection,
        bool resolveExternalReferences,
        string? resourceType = null)
    {
        // check to see if we can already resolve this (e.g., in a package we have loaded)
        if (definitionCollection.CanResolveCanonicalUri(canonicalUrl))
        {
            return true;
        }

        string modified = canonicalUrl;

        // check for a canonical that ends in .html
        if (modified.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            // try removing the html
            modified = modified.Remove(modified.Length - 5);

            if (definitionCollection.CanResolveCanonicalUri(modified))
            {
                return true;
            }
        }

        // check for a canonical that is set to https (canonicals should be HTTP)
        if (modified.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // try swapping to http
            modified = string.Concat("http://", modified[8..]);

            if (definitionCollection.CanResolveCanonicalUri(modified))
            {
                return true;
            }
        }

        // check for a canonical that is set to www.hl7.org instead of hl7.org
        if (modified.StartsWith("http://www.hl7.org", StringComparison.OrdinalIgnoreCase))
        {
            // try swapping to hl7.org
            modified = string.Concat("http://hl7.org", modified[18..]);

            if (definitionCollection.CanResolveCanonicalUri(modified))
            {
                return true;
            }
        }

        // check for an HL7 canonical by removing the prefix (e.g., http://hl7.org/fhir/StructureDefinition/ -> StructureDefinition)
        if (modified.StartsWith("http://hl7.org/fhir/", StringComparison.OrdinalIgnoreCase))
        {
            // try removing the prefix (save as a new variable so we don't destroy our current modified one)
            string stripped = modified.Substring(23);

            if (stripped.EndsWith('/'))
            {
                stripped = stripped.Remove(stripped.Length - 1);
            }

            if (definitionCollection.CanResolveCanonicalUri(stripped))
            {
                return true;
            }
        }

        // check for a relative hl7.org canonical (e.g., StructureDefinition -> http://hl7.org/fhir/StructureDefinition)
        if (!modified.Contains('/'))
        {
            // try adding the prefix (save as a new variable so we don't destroy our current modified one)
            string prefixed = string.Concat("http://hl7.org/fhir/", modified);

            if (definitionCollection.CanResolveCanonicalUri(prefixed))
            {
                return true;
            }

            // check for a hyphenated relative name (e.g., StructureDefinition-Patient -> http://hl7.org/fhir/StructureDefinition/Patient)
            if (modified.Contains('-'))
            {
                // try adding the prefix (save as a new variable so we don't destroy our current modified one)
                prefixed = string.Concat("http://hl7.org/fhir/", modified.Replace('-', '/'));

                if (definitionCollection.CanResolveCanonicalUri(prefixed))
                {
                    return true;
                }
            }
        }

        if (!resolveExternalReferences)
        {
            if (!canonicalUrl.StartsWith(_fhirUrl, StringComparison.OrdinalIgnoreCase) &&
                !modified.StartsWith(_fhirUrl, StringComparison.OrdinalIgnoreCase))
            {
                // nothing more to try
                return false;
            }
        }

        string url;

        // try to build a valid URL
        if (canonicalUrl.StartsWith("http", StringComparison.Ordinal))
        {
            url = canonicalUrl;
        }
        else if (_fhirUrl.EndsWith('/'))
        {
            url = string.Concat(_fhirUrl, canonicalUrl.StartsWith('/') ? canonicalUrl[1..] : canonicalUrl);
        }
        else
        {
            url = canonicalUrl.StartsWith('/')
                ? string.Concat(_fhirUrl, canonicalUrl)
                : string.Concat(_fhirUrl, "/", canonicalUrl);
        }

        string json = "";
        HttpStatusCode statusCode;
        bool successful = false;

        // if we have a resource type, we can ask the server if it can resolve the canonical
        if (!string.IsNullOrEmpty(resourceType))
        {
            string serverResolveUrl;
            if (_fhirUrl.EndsWith('/'))
            {
                serverResolveUrl = string.Concat(_fhirUrl, resourceType, "?url=", WebUtility.UrlEncode(canonicalUrl));
            }
            else
            {
                serverResolveUrl = string.Concat(_fhirUrl, "/", resourceType, "?url=", WebUtility.UrlEncode(canonicalUrl));
            }

            if (TryGetFhirJson(serverResolveUrl, out statusCode, out string searchJson) &&
                statusCode.IsSuccessful())
            {

                object? oBundle = parseResource(searchJson);
                if ((oBundle != null) &&
                    (oBundle is Bundle bundle) &&
                    (bundle.Entry.Count > 0) &&
                    (bundle.Entry[0].Resource != null))
                {

                    // add this canonical to the definition collection
                    definitionCollection.AddResource(
                        bundle.Entry[0].Resource,
                        _serverFhirVersion ?? FhirReleases.FhirSequenceCodes.Unknown,
                        "codegen.local",
                        "0.0.0",
                        canonicalUrl);

                    return true;
                }
            }
        }

        // first try the original canonical URL
        if (!successful &&
            TryGetFhirJson(url, out statusCode, out json) &&
            statusCode.IsSuccessful())
        {
            successful = true;
        }

        if (!successful)
        {
            // try the same process with our modified version
            if (modified.StartsWith("http", StringComparison.Ordinal))
            {
                url = modified;
            }
            else if (_fhirUrl.EndsWith('/'))
            {
                url = modified.StartsWith('/')
                    ? _fhirUrl + modified.Substring(1)
                    : _fhirUrl + modified;
            }
            else
            {
                url = modified.StartsWith('/')
                    ? _fhirUrl + modified
                    : _fhirUrl + "/" + modified;
            }

            if (TryGetFhirJson(url, out statusCode, out json) &&
                statusCode.IsSuccessful())
            {
                successful = true;
            }
        }

        if ((!successful) || (string.IsNullOrEmpty(json)))
        {
            return false;
        }

        object? r = parseResource(json);
        if (r == null)
        {
            return false;
        }

        // add this canonical to the definition collection
        definitionCollection.AddResource(
            r,
            _serverFhirVersion ?? FhirReleases.FhirSequenceCodes.Unknown,
            "codegen.local",
            "0.0.0",
            canonicalUrl);

        return true;

        object? parseResource(string json) => _serverFhirVersion switch
        {
            FhirReleases.FhirSequenceCodes.DSTU2 => _packageLoader.ParseContents20("application/fhir+json", content: json),
            FhirReleases.FhirSequenceCodes.STU3 => _packageLoader.ParseContents30("application/fhir+json", content: json),
            FhirReleases.FhirSequenceCodes.R4 => _packageLoader.ParseContents43("application/fhir+json", content: json),
            FhirReleases.FhirSequenceCodes.R4B => _packageLoader.ParseContents43("application/fhir+json", content: json),
            FhirReleases.FhirSequenceCodes.R5 => _packageLoader.ParseContentsPoco("application/fhir+json", content: json),
            _ => _packageLoader.ParseContentsPoco("application/fhir+json", content: json),
        };
    }

    /// <summary>Attempts to FHIR get JSON.</summary>
    /// <param name="url">       URL of the resource.</param>
    /// <param name="statusCode">[out] The HTTP status code of the response.</param>
    /// <param name="json">      [out] The JSON response from the server.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    private bool TryGetFhirJson(string url, out HttpStatusCode statusCode, out string json)
    {
        HttpRequestMessage? request = null;
        Uri requestUri;

        try
        {
            requestUri = new Uri(url);

            request = new HttpRequestMessage()
            {
                Method = HttpMethod.Get,
                RequestUri = requestUri,
                Headers =
                    {
                        Accept =
                        {
                            new MediaTypeWithQualityHeaderValue("application/fhir+json"),
                        },
                    },
            };

            if (url.StartsWith(_fhirUrl, StringComparison.Ordinal) && (_headers.Count > 0))
            {
                foreach ((string key, IEnumerable<string> values) in _headers)
                {
                    request.Headers.Add(key, values);
                }
            }

            Console.WriteLine($"Requesting {request.RequestUri}...");

            HttpResponseMessage response = _client.SendAsync(request).Result;
            statusCode = response.StatusCode;

            if (statusCode != System.Net.HttpStatusCode.OK)
            {
                Console.WriteLine($"Request to {request.RequestUri} failed! Returned: {statusCode}");
                json = string.Empty;
                return false;
            }

            json = response.Content.ReadAsStringAsync().Result;

            if (string.IsNullOrEmpty(json))
            {
                Console.WriteLine($"Request to {request.RequestUri} returned empty body!");
                json = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get metadata from: {_fhirUrl}, {ex.Message}");
            statusCode = HttpStatusCode.InternalServerError;
            json = string.Empty;
            return false;
        }
        finally
        {
            if (request != null)
            {
                request.Dispose();
            }
        }
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="ServerConnector"/>
    /// and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to
    ///  release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null!;
                }
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            _disposedValue = true;
        }
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged
    /// resources.
    /// </summary>
    void IDisposable.Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
