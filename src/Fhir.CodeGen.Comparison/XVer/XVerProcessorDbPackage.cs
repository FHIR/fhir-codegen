using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.Data;
using System.Data.Common;
using System.Drawing;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Language;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Model.CdsHooks;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Snapshot;
using Hl7.Fhir.Support;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;
using Microsoft.Extensions.Logging;
using Octokit;
using static System.Net.Mime.MediaTypeNames;
using static Fhir.CodeGen.Common.Packaging.PackageContents;

//using static ICSharpCode.SharpZipLib.Zip.FastZip;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;
using Tasks = System.Threading.Tasks;

namespace Fhir.CodeGen.Comparison.XVer;

public partial class XVerProcessor
{
    private readonly record struct XverIgDependencyRec
    {
        public required string PackageId { get; init; }
        public required string PackageVersion { get; init; }
        public required string CanonicalUrl { get; init; }
        public required bool VersionSpecificPackages { get; init; }
        public required bool HasR4B { get; init; }
        public required bool NeededForPublisher { get; init; }

        public string AsYamlProp(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            if (!VersionSpecificPackages)
            {
                return $"{PackageId} : {PackageVersion}";
            }

            string suffix = (fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B)
                ? "r4"
                : fhirSequence.ToString().ToLowerInvariant();

            return $"{PackageId}.{suffix} : {PackageVersion}";
        }

        public string AsSushiYaml(FhirReleases.FhirSequenceCodes fhirSequence, string levelIndent = "    ")
        {
            if (!VersionSpecificPackages)
            {
                if (!string.IsNullOrEmpty(CanonicalUrl))
                {
                    return $"""
                        {levelIndent}{PackageId}:
                        {levelIndent}{levelIndent}id: {PackageId.Replace('.', '_')}
                        {levelIndent}{levelIndent}uri: {CanonicalUrl}
                        {levelIndent}{levelIndent}version: {PackageVersion}
                        """;
                }

                return $"{levelIndent}{PackageId} : {PackageVersion}";
            }

            string suffix = (fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B)
                ? "r4"
                : fhirSequence.ToString().ToLowerInvariant();

            if (!string.IsNullOrEmpty(CanonicalUrl))
            {
                return $"""
                    {levelIndent}{PackageId}.{suffix}:
                    {levelIndent}{levelIndent}id: {(PackageId.Replace('.', '_') + "_" + suffix)}
                    {levelIndent}{levelIndent}uri: {CanonicalUrl}
                    {levelIndent}{levelIndent}version: {PackageVersion}
                    """;
            }

            return $"{levelIndent}{PackageId}.{suffix} : {PackageVersion}";
        }

        public string AsJsonProp(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            if (!VersionSpecificPackages)
            {
                return $"\"{PackageId}\" : \"{PackageVersion}\"";
            }

            string suffix = (fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B)
                ? "r4"
                : fhirSequence.ToString().ToLowerInvariant();

            return $"\"{PackageId}.{suffix}\" : \"{PackageVersion}\"";
        }

        public string AsJsonIgDependency(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            string dotSuffix;
            if (!VersionSpecificPackages)
            {
                dotSuffix = string.Empty;
            }
            else if ((fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B))
            {
                dotSuffix = ".r4";
            }
            else
            {
                dotSuffix = "." + fhirSequence.ToString().ToLowerInvariant();
            }

            string packageId = PackageId + dotSuffix;
            string id = packageId.Replace('.', '_');

            return $$$"""{ "packageId":"{{{packageId}}}", "version":"{{{PackageVersion}}}", "uri":"{{{CanonicalUrl}}}", "id":"{{{id}}}" }""";
        }

        public ImplementationGuide.DependsOnComponent AsIgDependsOn(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            string dotSuffix;
            if (!VersionSpecificPackages)
            {
                dotSuffix = string.Empty;
            }
            else if ((fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B))
            {
                dotSuffix = ".r4";
            }
            else
            {
                dotSuffix = "." + fhirSequence.ToString().ToLowerInvariant();
            }

            string packageId = PackageId + dotSuffix;
            string id = packageId.Replace('.', '_');

            return new ImplementationGuide.DependsOnComponent
            {
                ElementId = id,
                PackageId = packageId,
                Version = PackageVersion,
                Uri = CanonicalUrl,
            };
        }
    }

    private static readonly List<XverIgDependencyRec> _xverDependencies = [
        new()
        {
            PackageId = "hl7.terminology",
            PackageVersion = "6.5.0",
            CanonicalUrl = "http://terminology.hl7.org/ImplementationGuide/hl7.terminology",                // "http://terminology.hl7.org"
            VersionSpecificPackages = true,
            HasR4B = false,
            NeededForPublisher = false,
        },
        new()
        {
            PackageId = "hl7.fhir.uv.extensions",
            PackageVersion = "5.3.0-ballot-tc1",
            CanonicalUrl = "http://hl7.org/fhir/extensions/ImplementationGuide/hl7.fhir.uv.extensions",     // "http://hl7.org/fhir/extensions"
            VersionSpecificPackages = true,
            HasR4B = true,
            NeededForPublisher = false,
        },
        new()
        {
            PackageId = "hl7.fhir.uv.tools",
            PackageVersion = "0.8.0",
            CanonicalUrl = "http://hl7.org/fhir/tools/ImplementationGuide/hl7.fhir.uv.tools",                // "http://hl7.org/fhir/tools"
            VersionSpecificPackages = true,
            HasR4B = false,
            NeededForPublisher = false,
        }
    ];

    // codes described at: https://build.fhir.org/ig/FHIR/fhir-tools-ig/branches/master/CodeSystem-ig-parameters.html
    private static readonly List<(string code, string value)> _xverIgParameters = [
        // apply-contact: if true, overwrite all canonical resource contact details with that found in the IG.
        ("apply-contact", "false"),

        // apply-context: if true, overwrite all canonical resource context details with that found in the IG.
        ("apply-context", "false"),

        // apply-copyright: if true, overwrite all canonical resource copyright details with that found in the IG.
        ("apply-copyright", "true"),

        // apply-jurisdiction: if true, overwrite all canonical resource jurisdiction details with that found in the IG.
        ("apply-jurisdiction", "false"),

        // apply-publisher: if true, overwrite all canonical resource publisher details with that found in the IG.
        ("apply-publisher", "false"),

        // apply-version: if true, overwrite all canonical resource version details with that found in the IG.
        ("apply-version", "false"),

        // apply-wg: if true, overwrite all canonical resource WG details with that found in the IG.
        ("apply-wg", "false"),

        // copyrightyear: The copyright year text to include in the implementation guide footer
        ("copyrightyear", "2025+"),

        // default-contact: if true, populate all canonical resources that don't specify their own contact details with that found in the IG. Ignored if apply-contact is true.
        ("default-contact", "true"),

        // default-context: if true, populate all canonical resources that don't specify their own context details with that found in the IG. Ignored if apply-context is true.
        ("default-context", "false"),

        // default-copyright: if true, populate all canonical resources that don't specify their own copyright details with that found in the IG. Ignored if apply-copyright is true.
        //("default-copyright", "true"),

        // default-jurisdiction: f true, populate all canonical resources that don't specify their own jurisdiction details with that found in the IG. Ignored if apply-jurisdiction is true.
        ("default-jurisdiction", "true"),

        // default-publisher: if true, populate all canonical resources that don't specify their own publisher details with that found in the IG. Ignored if apply-publisher is true.
        ("default-publisher", "false"),

        // default-version: if true, populate all canonical resources that don't specify their own version details with that found in the IG. Ignored if apply-version is true.
        ("default-version", "true"),

        // default-wg: if true, populate all canonical resources that don't specify their own WG details with that found in the IG. Ignored if apply-contact is true.
        ("default-wg", "true"),

        // excludemap: If true, causes the mapping tab to be excluded from all StructureDefinition artifact pages
        ("excludemap", "true"),

        // i18n-default-lang: The default language (e.g. Resource.language) to assume in the IG when the resource and/or the element context doesn't specify a language
        ("i18n-default-lang", "US-en"),

        // jira-code: If your IG is published via HL7 and should your package ID diverge from the file name in the JIRA-Spec-Artifacts repository, this parameter will help point to the right file.
        //("jira-code", ""),

        // no-expansions-files: Do not create the 'expansions.*' files
        ("no-expansions-files", "true"),

        // no-ig-database: Do not create the package.db file
        ("no-ig-database", "true"),

        // no-usage-check: No Warning in QA if there are extensions/profiles that are not used in this IG
        ("no-usage-check", "true"),

        /* pin-canonicals: Defines how the IG publisher treats unversioned canonical references. Possible values:
         *   pin-none: no action is taken (default)
         *   pin-all: any unversioned canonical references that can be resolved through the package dependencies will have |(version) appended to the canonical, where (version) is the latest available within the package dependencies
         *   pin-multiples: pinning the canonical reference will only happen if there is multiple versions found in the package dependencies
         */
        ("pin-canonicals", "pin-all"),

        // releaselabel: The release label at the top of the page. This is a text label with no fixed set of values that describes the status of the publication to users. Typical values might be 'STU X' or 'Normative Standard' or '2024 Edition'
        ("releaselabel", "STU"),

        // show-inherited-invariants: if true, render inherited constraints in the full details and invariants view
        ("show-inherited-invariants", "false"),

        // shownav: Determines whether the next/previous navigation tabs are shown in the header and footer
        ("shownav", "true"),

        // special-url: If a canonical resource in the IG should actually have a URL that isn't the one implied by the canonical URL for the IG itself, it must be listed here explicitly (as well as defined in the resource itself). It must be listed here to stop it accidentally being different. Each canonical url must be listed in full as present on the resource; it is not possible to specify a pattern.
        //("special-url", "http://terminology.hl7.org/CodeSystem/designation-usage"),
        //("special-url", "http://terminology.hl7.org/ValueSet/designation-usage"),

        // special-url-base: A common alternative base URL for multiple canonical resources in the IG. The entire Canonical URL must exactly match {special-url-base}/{type}/{id}
        ("special-url-base", "http://terminology.hl7.org"),

        // suppress-mappings: By default, snapshots inherit mappings, and the mappings are carried through. But many of them aren't useful, or desired, and can be suppressed by adding this parameter. The value is the URI found in StructureDefinition.mapping.uri. The special value '*' suppresses most of the mappings in the main specification
        ("suppress-mappings", "true"),

        // usage-stats-opt-out: If true, usage stats (information about extensions, value sets, and invariants being used) is not sent to fhir.org (see e.g. http://clinfhir.com/igAnalysis.html).
        ("usage-stats-opt-out", "true"),

        /* version-comparison:
         * Control how the IG publisher does a comparison with a previously published version (see qa.html). Possible values:
         *   {last} - compare with the last published version (whatever it's status) - this is the default if the parameter doesn't appear
         *   {current} - compare with the last full published version
         *   n/a - don't do any comparison
         *   [v] - a previous version where [v] is the version
         */
        ("version-comparison", "n/a"),
    ];

    private const string _xverIgnoreWarningsTxt = $$$"""
        == Suppressed Messages ==

        # ==== 01. Extension names and ids are shortened to avoid exceeding 64 character limit ====
        RESOURCE_ID_MISMATCH
        ERROR: StructureDefinition.url: Resource id/url mismatch: %
        RESOURCE_CANONICAL_MISMATCH
        ERROR: StructureDefinition.where(url = '%'): Conformance resource % - the canonical URL (%) does not match the URL (%)
        ERROR: %: URL Mismatch % vs %
        
        # ==== 02. These are the 'default' ValueSets from ported CodeSystem resources. We do not want to define them ====
        TYPE_SPECIFIC_CHECKS_DT_CANONICAL_RESOLVE
        A definition could not be found for Canonical URL %
        ERROR: CodeSystem/%: CodeSystem.valueSet: A definition could not be found for Canonical URL %

        # ==== 03. We are not building examples for everything ====
        The Implementation Guide contains no examples for this extension
        WARNING: StructureDefinition.where(url = %): The Implementation Guide contains no examples for this extension
        WARNING: StructureDefinition.where(url = %): The Implementation Guide contains no examples for this profile
 
        # ==== 04. We are faithfully reproducing existing Code Systems and cannot address these ====
        CODESYSTEM_CONCEPT_NO_DEFINITION
        HL7 Defined CodeSystems should ensure that every concept has a definition
        CODESYSTEM_CONCEPT_NO_DISPLAY
        HL7 Defined CodeSystems should ensure that every concept has a display
        CODESYSTEM_CS_COMPLETE_AND_EMPTY
        When a CodeSystem has content = 'complete', it doesnt make sense for there to be no concepts defined
        CODESYSTEM_CS_HL7_MISSING_ELEMENT_SHOULD
        HL7 Defined CodeSystems SHOULD have a stated value for the hierarchyMeaning element so that users know the status and meaning of the code system clearly
        CODESYSTEM_PROPERTY_CODE_DEFAULT_WARNING
        The type of property 'code' is 'code', but no ValueSet information was found, so the codes will be validated as internal codes
        CODESYSTEM_PROPERTY_UNKNOWN_CODE
        This property has only a code ('%') and not a URI, so it has no clearly defined meaning in the terminology ecosystem
        CODESYSTEM_PROPERTY_URI_INVALID
        The uri '%' for the property '%' implies a property with that URI exists in the CodeSystem FHIR Defined Concept Properties for http://hl7.org/fhir/concept-properties, or the code '%' does, but neither were found
        CODESYSTEM_THO_CHECK
        Most code systems defined in HL7 IGs will need to move to THO later during the process. Consider giving this code system a THO URL now (See https://confluence.hl7.org/display/TSMG/Terminology+Play+Book, and/or talk to TSMG)
        MSG_DRAFT
        Reference to draft CodeSystem http://hl7.org/fhir/CodeSystem/knowledge-representation-level|5.0.0
        VALIDATION_VAL_STATUS_INCONSISTENT
        The resource status 'active' and the standards status 'draft' are not consistent
        The resource status 'draft' and the standards status 'normative' are not consistent
        ERROR: % If a resource is not implementable, is marked as experimental or example, the standards status can only be 'informative', 'draft' or 'deprecated', not 'trial-use'.
        VALIDATION_VAL_STATUS_INCONSISTENT_HINT
        The resource status 'draft' and the standards status 'trial-use' may not be consistent and should be reviewed
        WARNING: %: The property '%' has no definition in CodeSystem.property. Many terminology tools won't know what to do with it
        INFORMATION: %: CodeSystem: Review the All Codes Value Set - incomplete CodeSystems generally should not have an all codes value set specified
        INFORMATION: CodeSystem/v2-0360: CodeSystem: Resource is not deprecated, but the description mentions deprecated - check whether it should be deprecated
        
        # ==== 05. --  ===
        
        # ==== 06. We cannot honor inactive flags since we are porting existing values ====
        VALUESET_BAD_FILTER_VALUE_VALID_CODE_INACTIVE
        The code for the filter 'concept' is inactive %

        # ==== 07. We cannot change filters since we are porting existing values ====
        VALUESET_BAD_FILTER_VALUE_VALID_CODE_CHANGE
        ERROR: ValueSet/%: %: The value for a filter based on property 'SCALE_TYP' must be a valid code from the system 'http://loinc.org', and 'Doc' is not (Unknown code 'Doc' in the CodeSystem 'http://loinc.org' version '2.80'). Note that this is change from the past; terminology servers are expected to still continue to support this filter
        ERROR: ValueSet/%: %: The value for a filter based on property 'parent' must be a valid code from the system 'http://loinc.org', and 'LP43571-6' is not (Unknown code 'LP43571-6' in the CodeSystem 'http://loinc.org' version '2.80'). Note that this is change from the past; terminology servers are expected to still continue to support this filter
        
        # ==== 08. These are warnings in profiles based on elements inherited from core and cannot be overridden ====
        MSG_DEPENDS_ON_DEPRECATED_NOTE
        The extension http://hl7.org/fhir/StructureDefinition/elementdefinition-maxValueSet|% is deprecated with the note: 'Use additionalBinding extension or element instead'
        INFORMATION: CodeSystem/concept-properties: %: The extension http://hl7.org/fhir/StructureDefinition/elementdefinition-maxValueSet|% is deprecated
        The extension http://hl7.org/fhir/StructureDefinition/codesystem-use-markdown|% is deprecated with the note: 'This extension is deprecated as the Terminology Infrastructure work group felt there wasn't a use case for the extension'
        INFORMATION: CodeSystem/concept-properties: %: The extension http://hl7.org/fhir/StructureDefinition/codesystem-use-markdown|% is deprecated
        The extension http://hl7.org/fhir/StructureDefinition/valueset-special-status|% is deprecated with the note: 'This extension is deprecated as Terminology Infrastructure was unable to determine a use for it'
        INFORMATION: CodeSystem/concept-properties: %: The extension http://hl7.org/fhir/StructureDefinition/valueset-special-status|% is deprecated
        
        # ==== 09. We cannot change the experimental flag on any existing content ====
        SD_ED_EXPERIMENTAL_BINDING
        The definition for the element 'Extension.extension.extension.value[x]' binds to the value set '%' which is experimental, but this structure is not labeled as experimental
        INFORMATION: I%: ImplementationGuide.definition.parameter[0].code: Reference to experimental CodeSystem %

        # ==== 10. This URL should not have been used for an example code system, but it was and we cannot change it ====
        A definition for CodeSystem 'http://acme.com/config/fhir/codesystems/internal' could not be found, so the code cannot be validated

        # ==== 11. FHIR-I is publishing this package, but we preserve the WG responsible for content where possible ====
        VALIDATION_HL7_PUBLISHER_MISMATCH
        The nominated WG '%' means that the publisher should be '%' but 'HL7 International / FHIR Infrastructure' was found

        # ==== 12. We cannot add display values to existing code systems that lack them ====
        VALUESET_CONCEPT_DISPLAY_PRESENCE_MIXED
        This include has some concepts with displays and some without - check that this is what is intended

        # ==== 13. We cannot change the semantic structure of any existing content ====
        VALUESET_CONCEPT_DISPLAY_SCT_TAG_MIXED
        This SNOMED-CT based include has some concepts with semantic tags (FSN terms) and some without (preferred terms) - check that this is what is intended %

        # ==== 14. Pinning is configured ====
        Pinned the version of %
        INFORMATION: % Pinned the version of % to %
        WARNING: ValueSet/%: %: There are multiple different potential matches for the url 'http://hl7.org/fhir/CodeSystem/example'. It might be a good idea to fix to the correct version to reduce the likelihood of a wrong version being selected by an implementation/implementer, or use the [IG Parameter `pin-canonicals`](https://hl7.org/fhir/tools/CodeSystem-ig-parameters.html). %

        # ==== 15. `guide-parameter-code` is the correct CodeSystem for IG parameters ====
        INFORMATION: ImplementationGuide/%: ImplementationGuide.%.code: Reference to experimental CodeSystem http://hl7.org/fhir/guide-parameter-code|5.0.0

        # ==== 16. These definitions are not correct in the source version, but I cannot change them ====
        VALUESET_INCLUDE_WRONG_CS_OID
        ERROR: %: %: It is not valid to refer to a CodeSystem by an identifier like this 'urn:oid:2.16.840.1.113883.6.276' - use 'http://terminology.hl7.org/CodeSystem/GMDN'
        INFORMATION: %: %: A definition for CodeSystem 'urn:oid:2.16.840.1.113883.6.276' could not be found, so the code cannot be validated
        WARNING: %: %: The terminology server null used for the CodeSystem urn:oid:2.16.840.1.113883.6.276 does not support batch validation (tx version Not Known), so the codes have not been validated
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r3: Unable to provide support for code system urn:oid:2.16.840.1.113883.6.276
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r4: Unable to provide support for code system urn:oid:2.16.840.1.113883.6.276
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r5: Unable to provide support for code system urn:oid:2.16.840.1.113883.6.276
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r6: Unable to provide support for code system urn:oid:2.16.840.1.113883.6.276
        ERROR: %: %: It is not valid to refer to a CodeSystem by an identifier like this 'urn:oid:2.16.840.1.113883.3.26.1.1' - use 'http://ncicb.nci.nih.gov/xml/owl/EVS/Thesaurus.owl'
        ERROR: %: %: The code '%' is not valid in the system urn:oid:2.16.840.1.113883.3.26.1.1 version 5.0.0 (%)
        INFORMATION: %: %: A definition for CodeSystem 'urn:oid:2.16.840.1.113883.3.26.1.1' version '5.0.0' could not be found, so the code cannot be validated. Valid versions: []
        WARNING: %: %: The terminology server null used for the CodeSystem urn:oid:2.16.840.1.113883.3.26.1.1|5.0.0 does not support batch validation (tx version Not Known), so the codes have not been validated

        # ==== 17. Source content should not have duplicate Resource.name values, but they do ====
        WARNING: Jira file generation will not be correct because multiple artifacts have the same name (ignoring content in "()"): %
        **WARNING** Jira file generation will not be correct because multiple artifacts have the same name (ignoring content in "()"): %

        # ==== 18. We are not re-exporting the 'all system' value sets for ported code systems ====
        ERROR: %: CodeSystem: CodeSystem % has an 'all system' value set of %, but the value set doesn't have a matching system (%)

        # ==== 19. Ported CodeSystems need to use their original URLs, even though they are published in version-specific packages and do not match the `special-url` format ====
        ERROR: %: URL Mismatch http://hl7.org/fhir/1.0/CodeSystem/% vs http://hl7.org/fhir/%
        ERROR: %: URL Mismatch http://hl7.org/fhir/2.0/CodeSystem/% vs http://hl7.org/fhir/%
        ERROR: %: URL Mismatch http://hl7.org/fhir/3.0/CodeSystem/% vs http://hl7.org/fhir/%
        ERROR: %: URL Mismatch http://hl7.org/fhir/4.0/CodeSystem/% vs http://hl7.org/fhir/%
        ERROR: %: URL Mismatch http://hl7.org/fhir/4.3/CodeSystem/% vs http://hl7.org/fhir/%
        ERROR: %: URL Mismatch http://hl7.org/fhir/5.0/CodeSystem/% vs http://hl7.org/fhir/%
        ERROR: %: URL Mismatch http://hl7.org/fhir/6.0/CodeSystem/% vs http://hl7.org/fhir/%

        # ==== 20. Many existing code systems use properties and do not define the codes ====
        WARNING: CodeSystem/%: CodeSystem.%.property%: The code '%' is not a valid code in this code system

        # ==== 21. These are formatted with `<p>` tags ====
        INFORMATION: CodeSystem/v2-0203: %: The string value contains text that looks like embedded HTML tags. If this content is rendered to HTML without appropriate post-processing, it may be a security risk
        INFORMATION: CodeSystem/v3-ActCode: %: The string value contains text that looks like embedded HTML tags. If this content is rendered to HTML without appropriate post-processing, it may be a security risk

        # ==== 22. ValueSet with overly-large expansion is not validated ====
        INFORMATION: ValueSet/%: %: The value set include has too many codes to validate (%), so each individual code has not been checked
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r3: Unable to provide support for code system http://snomed.info/sct version http://snomed.info/sct/900000000000207008/version/20200731 (known versions = %)
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r4: Unable to provide support for code system http://snomed.info/sct version http://snomed.info/sct/900000000000207008/version/20200731 (known versions = %)
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r5: Unable to provide support for code system http://snomed.info/sct version http://snomed.info/sct/900000000000207008/version/20200731 (known versions = %)
        WARNING: ValueSet.where(id = '%'): Error from https://tx.fhir.org/r6: Unable to provide support for code system http://snomed.info/sct version http://snomed.info/sct/900000000000207008/version/20200731 (known versions = %)

        # ==== 23. ValueSets with overly-large expansions are not fully displayed ====
        VALUESET_INC_TOO_MANY_CODES
        INFORMATION: ValueSet.where(id = '%'): The value set expansion is too large, and only a subset has been displayed

        # ==== 24. This was a typo in R5 and has been fixed in R6 ====
        ERROR: ValueSet/%: %: The code 'urn:ihe.palm:apsr:2016' is not valid in the system http://ihe.net/fhir/ihe.formatcode.fhir/CodeSystem/formatcode version 1.0.0 (urn:ihe.palm:apsr:2016)

        # ==== 25. This should not be validated, since it is example.org ====
        WARNING: ValueSet/%: %: A definition for CodeSystem 'http://example.org/CodeSystem/contexttype' could not be found, so the code cannot be validated
        WARNING: ValueSet/%: %: A definition for CodeSystem 'http://example.org/fhir/CodeSystem/use-contexts' could not be found, so the code cannot be validated
        
        # ==== 26. We are using the jurisdiction on any artifacts that have one specified ====
        WARNING: ValueSet.jurisdiction: The resource should declare its jurisdiction to match the package id (%) (for Sushi users: in sushi-config.yaml, 'jurisdiction: http://unstats.un.org/unsd/methods/m49/m49.htm#001 "World"')

        # ==== 27. These are example ValueSets and incorrect, but we cannot change the properties ====
        ERROR: ValueSet.where(id = '%example%'): Unsupported property value for a CodeSystem Property: boolean (and Error from https://tx.fhir.org/r3: The filter "acme-plasma = true" from the value set % was not understood in the context of http://hl7.org/fhir/CodeSystem/example (3))
        ERROR: ValueSet.where(id = '%example%'): Unsupported property value for a CodeSystem Property: boolean (and Error from https://tx.fhir.org/r4: The filter "acme-plasma = true" from the value set % was not understood in the context of http://hl7.org/fhir/CodeSystem/example (3))
        ERROR: ValueSet.where(id = '%example%'): Unsupported property value for a CodeSystem Property: boolean (and Error from https://tx.fhir.org/r5: The filter "acme-plasma = true" from the value set % was not understood in the context of http://hl7.org/fhir/CodeSystem/example (3))
        ERROR: ValueSet.where(id = '%example%'): Unsupported property value for a CodeSystem Property: boolean (and Error from https://tx.fhir.org/r6: The filter "acme-plasma = true" from the value set % was not understood in the context of http://hl7.org/fhir/CodeSystem/example (3))

        # ==== 28. This was wrong in this version of THO ====
        VALUESET_INCLUDE_CS_CONTENT
        INFORMATION: ValueSet/%: %: The value set references CodeSystem '%' which has status 'fragment'
        VALUESET_INCLUDE_CSVER_CONTENT
        INFORMATION: ValueSet/%: %: The value set references CodeSystem '%' version '%' which has status 'fragment'
        
        """;

    /// <summary>
    /// Creates a compressed .tgz (tar.gz) archive from the specified source directory.
    /// </summary>
    /// <param name="sourceDirectory">The directory to archive and compress.</param>
    /// <param name="outputTgzFile">The path to the output .tgz file.</param>
    /// <remarks>
    /// This method creates a tar archive of the specified directory and compresses it using GZip.
    /// If an error occurs during the process, a message is written to the console.
    /// </remarks>
    private static void createTgzFromDirectory(string sourceDirectory, string outputTgzFile)
    {
        try
        {
            // Compress the tar file into a .tgz file
            using (FileStream tgzFileStream = File.Create(outputTgzFile))
            using (GZipStream gzipStream = new GZipStream(tgzFileStream, CompressionLevel.Optimal))
            {
                TarFile.CreateFromDirectory(sourceDirectory, gzipStream, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create tgz {outputTgzFile} from source {sourceDirectory}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the ImplementationGuide, manifest, index, and package.json files for each validation package.
    /// </summary>
    /// <param name="packageSupports">The list of package support objects representing each FHIR package.</param>
    /// <param name="allPackageIndexInfos">The list of all cross-version package index information objects.</param>
    /// <param name="fhirDir">The root directory where FHIR artifacts are written.</param>
    private void writeXverValidationPackageSupportFiles(
        List<PackageXverSupport> packageSupports,
        List<XverPackageIndexInfo> allPackageIndexInfos,
        string fhirDir)
    {
        // iterate over the support packages
        foreach (PackageXverSupport packageSupport in packageSupports)
        {
            string packageId = getPackageId(null, packageSupport.Package);
            string dir = createExportPackageDir(fhirDir, null, packageSupport.Package);

            dir = Path.Combine(dir, "package");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            List<(string packageId, string packageVersion)> internalDependencies = [];
            foreach (PackageXverSupport sourcePackage in packageSupports)
            {
                if (sourcePackage.Package.Key == packageSupport.Package.Key)
                {
                    continue;
                }

                internalDependencies.Add((
                    getPackageId(sourcePackage.Package, packageSupport.Package),
                    _crossDefinitionVersion));
            }

            // get the list of index informations that *target* this version
            List<XverPackageIndexInfo> packageIndexInfos = allPackageIndexInfos.Where(ii => ii.TargetPackageSupport.Package.Key == packageSupport.Package.Key).ToList();

            // build and write the ImplementationGuide resource for the combination package (single source and target)
            {
                string igJson;

                if (packageSupport.Package.FhirVersionShort.StartsWith('4'))
                {
                    igJson = getCombinedIgJsonR4(packageSupport.Package, packageId, internalDependencies, packageIndexInfos);
                }
                else if (packageSupport.Package.FhirVersionShort.StartsWith('5'))
                {
                    igJson = getCombinedIgJsonR5(packageSupport.Package, packageId, internalDependencies, packageIndexInfos);
                }
                else
                {
                    // TODO: Implment DSTU2 and STU3
                    continue;
                }

                string filename = $"ImplementationGuide-{packageId}.json";
                File.WriteAllText(Path.Combine(dir, filename), igJson);
            }

            // build and write the package.manifest.json file
            {
                string pmJson = $$$"""
                    {
                      "version" : "{{{_crossDefinitionVersion}}}",
                      "fhirVersion" : ["{{{packageSupport.Package.PackageVersion}}}"],
                      "date" : "{{{DateTime.Now.ToString("yyyyMMddHHmmss")}}}",
                      "name" : "{{{packageId}}}",
                      "jurisdiction" : "http://unstats.un.org/unsd/methods/m49/m49.htm#001"
                    }
                    """;

                string filename = "package.manifest.json";
                File.WriteAllText(Path.Combine(dir, filename), pmJson);
            }

            // build and write the .index.json file
            {
                PackageContents contentIndex = getPackageContentIndex(packageSupport.Package, packageId, internalDependencies, packageIndexInfos);
                string filename = ".index.json";
                File.WriteAllText(Path.Combine(dir, filename), JsonSerializer.Serialize(contentIndex));
            }

            // build and write the package.json file
            {
                Dictionary<string, string> dependencies = new()
                {
                    { packageSupport.Package.PackageId,  packageSupport.Package.PackageVersion }
                };

                foreach (XverIgDependencyRec xverDependency in _xverDependencies)
                {
                    dependencies.Add(xverDependency.PackageId, xverDependency.PackageVersion);
                }

                foreach ((string depPackageId, string depPackageVersion) in internalDependencies)
                {
                    dependencies.Add(depPackageId, depPackageVersion);
                }

                CachePackageManifest cpm = new()
                {
                    Name = packageId,
                    Version = _crossDefinitionVersion,
                    ToolsVersion = 3,
                    Type = "IG",
                    Date = DateTime.Now.ToString("yyyyMMddHHmmss"),
                    License = EnumUtility.GetLiteral(ImplementationGuide.SPDXLicense.CC01_0), //"CC0-1.0",
                    CanonicalUrl = "http://hl7.org/fhir/uv/xver",
                    WebPublicationUrl = "http://hl7.org/fhir/uv/xver",
                    Title = $"XVer-{packageSupport.Package.ShortName}",
                    Description = $"All Cross Version Extensions for FHIR {packageSupport.Package.ShortName}",
                    Dependencies = dependencies,
                    Author = CommonDefinitions.WorkgroupNames["fhir"],
                    Maintainers = [
                        new()
                        {
                            Name = CommonDefinitions.WorkgroupNames["fhir"],
                            Url = CommonDefinitions.WorkgroupUrls["fhir"],
                        }
                    ],
                    Directories = new()
                    {
                        { "lib", "package" },
                        { "doc", "doc" },
                    },
                    Jurisdiction = "http://unstats.un.org/unsd/methods/m49/m49.htm#001"
                };

                string filename = "package.json";
                File.WriteAllText(Path.Combine(dir, filename), JsonSerializer.Serialize(cpm));
            }
        }
    }



    /// <summary>
    /// Determines whether a file should be skipped when copying from the GitHub repository.
    /// </summary>
    /// <param name="filePath">The relative file path from the repository root.</param>
    /// <returns>True if the file should be skipped, false otherwise.</returns>
    private static bool shouldSkipScriptRepoFile(string filePath)
    {
        // Skip hidden files and directories
        if (filePath.StartsWith(".") || filePath.Contains("/."))
        {
            return true;
        }

        // skip repository README
        if (filePath.Equals("README.md", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Skip common binary file extensions
        string[] skipExtensions = { ".exe", ".dll", ".bin", ".jar", ".zip", ".tar", ".gz", ".7z",
                                   ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg",
                                   ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (skipExtensions.Contains(extension))
        {
            return true;
        }

        // Skip node_modules and other common directories
        string[] skipDirectories = { "node_modules/", ".git/", ".vs/", ".vscode/", "bin/", "obj/" };
        foreach (string skipDir in skipDirectories)
        {
            if (filePath.Contains(skipDir))
            {
                return true;
            }
        }

        // Skip very large files (> 1MB) - these are likely not useful for IG publishing
        // Note: We can't check file size here since we only have the path, 
        // but we'll handle this in the fetch method

        return false;
    }


    /// <summary>
    /// Fetches all files from the HL7 ig-publisher-scripts GitHub repository.
    /// </summary>
    /// <returns>A dictionary where keys are relative file paths and values are file contents.</returns>
    private Dictionary<string, string> getCurrentPublisherScripts()
    {
        if (_config.XverIncludeScripts == false)
        {
            return [];
        }

        const string owner = "HL7";
        const string repo = "ig-publisher-scripts";

        lock (_publisherScriptsLock)
        {
            if (_publisherScripts.Count > 0)
            {
                _logger.LogInformation("Using cached publisher scripts from GitHub repository: {Owner}/{Repo}", owner, repo);
                return _publisherScripts;
            }

            try
            {
                _logger.LogInformation("Fetching files from GitHub repository: {Owner}/{Repo}", owner, repo);

                GitHubClient client = new(new ProductHeaderValue("fhir-codegen"));

                // Get the repository contents recursively
                List<RepositoryContent> contents = getRepositoryContentsRecursive(client, owner, repo);

                foreach (RepositoryContent content in contents)
                {
                    if (content.Type == ContentType.File && !shouldSkipScriptRepoFile(content.Path))
                    {
                        try
                        {
                            // Skip files larger than 1MB
                            if (content.Size > 1024 * 1024)
                            {
                                _logger.LogDebug("Skipping large file: {Path} ({Size} bytes)", content.Path, content.Size);
                                continue;
                            }

                            // Get the file content
                            byte[] fileContent = client.Repository.Content.GetRawContent(owner, repo, content.Path).Result;
                            string contentString = System.Text.Encoding.UTF8.GetString(fileContent);

                            _publisherScripts[content.Path] = contentString;
                            _logger.LogDebug("Fetched file: {Path}", content.Path);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch file content for: {Path}", content.Path);
                        }
                    }
                    else if (content.Type == ContentType.File)
                    {
                        _logger.LogDebug("Skipping file: {Path}", content.Path);
                    }
                }

                _logger.LogInformation("Successfully fetched {Count} files from GitHub repository", _publisherScripts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch files from GitHub repository: {Owner}/{Repo}", owner, repo);
            }
        }
        return _publisherScripts;
    }

    /// <summary>
    /// Recursively gets all contents from a GitHub repository.
    /// </summary>
    private List<RepositoryContent> getRepositoryContentsRecursive(GitHubClient client, string owner, string repo, string? path = null)
    {
        List<RepositoryContent> allContents = [];

        try
        {
            IReadOnlyList<RepositoryContent> contents = path == null
                ? client.Repository.Content.GetAllContents(owner, repo).Result
                : client.Repository.Content.GetAllContents(owner, repo, path).Result;

            foreach (var content in contents)
            {
                if (content.Type == ContentType.File)
                {
                    allContents.Add(content);
                }
                else if (content.Type == ContentType.Dir && !shouldSkipScriptRepoFile(content.Path))
                {
                    // Recursively get directory contents
                    List<RepositoryContent> subContents = getRepositoryContentsRecursive(client, owner, repo, content.Path);
                    allContents.AddRange(subContents);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get repository contents for path: {Path}", path);
        }

        return allContents;
    }

    private string createExportPackageDir(string fhirDir, DbFhirPackage? sourcePackage, DbFhirPackage targetPackage)
    {
        if (!Directory.Exists(fhirDir))
        {
            Directory.CreateDirectory(fhirDir);
        }

        string packageId = getPackageId(sourcePackage, targetPackage);

        string dir = Path.Combine(fhirDir, packageId);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (_config.XverExportForPublisher)
        {
            string inputDir = Path.Combine(dir, "input");
            if (!Directory.Exists(inputDir))
            {
                Directory.CreateDirectory(inputDir);
            }

            //string fshDir = Contents.Combine(inputDir, "fsh");
            //if (!Directory.Exists(fshDir))
            //{
            //    Directory.CreateDirectory(fshDir);
            //}

            string includesDir = Path.Combine(inputDir, "includes");
            if (!Directory.Exists(includesDir))
            {
                Directory.CreateDirectory(includesDir);
            }

            string pagesDir = Path.Combine(inputDir, "pagecontent");
            if (!Directory.Exists(pagesDir))
            {
                Directory.CreateDirectory(pagesDir);
            }

            string extensionsDir = Path.Combine(inputDir, "extensions");
            if (!Directory.Exists(extensionsDir))
            {
                Directory.CreateDirectory(extensionsDir);
            }

            string profilesDir = Path.Combine(inputDir, "profiles");
            if (!Directory.Exists(profilesDir))
            {
                Directory.CreateDirectory(profilesDir);
            }

            string resourcesDir = Path.Combine(inputDir, "resources");
            if (!Directory.Exists(resourcesDir))
            {
                Directory.CreateDirectory(resourcesDir);
            }

            string vocabDir = Path.Combine(inputDir, "vocabulary");
            if (!Directory.Exists(vocabDir))
            {
                Directory.CreateDirectory(vocabDir);
            }
        }
        else
        {
            string packageSubDir = Path.Combine(dir, "package");
            if (!Directory.Exists(packageSubDir))
            {
                Directory.CreateDirectory(packageSubDir);
            }
        }

        return dir;
    }

    private List<XVerIgFileRecord> buildExtensionFileList(
        int targetPackageKey,
        Dictionary<(int sourceElementKey, int targetPackageId), (StructureDefinition, DbExtensionSubstitution?)> xverExtensions)
    {
        List<XVerIgFileRecord> fileRecords = [];

        foreach (((int sourceElementKey, int targetPackageId), (StructureDefinition sd, DbExtensionSubstitution? extensionSubstitution)) in xverExtensions)
        {
            if (targetPackageId != targetPackageKey)
            {
                continue;
            }

            if (extensionSubstitution != null)
            {
                continue;
            }

            fileRecords.Add(new()
            {
                FileName = $"StructureDefinition-{sd.Id}.json",
                FileNameWithoutExtension = $"StructureDefinition-{sd.Id}",
                IsPageContentFile = false,
                Name = sd.Name,
                Id = sd.Id,
                Url = sd.Url,
                ResourceType = "StructureDefinition",
                Version = sd.Version ?? _crossDefinitionVersion,
                Description = sd.Description,
                KindValue = "complex-type",
                TypeValue = "Extension",
                DerivationValue = "constraint",
            });
        }

        return fileRecords;
    }

    private List<XVerIgFileRecord> buildProfileFileList(
        int targetPackageKey,
        Dictionary<(int sourceStructureKey, int targetPackageId), StructureDefinition> xverProfiles)
    {
        List<XVerIgFileRecord> fileRecords = [];

        foreach (((int sourceStructureKey, int targetPackageId), StructureDefinition sd) in xverProfiles)
        {
            if (targetPackageId != targetPackageKey)
            {
                continue;
            }
            fileRecords.Add(new()
            {
                FileName = $"StructureDefinition-{sd.Id}.json",
                FileNameWithoutExtension = $"StructureDefinition-{sd.Id}",
                IsPageContentFile = false,
                Name = sd.Name,
                Id = sd.Id,
                Url = sd.Url,
                ResourceType = "StructureDefinition",
                Version = sd.Version ?? _crossDefinitionVersion,
                Description = sd.Description,
                KindValue = "resource",
                TypeValue = sd.Type,
                DerivationValue = "constraint",
            });
        }

        return fileRecords;
    }

    private List<XVerIgFileRecord> buildCodeSystemFileList(
        int targetPackageKey,
        string targetPackageNpmId,
        Dictionary<(int sourceCsKey, int targetPackageId), (CodeSystem? cs, int? externalInclusionKey)> xverCodeSystems)
    {
        List<XVerIgFileRecord> fileRecords = [];

        // add external inclusion code systems
        List<DbExternalInclusion> externalInclusions = DbExternalInclusion.SelectList(
            _db!.DbConnection,
            ResourceType: Hl7.Fhir.Model.FHIRAllTypes.CodeSystem);

        foreach (DbExternalInclusion inclusion in externalInclusions)
        {
            // check to see if we should be including this
            if ((inclusion.IncludeInPackages == null) ||
                inclusion.GetIncludeInPackagesList().Contains(targetPackageNpmId, StringComparer.OrdinalIgnoreCase))
            {
                fileRecords.Add(new()
                {
                    FileName = $"CodeSystem-{inclusion.Id}.json",
                    FileNameWithoutExtension = $"CodeSystem-{inclusion.Id}",
                    IsPageContentFile = false,
                    Name = inclusion.Name,
                    Id = inclusion.Id,
                    Url = inclusion.UnversionedUrl,
                    ResourceType = "CodeSystem",
                    Version = inclusion.Version ?? _crossDefinitionVersion,
                    Description = $"Included external CodeSystem: '{inclusion.UnversionedUrl}|{inclusion.Version}'",
                });
            }
        }

        HashSet<string> processedIds = [];

        foreach (((int sourceCsKey, int targetPackageId), (CodeSystem? cs, int? externalInclusionKey)) in xverCodeSystems)
        {
            if (targetPackageId != targetPackageKey)
            {
                continue;
            }

            // external inclusions are already handled
            if (cs is null)
            {
                continue;
            }

            if (!processedIds.Add(cs.Id))
            {
                // already processed this one
                continue;
            }

            fileRecords.Add(new()
            {
                FileName = $"CodeSystem-{cs.Id}.json",
                FileNameWithoutExtension = $"CodeSystem-{cs.Id}",
                IsPageContentFile = false,
                Name = cs.Name,
                Id = cs.Id,
                Url = cs.Url,
                ResourceType = "CodeSystem",
                Version = cs.Version ?? _crossDefinitionVersion,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(cs.Description ?? cs.Title ?? $"CodeSystem: '{cs.Url}|{cs.Version}'"),
            });
        }

        return fileRecords;
    }

    private List<XVerIgFileRecord> buildValueSetFileList(
        int targetPackageKey,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets)
    {
        List<XVerIgFileRecord> fileRecords = [];
        HashSet<string> processedIds = [];

        // build the list of value sets we are defining
        foreach (((int sourceElementKey, int targetPackageId), ValueSet vs) in xverValueSets)
        {
            if (targetPackageId != targetPackageKey)
            {
                continue;
            }

            if (!processedIds.Add(vs.Id))
            {
                // already processed this one
                continue;
            }

            fileRecords.Add(new()
            {
                FileName = $"ValueSet-{vs.Id}.json",
                FileNameWithoutExtension = $"ValueSet-{vs.Id}",
                IsPageContentFile = false,
                Name = vs.Name,
                Id = vs.Id,
                Url = vs.Url,
                ResourceType = "ValueSet",
                Version = vs.Version ?? _crossDefinitionVersion,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(vs.Description ?? vs.Title ?? $"ValueSet: '{vs.Url}|{vs.Version}'"),
                HasExpansion = (vs.Expansion != null && vs.Expansion.Contains != null && vs.Expansion.Contains.Count > 0),
            });
        }

        return fileRecords;
    }

    /// <summary>
    /// Generates the .index.json content for a cross-version package, listing all FHIR package contents
    /// defined for a specific source-target package combination.
    /// </summary>
    /// <param name="sourcePackage">The source <see cref="DbFhirPackage"/> for the cross-version package.</param>
    /// <param name="targetPackage">The target <see cref="DbFhirPackage"/> for the cross-version package.</param>
    /// <param name="xverValueSets">A dictionary of cross-version <see cref="ValueSet"/>s, keyed by (source ValueSet key, target package id).</param>
    /// <param name="xverExtensions">A dictionary of cross-version <see cref="StructureDefinition"/>s (extensions), keyed by (source element key, target package id).</param>
    /// <param name="indexInfo">The <see cref="XverPackageIndexInfo"/> object to populate with index entries.</param>
    /// <returns>A JSON string representing the .index.json file for the cross-version package.</returns>
    [Obsolete("Switched to IgExporter")]
    private PackageContents getPackageContentIndex(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets,
        Dictionary<(int sourceCsKey, int targetPackageId), (CodeSystem? cs, int? externalInclusionKey)> xverCodeSystems,
        Dictionary<(int sourceElementKey, int targetPackageId), (StructureDefinition, DbExtensionSubstitution?)> xverExtensions,
        Dictionary<(int sourceStructureKey, int targetPackageId), StructureDefinition> xverProfiles,
        XverPackageIndexInfo indexInfo)
    {
        if (indexInfo.ExtensionFiles.Count == 0)
        {
            indexInfo.ExtensionFiles = buildExtensionFileList(targetPackage.Key, xverExtensions);
        }

        if (indexInfo.ProfileFiles.Count == 0)
        {
            indexInfo.ProfileFiles = buildProfileFileList(targetPackage.Key, xverProfiles);
        }

        if (indexInfo.CodeSystemFiles.Count == 0)
        {
            indexInfo.CodeSystemFiles = buildCodeSystemFileList(targetPackage.Key, targetPackage.NpmId, xverCodeSystems);
        }

        if (indexInfo.ValueSetFiles.Count == 0)
        {
            indexInfo.ValueSetFiles = buildValueSetFileList(targetPackage.Key, xverValueSets);
        }

        // add our ImplementationGuide file
        indexInfo.IgIndexFile ??= new()
        {
            FileName = $"ImplementationGuide-{indexInfo.PackageId}.json",
            FileNameWithoutExtension = $"ImplementationGuide-{indexInfo.PackageId}",
            IsPageContentFile = false,
            Name = FhirSanitizationUtils.ReformatIdForName(indexInfo.PackageId),
            Id = indexInfo.PackageId,
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{indexInfo.PackageId}",
            ResourceType = "ImplementationGuide",
            Version = _crossDefinitionVersion,
            Description = $"FHIR Cross-Version Extensions package for FHIR {targetPackage.ShortName} from FHIR {sourcePackage.ShortName}",
        };

        return indexInfo.AsPackageContents();
    }


    /// <summary>
    /// Generates the .index.json content for a cross-version package, listing all FHIR package contents
    /// defined for a specific source-target package combination.
    /// </summary>
    /// <param name="package">The <see cref="DbFhirPackage"/> representing the package for which the index is generated.</param>
    /// <param name="packageId">The unique package identifier for this cross-version package.</param>
    /// <param name="internalDependencies">A list of internal package dependencies, each as a tuple of package ID and version.</param>
    /// <param name="targetInfos">A list of <see cref="XverPackageIndexInfo"/> objects containing index information for each target package.</param>
    /// <returns>A JSON string representing the .index.json file for the cross-version package.</returns>

    private PackageContents getPackageContentIndex(
        DbFhirPackage package,
        string packageId,
        List<(string packageId, string packageVersion)> internalDependencies,
        List<XverPackageIndexInfo> targetInfos)
    {
        // add our ImplementationGuide file
        PackageContents.PackageFile igIndexFile = new()
        {
            FileName = $"ImplementationGuide-{packageId}.json",
            ResourceType = "ImplementationGuide",
            Id = packageId,
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{packageId}",
            Version = _crossDefinitionVersion,
        };

        return new PackageContents
        {
            IndexVersion = 2,
            Files = [igIndexFile],
        };
    }

    [Obsolete("Switched to IgExporter")]
    private string getIgJsonR5(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        XverPackageIndexInfo indexInfo)
    {
        List<ImplementationGuide.DependsOnComponent> deps = _xverDependencies
            .Where(d => d.NeededForPublisher)
            .Select(d => d.AsIgDependsOn(targetPackage.DefinitionFhirSequence))
            .ToList();

        ImplementationGuide.PageComponent lookupPage = new()
        {
            Source = new FhirUrl("lookup.html"),
            Name = "lookup.html",
            Title = "Artifact Lookup",
            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            Page = [],
        };

        foreach (XVerIgFileRecord fileRec in indexInfo.ResourceLookupFiles)
        {
            lookupPage.Page.Add(new()
            {
                Source = new FhirUrl(fileRec.FileName),
                Name = $"{fileRec.FileNameWithoutExtension}.html",
                Title = $"Lookup for {fileRec.Name}",
                Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            });
        }

        ImplementationGuide.PageComponent igPage = new()
        {
            Source = new FhirUrl("index.html"),
            Name = "index.html",
            Title = "Home",
            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            Page = [
                lookupPage,
                new()
                {
                    Source = new FhirUrl("downloads.html"),
                    Name = "downloads.html",
                    Title = "Downloads",
                    Generation = ImplementationGuide.GuidePageGeneration.Markdown,

                },
                new()
                {
                    Source = new FhirUrl("changelog.html"),
                    Name = "changelog.html",
                    Title = "Change Log",
                    Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                }
            ],
        };

        List<ImplementationGuide.ParameterComponent> igParams = _xverIgParameters
            .Select(cv => new ImplementationGuide.ParameterComponent
            {
                Code = new Coding()
                {
                    System = "http://hl7.org/fhir/tools/CodeSystem/ig-parameters",
                    Code = cv.code,
                },
                Value = cv.value,
            })
            .ToList();

        ImplementationGuide ig = new()
        {
            Id = indexInfo.PackageId,
            Extension = [
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                    Value = new Code("trial-use"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                    Value = new Code("fhir"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                    Value = new Integer(0),
                }
            ],
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{indexInfo.PackageId}",
            Version = _crossDefinitionVersion,
            Name = FhirSanitizationUtils.ReformatIdForName(indexInfo.PackageId),
            Title = $"FHIR Cross-Version Extensions package for FHIR {targetPackage.ShortName} from FHIR {sourcePackage.ShortName}",
            Status = PublicationStatus.Active,
            Date = _runTime.ToString("O"),
            Publisher = CommonDefinitions.WorkgroupNames["fhir"],
            Contact = [
                new()
                {
                    Name = CommonDefinitions.WorkgroupNames["fhir"],
                    Telecom = [
                        new()
                        {
                            System = ContactPoint.ContactPointSystem.Url,
                            Value = CommonDefinitions.WorkgroupUrls["fhir"],
                        },
                    ],
                }
            ],
            Description = $"Cross Version Extensions for using FHIR {sourcePackage.ShortName} in FHIR {targetPackage.ShortName}",
            Jurisdiction = [
                new()
                {
                    Coding = [
                        new()
                        {
                            System = "http://unstats.un.org/unsd/methods/m49/m49.htm",
                            Code = "001",
                            Display = "World",
                        }
                    ],
                }
            ],
            PackageId = indexInfo.PackageId,
            License = ImplementationGuide.SPDXLicense.CC01_0,
            FhirVersion = [FHIRVersion.N5_0_0],
            DependsOn = deps,
            Definition = new()
            {
                Resource = [],
                Page = igPage,
                Parameter = igParams,
            }
        };

        // add our extensions
        foreach (XVerIgFileRecord fileRec in indexInfo.ExtensionFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"StructureDefinition/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("StructureDefinition:extension"),
                    },
                ],
            });
        }

        // add our profiles
        foreach (XVerIgFileRecord fileRec in indexInfo.ProfileFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"StructureDefinition/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("StructureDefinition:profile"),
                    },
                ],
            });
        }

        // add our code systems
        foreach (XVerIgFileRecord fileRec in indexInfo.CodeSystemFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"CodeSystem/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("CodeSystem"),
                    },
                ],
            });
        }

        // add our value sets
        foreach (XVerIgFileRecord fileRec in indexInfo.ValueSetFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"ValueSet/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("ValueSet"),
                    },
                ],
            });
        }

        return ig.ToJson(new FhirJsonSerializationSettings() { Pretty = true });
    }


    private string getCombinedIgJsonR5(
        DbFhirPackage package,
        string packageId,
        List<(string packageId, string packageVersion)> internalDependencies,
        List<XverPackageIndexInfo> targetInfos)
    {
        ImplementationGuide ig = new()
        {
            Id = "ImplementationGuide-" + packageId,
            Extension = [
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                    Value = new Code("trial-use"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                    Value = new Code("fhir"),
                }
            ],
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{packageId}",
            Version = _crossDefinitionVersion,
            Name = FhirSanitizationUtils.ReformatIdForName(packageId),
            Title = $"All FHIR Cross-Version Extensions for FHIR {package.ShortName}",
            Status = PublicationStatus.Active,
            Date = _runTime.ToString("O"),
            Publisher = CommonDefinitions.WorkgroupNames["fhir"],
            Contact = [
                new()
                {
                    Name = CommonDefinitions.WorkgroupNames["fhir"],
                    Telecom = [
                        new()
                        {
                            System = ContactPoint.ContactPointSystem.Url,
                            Value = CommonDefinitions.WorkgroupUrls["fhir"],
                        },
                    ],
                }
            ],
            Description = $"All Cross Version Extensions for FHIR {package.ShortName}",
            Jurisdiction = [
                new()
                {
                    Coding = [
                        new()
                        {
                            System = "http://unstats.un.org/unsd/methods/m49/m49.htm",
                            Code = "001",
                            Display = "World",
                        }
                    ],
                }
            ],
            PackageId = packageId,
            License = ImplementationGuide.SPDXLicense.CC01_0,
            FhirVersion = [FHIRVersion.N5_0_0],
            DependsOn = _xverDependencies.Select(xd => xd.AsIgDependsOn(FhirReleases.FhirSequenceCodes.R5)).ToList(),
            Definition = new()
            {
                Page = new ImplementationGuide.PageComponent
                {
                    Source = new FhirUrl("index.html"),
                    Name = "index.html",
                    Title = "Home",
                    Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                    Page = [
                        new()
                        {
                            Source = new FhirUrl("downloads.html"),
                            Name = "downloads.html",
                            Title = "Downloads",
                            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                        },
                        new()
                        {
                            Source = new FhirUrl("changelog.html"),
                            Name = "changelog.html",
                            Title = "Change Log",
                            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                        }
                        ],
                },
                Resource = [],
            }
        };

        foreach ((string depPackageId, string depPackageVersion) in internalDependencies)
        {
            ig.DependsOn.Add(new()
            {
                ElementId = depPackageId.Replace('.', '_'),
                Uri = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{depPackageId}",
                PackageId = depPackageId,
                Version = depPackageVersion
            });
        }

        return ig.ToJson(new FhirJsonSerializationSettings() { Pretty = true });
    }
    private static string buildMdPageJson(string filenameWithoutExtension, string title) => $$$"""
        { "sourceUrl" : "{{{filenameWithoutExtension}}}.md", "name" : "{{{filenameWithoutExtension}}}.html", "title" : "{{{title}}}", "generation" : "markdown" }
        """;

    [Obsolete("Switched to IgExporter")]
    private string getIgJsonR4(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        XverPackageIndexInfo indexInfo)
    {
        List<string> resourceDefinitions = [];

        // process extensions
        foreach (XVerIgFileRecord fileRec in indexInfo.ExtensionFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "StructureDefinition:extension"
                        }],
                        "reference" : {
                            "reference" : "StructureDefinition/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process profiles
        foreach (XVerIgFileRecord fileRec in indexInfo.ProfileFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "StructureDefinition:profile"
                        }],
                        "reference" : {
                            "reference" : "StructureDefinition/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process code systems
        foreach (XVerIgFileRecord fileRec in indexInfo.CodeSystemFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "CodeSystem"
                        }],
                        "reference" : {
                            "reference" : "CodeSystem/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process value sets
        foreach (XVerIgFileRecord fileRec in indexInfo.ValueSetFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "ValueSet"
                        }],
                        "reference" : {
                            "reference" : "ValueSet/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        StringBuilder pageBuilder = new();
        pageBuilder.AppendLine(""" "page" : """);
        pageBuilder.AppendLine("""  { "nameUrl" : "index.html", "title" : "Home", "generation" : "markdown" , "page" : [ """);

        pageBuilder.AppendLine("""  { "nameUrl" : "lookup.html", "title" : "Artifact Lookup", "generation" : "markdown" , "page" : [ """);
        List<string> lookupPages = [];
        foreach (XVerIgFileRecord fileRec in indexInfo.ResourceLookupFiles)
        {
            lookupPages.Add($$$"""    { "nameUrl" : "{{{fileRec.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{fileRec.Name}}}", "generation" : "markdown" }""");
        }
        pageBuilder.AppendLine(string.Join(",\n", lookupPages));
        pageBuilder.AppendLine("""]},""");  // close lookup

        pageBuilder.AppendLine("""  { "nameUrl" : "downloads.html", "title" : "Downloads", "generation" : "markdown" },""");
        pageBuilder.AppendLine("""  { "nameUrl" : "changelog.html", "title" : "Change Log", "generation" : "markdown" }""");

        pageBuilder.AppendLine("""]},""");  // close index

        string igParams = string.Join(",\n", _xverIgParameters.Select(cv =>
            $$$"""    { "code" : "{{{cv.code}}}", "value" : "{{{cv.value}}}" }"""));

        List<string> deps = _xverDependencies.Where(d => d.NeededForPublisher).Select(d => d.AsJsonIgDependency(targetPackage.DefinitionFhirSequence)).ToList();

        string dependencies = deps.Count == 0
            ? string.Empty
            : $$$"""
                "dependsOn" : [
                {{{string.Join(",\n", deps)}}}
                ],
                """;
        
        string igJson = $$$"""
            {
              "resourceType" : "ImplementationGuide",
              "id" : "{{{indexInfo.PackageId}}}",
              "extension" : [{
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                "valueCode" : "trial-use"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                "valueCode" : "fhir"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                "valueInteger" : 0
              }],
              "url" : "http://hl7.org/fhir/uv/xver/ImplementationGuide/{{{indexInfo.PackageId}}}",
              "version" : "{{{_crossDefinitionVersion}}}",
              "name" : "{{{FhirSanitizationUtils.ReformatIdForName(indexInfo.PackageId)}}}",
              "title" : "FHIR Cross-Version Extensions package for FHIR {{{targetPackage.ShortName}}} from FHIR {{{sourcePackage.ShortName}}}",
              "status" : "active",
              "date" : "{{{_runTime.ToString("O")}}}",
              "publisher" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
              "contact" : [{
                "name" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
                "telecom" : [{
                  "system" : "url",
                  "value" : "{{{CommonDefinitions.WorkgroupUrls["fhir"]}}}"
                }]
              }],
              "description" : "Cross Version Extensions for using FHIR {{{sourcePackage.ShortName}}} in FHIR {{{targetPackage.ShortName}}}",
              "jurisdiction" : [{
                "coding" : [{
                  "system" : "http://unstats.un.org/unsd/methods/m49/m49.htm",
                  "code" : "001",
                  "display" : "World"
                }]
              }],
              "packageId" : "{{{indexInfo.PackageId}}}",
              "license" : "{{{EnumUtility.GetLiteral(ImplementationGuide.SPDXLicense.CC01_0)}}}",
              "fhirVersion" : ["{{{targetPackage.PackageVersion}}}"],
              {{{dependencies}}}
              "definition" : {
                {{{pageBuilder.ToString()}}}
                "resource" : [
                {{{string.Join("\n,  ", resourceDefinitions)}}}
                ],
                "parameter" : [
                {{{igParams}}}
                ]
              }
            }
            """;

        return igJson;
    }

    private string getCombinedIgJsonR4(
        DbFhirPackage package,
        string packageId,
        List<(string packageId, string packageVersion)> internalDependencies,
        List<XverPackageIndexInfo> targetInfos)
    {
        string packageSuffix = package.ShortName.ToLowerInvariant();

        // TODO: hl7.fhir.uv.tools does not output an R4B package as of 0.8.0, remove this once it does
        string toolsPackageSuffix = package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R4B
            ? "r4"
            : package.ShortName.ToLowerInvariant();

        List<string> deps = _xverDependencies.Where(d => d.NeededForPublisher).Select(d => d.AsJsonIgDependency(package.DefinitionFhirSequence)).ToList();

        deps.AddRange(
            internalDependencies.Select(pi => $$$"""
                {
                    "id" : "{{{pi.packageId.Replace('.', '_')}}}",
                    "uri" : "http://hl7.org/fhir/uv/xver/ImplementationGuide/{{{packageId}}}",
                    "packageId" : "{{{pi.packageId}}}",
                    "version" : "{{{pi.packageVersion}}}"
                }
            """));

        string dependencies = deps.Count == 0
            ? string.Empty
            : $$$"""
                "dependsOn" : [
                {{{string.Join(",\n", deps)}}}
                ],
                """;

        string resources;
        resources = string.Empty;

        StringBuilder pageBuilder = new();
        pageBuilder.AppendLine(""" "page" : """);
        pageBuilder.AppendLine("""  { "nameUrl" : "index.html", "title" : "Home", "generation" : "markdown" , "page" : [ """);

        //pageBuilder.AppendLine("""  { "sourceUrl" : "lookup.md", "name" : "lookup.html", "title" : "Artifact Lookup", "generation" : "markdown" , "page" : [ """);
        //foreach (XVerIgFileRecord fileRec in indexInfo.ResourceLookupFiles)
        //{
        //    pageBuilder.AppendLine($$$"""    { "sourceUrl" : "{{{fileRec.FileName}}}", "name" : "{{{fileRec.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{fileRec.Name}}}", "generation" : "markdown" }""");
        //}
        //pageBuilder.AppendLine("""]},""");  // close lookup

        pageBuilder.AppendLine("""  { "nameUrl" : "downloads.html", "title" : "Downloads", "generation" : "markdown" }""");
        pageBuilder.AppendLine("""  { "nameUrl" : "changelog.html", "title" : "Change Log", "generation" : "markdown" }""");

        pageBuilder.AppendLine("""]},""");  // close index

        string igParams = string.Join(",\n", _xverIgParameters.Select(cv =>
            $$$"""    { "code" : "{{{cv.code}}}", "value" : "{{{cv.value}}}" }"""));

        string igJson = $$$"""
            {
              "resourceType" : "ImplementationGuide",
              "id" : "{{{packageId}}}",
              "extension" : [{
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                "valueCode" : "trial-use"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                "valueCode" : "fhir"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                "valueInteger" : 0
              }],
              "url" : "http://hl7.org/fhir/uv/xver/ImplementationGuide/{{{packageId}}}",
              "version" : "{{{_crossDefinitionVersion}}}",
              "name" : "{{{FhirSanitizationUtils.ReformatIdForName(packageId)}}}",
              "title" : "All FHIR Cross-Version Extensions for FHIR {{{package.ShortName}}}",
              "status" : "active",
              "date" : "{{{_runTime.ToString("O")}}}",
              "publisher" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
              "contact" : [{
                "name" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
                "telecom" : [{
                  "system" : "url",
                  "value" : "{{{CommonDefinitions.WorkgroupUrls["fhir"]}}}"
                }]
              }],
              "description" : "All Cross Version Extensions for for FHIR {{{package.ShortName}}}",
              "jurisdiction" : [{
                "coding" : [{
                  "system" : "http://unstats.un.org/unsd/methods/m49/m49.htm",
                  "code" : "001",
                  "display" : "World"
                }]
              }],
              "packageId" : "{{{packageId}}}",
              "license" : "{{{EnumUtility.GetLiteral(ImplementationGuide.SPDXLicense.CC01_0)}}}",
              "fhirVersion" : ["{{{package.PackageVersion}}}"],
              "definition" : {
                {{{pageBuilder.ToString()}}}
                {{{resources}}}
                "parameter" : [
                {{{igParams}}}
                ]
              }
            }
            """;

        return igJson;
    }

}
