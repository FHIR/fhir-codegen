# **DRAFT** Cross-Version FHIR Artifacts Generation Process

This document describes the comprehensive process for creating cross-version ValueSets and StructureDefinition extensions that enable interoperability between different versions of FHIR. The process consists of four main phases that work together to analyze differences between FHIR versions and generate appropriate bridge artifacts.

## Overview

The cross-version artifacts generation process enables FHIR implementations to work with data from different FHIR versions by providing:

- **Cross-version ValueSets**: ValueSets containing concepts that don't have direct equivalent mappings between versions
- **Cross-version Extensions**: StructureDefinition extensions that allow elements from one FHIR version to be represented in another version
- **Validation Packages**: Complete FHIR packages containing all necessary artifacts for cross-version validation

The process analyzes structural and terminological differences between FHIR versions and automatically generates the necessary bridging artifacts.

## Phase 1: Database Loading and Initialization

### Database Creation and Population

The first phase establishes a comparison database that serves as the foundation for all subsequent analysis. This involves:

**Package Definition Loading**: Multiple FHIR core packages (representing different FHIR versions) are loaded into memory. Each package contains the complete set of FHIR artifacts for that version, including:
- ValueSets and their expanded concept lists
- StructureDefinitions for all data types and resources
- Element definitions with type information and bindings
- Binding strength information and constraints

**Database Schema Creation**: A SQLite database is created with tables to store:
- FHIR package metadata and version information
- ValueSet definitions and their concept expansions
- StructureDefinition metadata and element hierarchies
- Element type information and binding relationships
- Comparison results and relationship mappings

**Content Extraction and Storage**: For each FHIR package:

*ValueSet Processing*:
- All ValueSets are identified and their metadata extracted
- Each ValueSet is expanded to enumerate all contained concepts
- Concepts are stored with their system, code, display, and property information
- Binding relationships are analyzed to determine which ValueSets have required bindings
- Escape valve codes (like "OTHER", "UNKNOWN") are identified and flagged

*StructureDefinition Processing*:
- All primitive types, complex types, and resources are processed
- Element hierarchies are built with parent-child relationships
- Type information is extracted including profiles and target profiles
- Cardinality, binding, and constraint information is preserved
- Additional bindings and their purposes are catalogued

**Post-Processing Operations**:
- ValueSets containing escape valve codes are flagged for special handling
- Element inheritance relationships are resolved
- Type structure keys are linked to reference the appropriate StructureDefinitions

## Phase 2: Cross-Version Map Loading

### Existing Map Integration

This phase loads pre-existing cross-version mappings to bootstrap the comparison process with known relationships.

**Map Source Discovery**: The system searches for existing ConceptMaps in the cross-version map source directory. These maps are categorized by their usage context:
- ValueSet mappings (concept-level mappings between ValueSets)
- Type overview mappings (high-level type relationships)
- Resource overview mappings (high-level resource relationships)  
- Detailed structure mappings (element-level mappings)

**Primitive Type Mappings**: Default mappings for primitive types are loaded based on predefined relationship rules. These establish fundamental type relationships like:
- Concept domain relationships (whether types are conceptually equivalent)
- Value domain relationships (whether value spaces are compatible)
- Specific primitive type cross-version mappings (e.g., `id` vs `string`)

**ValueSet Concept Mapping Processing**: For each ValueSet ConceptMap:
- Source and target ValueSets are identified in the database
- Individual concept mappings are processed with their relationships
- Unresolved concepts (those that exist in maps but not in expanded ValueSets) are flagged
- No-map concepts are recorded for completeness

**Structure and Element Mapping Processing**: For structure-level ConceptMaps:
- StructureDefinition mappings are established between versions
- Element-level mappings are processed with their relationship types
- Unresolved elements and structures are catalogued for later analysis
- Inverse relationship keys are established for bidirectional navigation

**Relationship Validation**: The loaded mappings are validated for consistency:
- Inverse relationships are cross-referenced
- Mapping completeness is assessed
- Conflicting mappings are identified and flagged

## Phase 3: Database Comparison Analysis

### Comprehensive Cross-Version Analysis

This phase performs systematic comparison between FHIR versions to identify all differences and relationship types.

**Comparison Pair Setup**: For each adjacent pair of FHIR versions:
- Comparison contexts are established with source and target packages
- Filtering criteria are applied based on configuration
- Comparison caches are initialized for performance optimization

**ValueSet Comparison Process**:

*Content Analysis*:
- Concepts are compared across versions using exact matching (system + code)
- Relationships are determined: equivalent, broader, narrower, related, or unrelated
- New concepts (present in target but not source) are identified
- Deprecated concepts (present in source but not target) are flagged
- Escape valve code usage is analyzed for consistency

*Binding Strength Analysis*:
- Required bindings are prioritized for cross-version support
- Binding strength changes are documented
- Additional bindings are compared for compatibility

*Expansion Capability Assessment*:
- ValueSets that cannot be expanded are flagged
- Expansion failures are categorized by cause
- Alternative representation strategies are determined

**StructureDefinition Comparison Process**:

*Structural Analysis*:
- Element hierarchies are compared between versions
- New elements, removed elements, and renamed elements are identified
- Cardinality changes are analyzed for compatibility
- Type system evolution is tracked

*Element-Level Comparison*:
- Element paths are matched across versions
- Type compatibility is assessed using primitive type mappings
- Binding changes are analyzed for impact
- Constraint modifications are evaluated

*Inheritance and Profiling Analysis*:
- Base type relationships are traced across versions
- Profile compatibility is assessed
- Extension points are identified for elements without direct mappings

**Relationship Determination Logic**: For each comparison, relationships are classified using a sophisticated algorithm that considers:
- Exact matches (same system, code, and meaning)
- Semantic equivalence (same concept, different representation)
- Hierarchical relationships (broader/narrower concept spaces)
- Related concepts (overlapping but not equivalent)
- Unrelated concepts (no meaningful relationship)

## Phase 4: Cross-Version Artifact Generation

### ValueSet Generation

**Cross-Version ValueSet Creation**: For each source package, the system generates ValueSets for target packages containing concepts that lack direct equivalent mappings.

*Target Analysis*:
- Concepts with equivalent mappings across all intermediate versions are excluded
- Concepts requiring special handling (broader, narrower, related) are included
- Escape valve codes are specially handled to maintain consistency

*ValueSet Construction*:
- New ValueSet resources are created with appropriate metadata
- Compose sections enumerate the source concepts requiring cross-version support
- Expansion sections provide the complete enumerated concept list
- Versioning aligns with the cross-version artifact versioning scheme

*Multi-Version Propagation*:
- ValueSets are built incrementally across version chains
- Concepts are tracked through multiple version transitions
- Cumulative concept collections are maintained for complex version paths

### Extension Generation

**Cross-Version Extension Creation**: For elements that cannot be directly mapped between versions, extension StructureDefinitions are generated.

*Extension Necessity Determination*:
- Elements with equivalent mappings are excluded from extension generation
- Elements mapping to Basic resource paths are handled specially
- Parent element relationships affect child element extension needs
- Version-specific context requirements are analyzed

*Extension StructureDefinition Construction*:

*Context Determination*:
- Target contexts are identified by analyzing element mappings
- Fallback contexts (like "Element") are used when specific contexts cannot be determined
- Multiple contexts are supported when elements appear in various locations

*Value Type Mapping*:
- Source element types are mapped to target-version-compatible types
- Type profile information is preserved where possible
- Canonical references are handled with special alternate-canonical extensions
- Complex type mappings generate nested extension structures

*Datatype Extension Handling*:
- Replacement types are generated for incompatible primitive types
- Datatype extensions track the original type name for round-trip compatibility
- Type promotion and demotion is handled systematically

*Constraint Preservation*:
- Cardinality constraints from source elements are maintained
- Binding information is transferred with cross-version ValueSet references
- Modifier flags and other semantic indicators are preserved

### Package Assembly

**Validation Package Creation**: Complete FHIR packages are assembled containing all cross-version artifacts.

*Package Structure*:
- Individual source-to-target packages for specific version pairs
- Comprehensive packages containing all cross-version artifacts for a target version
- Proper dependency declarations for required base packages

*Implementation Guide Generation*:
- ImplementationGuide resources catalog all generated artifacts
- Resource listings provide complete inventories
- Dependency relationships are properly declared

*Package Metadata*:
- Proper package.json files with dependency declarations
- Manifest files for package managers
- Index files for artifact discovery

*Distribution Preparation*:
- NPM-compatible package archives are created
- Directory structures follow FHIR package conventions
- Validation-ready artifacts are properly formatted

## Outcome Tracking and Documentation

### Mapping Decision Documentation

Throughout the process, detailed records are maintained documenting every mapping decision:

**Element Mapping Outcomes**: For each source element, one of several outcomes is recorded:
- Use element with same name in target version
- Use renamed element in target version  
- Use cross-version extension
- Use extension inherited from ancestor element
- Use Basic resource element path
- Use one of several possible target elements

**Substitution Handling**: Known substitutions are applied where appropriate:
- Well-known extensions replace deprecated elements
- Standard FHIR extension patterns are leveraged
- Community-established cross-version practices are followed

**Lookup Table Generation**: Comprehensive lookup tables are generated providing:
- Source element to target element mappings
- Extension URL references for non-mappable elements
- Usage guidance for implementers
- Round-trip conversion strategies

This comprehensive process ensures that FHIR implementations can seamlessly work with data from multiple FHIR versions while maintaining semantic fidelity and validation compliance.
