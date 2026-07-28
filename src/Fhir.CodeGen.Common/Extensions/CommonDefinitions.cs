// <copyright file="CommonDefinitions.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

namespace Fhir.CodeGen.Common.FhirExtensions;

/// <summary>A common definitions.</summary>
public static class CommonDefinitions
{
    public const string CanonicalTooling = "http://hl7.org/fhir/tools/ImplementationGuide/hl7.fhir.uv.tools";
    public const string CanonicalExtensionsPack = "http://hl7.org/fhir/extensions/ImplementationGuide/hl7.fhir.uv.extensions";

    public const string ExtensionComment = "There can be no stigma associated with the use of extensions by any application, project, or standard - regardless of the institution or jurisdiction that uses or defines the extensions.  The use of extensions is what allows the FHIR specification to retain a core level of simplicity for everyone.";
    public const string ExtensionDefinition = "May be used to represent additional information that is not part of the basic definition of the element. To make the use of extensions safe and manageable, there is a strict set of governance  applied to the definition and use of extensions. Though any implementer can define an extension, there is a set of requirements that SHALL be met as part of the definition of the extension.";
    public const string ExtensionShort = "Additional content defined by implementations";

    public const string ExtUrlStandardStatus = "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status";
    public const string ExtUrlFmm = "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm";
    public const string ExtUrlWorkGroup = "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg";
    public const string ExtUrlCategory = "http://hl7.org/fhir/StructureDefinition/structuredefinition-category";

    public const string ExtUrlIsInterface = "http://hl7.org/fhir/StructureDefinition/structuredefinition-interface";
    public const string ExtUrlImplements = "http://hl7.org/fhir/StructureDefinition/structuredefinition-implements";

    public const string ExtUrlBestPractice = "http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice";
    public const string ExtUrlBestPracticeExplanation = "http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice-explanation";

    public const string ExtUrlCapExpectation = "http://hl7.org/fhir/StructureDefinition/capabilitystatement-expectation";
    public const string ExtUrlCapSearchParamCombo = "http://hl7.org/fhir/StructureDefinition/capabilitystatement-search-parameter-combination";

    public const string ExtUrlPackageSource = "http://hl7.org/fhir/StructureDefinition/package-source";

    public const string ExtUrlSdRegex = "http://hl7.org/fhir/StructureDefinition/regex";
    public const string ExtUrlSdRegex2 = "http://hl7.org/fhir/StructureDefinition/structuredefinition-regex";

    public const string ExtUrlFhirType = "http://hl7.org/fhir/StructureDefinition/structuredefinition-fhir-type";

    public const string ExtUrlJsonType = "http://hl7.org/fhir/StructureDefinition/structuredefinition-json-type";
    public const string ExtUrlXmlType = "http://hl7.org/fhir/StructureDefinition/structuredefinition-xml-type";
    public const string ExtUrlRdfType = "http://hl7.org/fhir/StructureDefinition/structuredefinition-rdf-type";

    public const string ExtUrlExplicitTypeName = "http://hl7.org/fhir/StructureDefinition/structuredefinition-explicit-type-name";

    public const string ExtUrlBindingName = "http://hl7.org/fhir/StructureDefinition/elementdefinition-bindingName";
    public const string ExtUrlIsCommonBinding = "http://hl7.org/fhir/StructureDefinition/elementdefinition-isCommonBinding";

    public const string ExtUrlMinValueSet = "http://hl7.org/fhir/StructureDefinition/elementdefinition-minValueSet";
    public const string ExtUrlMaxValueSet = "http://hl7.org/fhir/StructureDefinition/elementdefinition-maxValueSet";

    //public const string ExtUrlEdFieldOrder = "http://ginoc.io/fhir/StructureDefinition/elementdefinition-fieldOrder";
    //public const string ExtUrlEdComponentFieldOrder = "http://ginoc.io/fhir/StructureDefinition/elementdefinition-componentFieldOrder";
    public const string ExtUrlSpXPath = "http://hl7.org/fhir/1.0/StructureDefinition/extension-SearchParameter.xpath";
    public const string ExtUrlSpXPathUsage = "http://hl7.org/fhir/1.0/StructureDefinition/extension-SearchParameter.xpathUsage";

    public const string ExtUrlEdProfileElement = "http://hl7.org/fhir/StructureDefinition/elementdefinition-profile-element";

    public const string FhirStructureUrlPrefix = "http://hl7.org/fhir/StructureDefinition/";
    public const string FhirUrlPrefix = "http://hl7.org/fhir/";
    public const string FhirPathUrlPrefix = "http://hl7.org/fhirpath/";
    public const string THOCsUrlPrefix = "http://terminology.hl7.org/CodeSystem/";
    public const string THOUrlPrefix = "http://terminology.hl7.org/";

    public const string ExtUrlSmartOAuth = "http://fhir-registry.smarthealthit.org/StructureDefinition/oauth-uris";

    public const string ExtUrlConceptMapAdditionalUrls = "http://ginoc.io/fhir/StructureDefinition/conceptmap-additional-urls";
    public const string ExtUrlConceptMapAggregateRelationship = "http://ginoc.io/fhir/StructureDefinition/conceptmap-aggregate-relationship";

    public const string ExtUrlSearchParameterBaseType = "http://hl7.org/fhir/tools/StructureDefinition/searchparameter-base-type";
    public const string ExtUrlOperationDefinitionAllowedType = "http://hl7.org/fhir/StructureDefinition/operationdefinition-allowed-type";

    public const string ExtUrlVersionSpecificUse = "http://hl7.org/fhir/StructureDefinition/version-specific-use";
    public const string ExtUrlVersionSpecificUseStart = "startFhirVersion";
    public const string ExtUrlVersionSpecificUseEnd = "endFhirVersion";

    public const string ExtUrlAlternateCanonical = "http://hl7.org/fhir/StructureDefinition/alternate-canonical";
    public const string ExtUrlAlternateReference = "http://hl7.org/fhir/StructureDefinition/alternate-reference";

    public const string ConceptMapPropertiesSystem = "http://hl7.org/fhir/uv/xver/CodeSystem/xver-conceptmap-properties";

    /// <summary>
    /// Boolean value that indicates whether the cardinality of the source element collapses when mapped to
    /// the target element. E.g., 0..* string values can be concatenated into a single string value.
    /// </summary>
    public const string ConceptMapPropertyCardinalityCollapses = "cardinality-collapses";

    /// <summary>
    /// String value that provides guidance or notes about how the mapping should be interpreted or applied.
    /// </summary>
    public const string ConceptMapPropertyMappingGuidance = "mapping-guidance";

    //public const string ConceptMapPropertyGenerated = "cg-generated";
    //public const string ConceptMapPropertyNeedsReview = "cg-needs-review";
    //public const string ConceptMapPropertyValueDomainRelationship = "value-domain-relationship";
    //public const string ConceptMapPropertyConceptDomainRelationship = "concept-domain-relationship";

    public const string ConceptMapUsageContextSystem = "http://ginoc.io/fhir/CodeSystem/conceptmap-usage-context";
    public const string ConceptMapUsageContextTarget = "Target";
    public const string ConceptMapUsageContextValueSet = "ValueSet";
    public const string ConceptMapUsageContextTypeOverview = "Types";
    public const string ConceptMapUsageContextDataType = "DataType";
    public const string ConceptMapUsageContextResourceOverview = "Resources";
    public const string ConceptMapUsageContextResource = "Resource";
    public const string ConceptMapUsageContextElements = "Elements";


    public const string ExtUrlComment = "http://ginoc.io/fhir/StructureDefinition/comment";

    public static string ResolveWorkgroup(string? value, string defaultValue = "fhir")
    {
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        if (WorkgroupReplacement.TryGetValue(value!, out string? replacement))
        {
            return replacement;
        }

        if (WorkgroupUrls.ContainsKey(value!))
        {
            return value!;
        }

        return defaultValue;
    }

    /// <summary>
    /// Resolve disbanded (but listed) HL7 Workgroup names to their replacements.
    /// </summary>
    public static readonly Dictionary<string, string> WorkgroupReplacement = new(StringComparer.OrdinalIgnoreCase)
    {
        { "rcrim", "brr" },
        { "mnm", "fhir" },
        { "ti", "vocab" },
    };

    /// <summary>
    /// Resolve an HL7 Workgroup name to its URL.
    /// </summary>
    /// <remarks>
    /// Pulled from:
    ///     - https://github.com/hapifhir/org.hl7.fhir.core/blob/master/org.hl7.fhir.utilities/src/main/java/org/hl7/fhir/utilities/HL7WorkGroups.java
    ///     - on 2025.08.18
    /// </remarks>
    public static readonly Dictionary<string, string> WorkgroupUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        { "aid", "http://www.hl7.org/Special/committees/java" },
        { "arden", "http://www.hl7.org/Special/committees/arden" },
        { "brr", "http://www.hl7.org/Special/committees/rcrim" },
        { "cbcc", "http://www.hl7.org/Special/committees/cbcc" },
        { "cdamg", "http://www.hl7.org/Special/committees/cdamg" },
        { "cds", "http://www.hl7.org/Special/committees/dss" },
        { "cg", "http://www.hl7.org/Special/committees/clingenomics" },
        { "cgp", "http://www.hl7.org/Special/committees/cgp" },
        { "cic", "http://www.hl7.org/Special/committees/cic" },
        { "cimi", "http://www.hl7.org/Special/committees/cimi" },
        { "claims", "http://www.hl7.org/Special/committees/claims" },
        { "cqi", "http://www.hl7.org/Special/committees/cqi" },
        { "dev", "http://www.hl7.org/Special/committees/healthcaredevices" },
        { "ehr", "http://www.hl7.org/Special/committees/ehr" },
        { "ec", "http://www.hl7.org/Special/committees/emergencycare" },
        { "fhir", "http://www.hl7.org/Special/committees/fiwg" },
        { "fmg", "http://www.hl7.org/Special/committees/fhirmg" },
        { "fm", "http://www.hl7.org/Special/committees/fm" },
        { "hsi", "http://www.hl7.org/Special/committees/hsi" },
        { "hsswg", "http://www.hl7.org/Special/committees/hsswg" },
        { "hta", "http://www.hl7.org/Special/committees/termauth" },
        { "ictc", "http://www.hl7.org/Special/committees/ictc" },
        { "ii", "http://www.hl7.org/Special/committees/imagemgt" },
        { "inm", "http://www.hl7.org/Special/committees/inm" },
        { "its", "http://www.hl7.org/Special/committees/xml" },
        { "lhs", "http://www.hl7.org/Special/committees/lhs" },
        { "mnm", "http://www.hl7.org/Special/committees/mnm" },
        { "mobile", "http://www.hl7.org/Special/committees/mobile" },
        { "oo", "http://www.hl7.org/Special/committees/orders" },
        { "pa", "http://www.hl7.org/Special/committees/pafm" },
        { "pe", "http://www.hl7.org/Special/committees/patientempowerment" },
        { "pc", "http://www.hl7.org/Special/committees/patientcare" },
        { "pher", "http://www.hl7.org/Special/committees/pher" },
        { "phx", "http://www.hl7.org/Special/committees/medication" },
        { "sd", "http://www.hl7.org/Special/committees/structure" },
        { "sec", "http://www.hl7.org/Special/committees/secure" },
        { "soa", "http://www.hl7.org/Special/committees/soa" },
        { "ti", "http://www.hl7.org/Special/committees/Vocab" },
        { "tsmg", "http://www.hl7.org/Special/committees/tsmg" },
        { "us", "http://www.hl7.org/Special/committees/usrealm" },
        { "v2", "http://www.hl7.org/Special/committees/v2management" },
        {  "vocab", "http://www.hl7.org/Special/committees/Vocab" },
    };

    /// <summary>
    /// Resolve an HL7 Workgroup name to its human-readable name.
    /// </summary>
    /// <remarks>
    /// Pulled from:
    ///     - https://github.com/hapifhir/org.hl7.fhir.core/blob/master/org.hl7.fhir.utilities/src/main/java/org/hl7/fhir/utilities/HL7WorkGroups.java
    ///     - on 2025.08.18
    /// </remarks>
    public static readonly Dictionary<string, string> WorkgroupNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "aid", "Application Implementation and Design" },
        { "arden", "Arden Syntax" },
        { "brr", "Biomedical Research and Regulation" },
        { "cbcc", "Community Based Collaborative Care" },
        { "cdamg", "CDA Management Group" },
        { "cds", "Clinical Decision Support" },
        { "cg", "Clinical Genomics" },
        { "cgp", "Cross-Group Projects" },
        { "cic", "Clinical Interoperability Council" },
        { "cimi", "Clinical Information Modeling Initiative" },
        { "claims", "Payer/Provider Information Exchange Work Group" },
        { "cqi", "Clinical Quality Information" },
        { "dev", "Health Care Devices" },
        { "ehr", "Electronic Health Records" },
        { "ec", "Emergency Care" },
        { "fhir", "FHIR Infrastructure" },
        { "fmg", "FHIR Management Group" },
        { "fm", "Financial Management" },
        { "hsi", "Health Standards Integration" },
        { "hsswg", "Human and Social Services" },
        { "hta", "Terminology Authority" },
        { "ictc", "Conformance" },
        { "ii", "Imaging Integration" },
        { "inm", "Infrastructure And Messaging" },
        { "its", "Implementable Technology Specifications" },
        { "lhs", "Learning Health Systems" },
        { "mnm", "Modeling and Methodology" },
        { "mobile", "Mobile Health" },
        { "oo", "Orders and Observations" },
        { "pa", "Patient Administration" },
        { "pe", "Patient Empowerment" },
        { "pc", "Patient Care" },
        { "pher", "Public Health" },
        { "phx", "Pharmacy" },
        { "sd", "Structured Documents" },
        { "sec", "Security" },
        { "soa", "Services Oriented Architecture" },
        { "ti", "Terminology Infrastructure" },
        { "tsmg", "Terminology Services Management Group (TSMG)" },
        { "us", "US Realm Steering Committee" },
        { "v2", "V2 Management Group" },
        { "vocab", "HL7 International / Terminology Infrastructure" },
        //{ "vocab", "Terminology Infrastructure" },
    };
}
