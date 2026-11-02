// <copyright file="UnsafeCodeSourceTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

public class UnsafeCodeSourceTests
{
    [Fact]
    public void FilesCaptureBothDirectionsBeforeLegacyResolution()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();

        List<DbUnsafeCodeElementAnnotation> annotations = DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection);
        annotations.Count.ShouldBe(2);
        annotations.Select(row => row.TargetFhirPackageKey).Order().ShouldBe(fixture.Packages.Take(2).Select(row => row.Key).Order());
        foreach (DbUnsafeCodeElementAnnotation row in annotations)
        {
            row.SourceFhirPackageKey.ShouldBe(fixture.Packages[2].Key);
            row.SourceElementId.ShouldBe(UnsafeCodeTestFixture.FilterElement);
            row.TargetElementId.ShouldBe(UnsafeCodeTestFixture.FilterElement);
            row.RequiresOmittedMandatoryCode.ShouldBeTrue();
            row.GroupIndex.ShouldBe(0);
            row.SourceIndex.ShouldBe(0);
            row.TargetIndex.ShouldBe(0);
            row.SourceRelativeFilename.ShouldStartWith(Path.Combine("input", "elements"));
            row.ConceptMapId.ShouldNotBeNullOrEmpty();
            row.ConceptMapUrl.ShouldNotBeNullOrEmpty();
        }

        List<DbUnsafeCodeMapEvidence> evidence = DbUnsafeCodeMapEvidence.SelectList(fixture.Connection);
        evidence.Count.ShouldBe(8);
        evidence.Count(row => row.ExplicitNoMap).ShouldBe(4);
        evidence.Where(row => row.SourceCode == "no-target").ShouldAllBe(row => !row.ExplicitNoMap && row.TargetIndex == null);
        evidence.Where(row => row.SourceCode == "=").ShouldAllBe(row => row.Relationship == ConceptMap.ConceptMapRelationship.Equivalent && row.TargetIndex == 0 && !row.ExplicitNoMap);
        evidence.ShouldAllBe(row =>
            row.SourceSystem == UnsafeCodeTestFixture.FilterSystem &&
            row.SourceSystemVersion == "source-system,version" &&
            row.SourceValueSetUrl == UnsafeCodeTestFixture.FilterValueSet &&
            row.SourceValueSetVersion == "source-vs" &&
            row.TargetValueSetVersion != row.TargetSystemVersion &&
            row.SourceRelativeFilename.StartsWith(Path.Combine("input", "codes"), StringComparison.Ordinal));

        // Non-expandable ValueSets have no normalized concept maps, but authored evidence survives.
        DbValueSetConceptMapping.SelectList(fixture.Connection).ShouldBeEmpty();
        fixture.InputState.CompletedInputRevision.ShouldNotBeNullOrEmpty();
        fixture.InputState.FormatVersion.ShouldBe(UnsafeCodeConstants.InputFormatVersion);
        List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(fixture.Connection);
        pairs.Count.ShouldBe(6);
        pairs.ShouldAllBe(row => row.InputRevision == fixture.InputState.CompletedInputRevision && row.ComparisonRevision == null && row.OutcomeInputRevision == null);
    }

    [Theory]
    [InlineData("declaration-only")]
    [InlineData("false")]
    [InlineData("absent")]
    [InlineData("wrong-system")]
    [InlineData("wrong-type")]
    [InlineData("wrong-code")]
    [InlineData("wrong-value-type")]
    [InlineData("undeclared")]
    [InlineData("wrong-assignment-code")]
    public void NonPositiveAnnotationsDoNotProduceFacts(string scenario)
    {
        using UnsafeCodeTestFixture fixture = new();
        ConceptMap map = fixture.ReadMap("elements", "ConceptMap-elements-5to4.json");
        switch (scenario)
        {
            case "declaration-only":
                map.Group.Clear();
                break;
            case "false":
                map.Group[0].Element[0].Target[0].Property[0].Value = new FhirBoolean(false);
                break;
            case "absent":
                map.Group[0].Element[0].Target[0].Property.Clear();
                break;
            case "wrong-system":
                map.Property[0].System = "https://hl7.org/fhir/uv/xver/ValueSet/xver-conceptmap-properties";
                break;
            case "wrong-type":
                map.Property[0].Type = ConceptMap.ConceptMapPropertyType.Code;
                break;
            case "wrong-code":
                map.Property[0].Code = "other-property";
                break;
            case "wrong-value-type":
                map.Group[0].Element[0].Target[0].Property[0].Value = new Code("true");
                break;
            case "undeclared":
                map.Property.Clear();
                break;
            case "wrong-assignment-code":
                map.Group[0].Element[0].Target[0].Property[0].Code = "other-property";
                break;
        }

        fixture.Capture(map);
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldBeEmpty();
    }

    [Fact]
    public void TrueAssignmentsAreIndividualAndDoNotRequireResolvedElementsOrMappings()
    {
        using UnsafeCodeTestFixture fixture = new();
        ConceptMap map = fixture.ReadMap("elements", "ConceptMap-elements-5to4.json");
        ConceptMap.TargetElementComponent absent = new() { Code = "Other.op" };
        map.Group[0].Element[0].Target.Add(absent);
        fixture.Capture(map);
        List<DbUnsafeCodeElementAnnotation> rows = DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection);
        rows.Count.ShouldBe(1);
        rows[0].TargetIndex.ShouldBe(0);
        DbElementMapping.SelectList(fixture.Connection).ShouldBeEmpty();

        // Reconciliation of a legacy mapping cannot mutate the separate authored fact.
        DbElementMapping.DropTable(fixture.Connection);
        DbElementMapping.CreateTable(fixture.Connection);
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmbiguousRelevantDeclarationsFailContextually(bool differentSystem)
    {
        using UnsafeCodeTestFixture fixture = new();
        ConceptMap map = fixture.ReadMap("elements", "ConceptMap-elements-5to4.json");
        map.Property.Add(new()
        {
            Code = UnsafeCodeConstants.PropertyCode,
            System = differentSystem ? "urn:other" : UnsafeCodeConstants.PropertySystem,
            Type = differentSystem ? ConceptMap.ConceptMapPropertyType.Boolean : ConceptMap.ConceptMapPropertyType.String,
        });
        InvalidDataException exception = Should.Throw<InvalidDataException>(() => fixture.Capture(map));
        exception.Message.ShouldContain("test.json");
        exception.Message.ShouldContain(map.Url);
        exception.Message.ShouldContain("$.property");
        exception.Message.ShouldContain("ambiguous");
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldBeEmpty();
    }

    [Fact]
    public void UnresolvedConceptsStillHaveAuthoredNoMapAndRelationshipEvidence()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.SetExpandable(true);
        fixture.Load().ShouldBeTrue();
        DbValueSetConceptMapping.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection, SourceCode: "unresolved").Count.ShouldBe(2);
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection, SourceCode: "=").ShouldAllBe(row => row.Relationship == ConceptMap.ConceptMapRelationship.Equivalent);
    }

    [Fact]
    public void UnresolvedValueSetRetainsFactsButDoesNotChangeLegacyFailure()
    {
        using UnsafeCodeTestFixture fixture = new();
        DbValueSet.Delete(fixture.Connection);
        Should.Throw<Exception>(() => fixture.Load()).Message.ShouldContain("Failed to resolve source value set");
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection).ShouldContain(row => row.SourceCode == "unresolved" && row.ExplicitNoMap);
        fixture.InputState.CompletedInputRevision.ShouldBeNull();
    }

    [Fact]
    public void CodeEvidenceKeepsAllGroupSourceAndTargetPositions()
    {
        using UnsafeCodeTestFixture fixture = new();
        ConceptMap map = fixture.ReadMap("codes", "ConceptMap-R5-filter-operator-R4.json");
        map.Group[0].Element[1].Target.Add(new() { Code = "other", Relationship = ConceptMap.ConceptMapRelationship.RelatedTo });
        map.Group.Add((ConceptMap.GroupComponent)map.Group[0].DeepCopy());
        fixture.Capture(map, "codes");
        List<DbUnsafeCodeMapEvidence> rows = DbUnsafeCodeMapEvidence.SelectList(fixture.Connection);
        rows.Count.ShouldBe(10);
        rows.Count(row => row.GroupIndex == 1).ShouldBe(5);
        rows.Count(row => row.SourceIndex == 1 && row.TargetIndex == 1 && row.TargetCode == "other").ShouldBe(2);
        rows.ShouldAllBe(row => row.TargetValueSetVersion == "target-vs-r4" && row.TargetSystemVersion == "target-system-r4");
    }

    [Fact]
    public void OptionalConfigurationNormalizesDuplicatesAndPreservesExactIdentity()
    {
        ImmutableArray<UnsafeCodeRule> rules = Parse("""
            [
              {"system":"urn:oid:1.2.3","code":"X"},
              {"system":"urn:oid:1.2.3","code":"X","noMapOnly":false,"excludedRelationship":[],"excludedVersion":[]},
              {"system":"urn:oid:1.2.3","version":"v,1|'quoted'","code":"X","excludedRelationship":["equivalent","related-to","equivalent"],"excludedVersion":["4.3.0","4.0.1","4.3.0"]},
              {"system":"urn:oid:1.2.3","version":"v,1|'quoted'","code":"X","noMapOnly":false,"excludedRelationship":["related-to","equivalent"],"excludedVersion":["4.0.1","4.3.0"]}
            ]
            """);
        rules.Length.ShouldBe(2);
        rules[0].Identity.Version.ShouldBeNull();
        rules[0].NoMapOnly.ShouldBeFalse();
        rules[1].Identity.Version.ShouldBe("v,1|'quoted'");
        rules[1].ExcludedVersions.Count.ShouldBe(2);
        rules[1].EntryIndex.ShouldBe(2);

        using UnsafeCodeTestFixture fixture = new();
        UnsafeCodeInputStore.InsertRules(fixture.Connection, rules);
        List<UnsafeCodeRule> roundTrip = DbUnsafeCodeRule.SelectList(fixture.Connection).Select(UnsafeCodeInputStore.ReadRule).ToList();
        roundTrip.Select(rule => rule.Identity).ShouldBe(rules.Select(rule => rule.Identity));
        roundTrip[1].ExcludedRelationships.SetEquals(rules[1].ExcludedRelationships).ShouldBeTrue();
        roundTrip[1].ExcludedVersions.SetEquals(rules[1].ExcludedVersions).ShouldBeTrue();
    }

    [Theory]
    [InlineData("{}", "<root>", "$", "array")]
    [InlineData("null", "<root>", "$", "array")]
    [InlineData("[null]", "0", "$[0]", "object")]
    [InlineData("[{}]", "0", "$[0].system", "nonblank")]
    [InlineData("""[{"system":"relative","code":"x"}]""", "0", "$[0].system", "absolute")]
    [InlineData("""[{"system":" http://example.org","code":"x"}]""", "0", "$[0].system", "absolute")]
    [InlineData("""[{"system":"http://example.org|v","code":"x"}]""", "0", "$[0].system", "absolute")]
    [InlineData("""[{"system":null,"code":"x"}]""", "0", "$[0].system", "nonblank")]
    [InlineData("""[{"system":7,"code":"x"}]""", "0", "$[0].system", "nonblank")]
    [InlineData("""[{"system":"urn:test","code":" "}]""", "0", "$[0].code", "nonblank")]
    [InlineData("""[{"system":"urn:test"}]""", "0", "$[0].code", "nonblank")]
    [InlineData("""[{"system":"urn:test","code":1}]""", "0", "$[0].code", "nonblank")]
    [InlineData("""[{"system":"urn:test","code":"x","version":null}]""", "0", "$[0].version", "nonblank")]
    [InlineData("""[{"system":"urn:test","code":"x","version":""}]""", "0", "$[0].version", "nonblank")]
    [InlineData("""[{"system":"urn:test","code":"x","version":4}]""", "0", "$[0].version", "nonblank")]
    [InlineData("""[{"system":"urn:test","code":"x","noMapOnly":"true"}]""", "0", "$[0].noMapOnly", "boolean")]
    [InlineData("""[{"system":"urn:test","code":"x","noMapOnly":null}]""", "0", "$[0].noMapOnly", "boolean")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedRelationship":"equivalent"}]""", "0", "$[0].excludedRelationship", "array")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedRelationship":[true]}]""", "0", "$[0].excludedRelationship[0]", "string")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedRelationship":["Equivalent"]}]""", "0", "$[0].excludedRelationship", "literal")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersion":null}]""", "0", "$[0].excludedVersion", "array")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersion":[null]}]""", "0", "$[0].excludedVersion[0]", "string")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersion":["R4"]}]""", "0", "$[0].excludedVersion", "release")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersion":["4B"]}]""", "0", "$[0].excludedVersion", "release")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersion":["4.0"]}]""", "0", "$[0].excludedVersion", "release")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersion":["0.0.1"]}]""", "0", "$[0].excludedVersion", "release")]
    [InlineData("""[{"system":"urn:test","code":"x","excludedVersions":[]}]""", "0", "$[0].excludedVersions", "unknown")]
    [InlineData("""[{"system":"urn:test","system":"urn:test","code":"x"}]""", "0", "$[0].system", "duplicate object property")]
    [InlineData("""[{"system":"urn:test","code":"x","noMapOnly":false,"noMapOnly":false}]""", "0", "$[0].noMapOnly", "duplicate object property")]
    [InlineData("""[{"system":"urn:test","code":"x","\u0063ode":"x"}]""", "0", "$[0].code", "duplicate object property")]
    [InlineData("""[{"system":"urn:test","code":"x"},{"system":"urn:test","code":"x","noMapOnly":true}]""", "1", "$[1]", "contradictory")]
    [InlineData("""[{"system":"urn:test","code":"x"},{"system":"urn:test","code":"x","excludedVersion":["4.0.1"]}]""", "1", "$[1]", "contradictory")]
    public void InvalidConfigurationReportsFileEntryPathAndReason(string json, string entry, string path, string reason)
    {
        InvalidDataException exception = Should.Throw<InvalidDataException>(() => Parse(json));
        exception.Message.ShouldContain("config.json");
        exception.Message.ShouldContain("entry " + entry);
        exception.Message.ShouldContain("JSON path " + path);
        exception.Message.ShouldContain(reason);
    }

    [Fact]
    public void MalformedConfigurationHasContextAndCause()
    {
        InvalidDataException exception = Should.Throw<InvalidDataException>(() => Parse("[{"));
        exception.Message.ShouldContain("config.json");
        exception.Message.ShouldContain("entry");
        exception.Message.ShouldContain("JSON path $");
        exception.Message.ShouldContain("malformed JSON");
        (exception.InnerException is JsonException).ShouldBeTrue();
    }

    [Fact]
    public void ConfigurationIsIndependentAndRemovalOrEmptyArrayReplacesRules()
    {
        using UnsafeCodeTestFixture fixture = new();
        Directory.Delete(Path.Combine(fixture.SourceRoot, "input", "elements"), true);
        fixture.WriteConfiguration("""[{"system":"urn:configured","code":"only","noMapOnly":true}]""");
        fixture.Load().ShouldBeTrue();
        string? revision = fixture.InputState.CompletedInputRevision;
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeRule.SelectList(fixture.Connection).Single().NoMapOnly.ShouldBeTrue();
        File.Delete(fixture.ConfigurationFilename);
        fixture.Load().ShouldBeTrue();
        fixture.InputState.CompletedInputRevision.ShouldNotBe(revision);
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
        fixture.WriteConfiguration("""[{"system":"urn:configured","code":"only"}]""");
        fixture.Load().ShouldBeTrue();
        fixture.WriteConfiguration("[]");
        fixture.Load().ShouldBeTrue();
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
    }

    [Fact]
    public void FailedPreflightPreservesCompletedSnapshotAndLegacyMaps()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.WriteConfiguration("""[{"system":"urn:configured","code":"only"}]""");
        fixture.Load().ShouldBeTrue();
        DbUnsafeCodeInputState before = fixture.InputState;
        List<int> legacyKeys = DbElementMapping.SelectList(fixture.Connection).Select(row => row.Key).ToList();
        fixture.WriteConfiguration("""[{"system":"urn:configured","code":"only","version":null}]""");
        Should.Throw<InvalidDataException>(() => fixture.Load()).Message.ShouldContain(fixture.ConfigurationFilename);
        fixture.InputState.CompletedInputRevision.ShouldBe(before.CompletedInputRevision);
        fixture.InputState.SnapshotId.ShouldBe(before.SnapshotId);
        DbElementMapping.SelectList(fixture.Connection).Select(row => row.Key).ShouldBe(legacyKeys);
        DbUnsafeCodeRule.SelectList(fixture.Connection).Count.ShouldBe(1);
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).Count.ShouldBe(2);
        DbUnsafeCodePairState.SelectList(fixture.Connection).ShouldAllBe(row => row.InputRevision == before.CompletedInputRevision);
    }

    [Fact]
    public void UnreadableConfigurationPreservesCompletedSnapshot()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.WriteConfiguration("[]");
        fixture.Load().ShouldBeTrue();
        string? revision = fixture.InputState.CompletedInputRevision;
        using FileStream locked = new(fixture.ConfigurationFilename, FileMode.Open, FileAccess.Read, FileShare.None);
        InvalidDataException exception = Should.Throw<InvalidDataException>(() => fixture.Load());
        exception.Message.ShouldContain(fixture.ConfigurationFilename);
        exception.Message.ShouldContain("entry <root>");
        exception.Message.ShouldContain("JSON path $");
        exception.Message.ShouldContain("cannot read");
        exception.InnerException.ShouldNotBeNull();
        fixture.InputState.CompletedInputRevision.ShouldBe(revision);
    }

    [Fact]
    public void FmlReconciliationDoesNotOverwriteAuthoredFacts()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.InsertFilterElements();
        string directory = Path.Combine(fixture.SourceRoot, "input", "R5toR4");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ValueSet5to4.fml"), """
            /// url = "http://example.org/StructureMap/ValueSet5to4"
            /// name = "ValueSet5to4"
            uses "http://hl7.org/fhir/5.0/ValueSet" alias ValueSetR5 as source
            uses "http://hl7.org/fhir/4.0/ValueSet" alias ValueSetR4 as target
            group ValueSet(source src : ValueSetR5, target tgt : ValueSetR4) {
              src.compose.include.filter.op -> tgt.compose.include.filter.op;
            }
            """);
        fixture.Load().ShouldBeTrue();
        DbElementMapping.SelectList(fixture.Connection).ShouldContain(row =>
            row.SourceElementId == UnsafeCodeTestFixture.FilterElement &&
            row.TargetFhirPackageKey == fixture.Packages[0].Key &&
            row.FmlSourceKey != null && row.FmlIsSimpleCopy == true);
        List<DbUnsafeCodeElementAnnotation> annotations = DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection);
        annotations.Count.ShouldBe(2);
        annotations.ShouldAllBe(row => row.SourceRelativeFilename.EndsWith(".json", StringComparison.Ordinal) && row.TargetIndex == 0 && row.RequiresOmittedMandatoryCode);
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection).Count.ShouldBe(8);
    }

    [Fact]
    public void MappingResetOwnsInputsAndStatesButNotTriggerLifecycle()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        DbUnsafeCodeTrigger.CreateTable(fixture.Connection);
        new DbUnsafeCodeTrigger
        {
            StructureOutcomeKey = 1,
            SourceFhirPackageKey = fixture.Packages[2].Key,
            TargetFhirPackageKey = fixture.Packages[0].Key,
            SourceElementId = UnsafeCodeTestFixture.FilterElement,
            TargetElementId = UnsafeCodeTestFixture.FilterElement,
            SourcePathJson = "[]",
            System = UnsafeCodeTestFixture.FilterSystem,
            Code = "unsafe-demo",
            IsWildcardVersion = true,
            VersionsJson = "[]",
            EvidenceKeysJson = "[]",
            RuleKeysJson = "[]",
        }.Insert(fixture.Connection);
        DbMappingClasses.DropTables(fixture.Connection);
        DbMappingClasses.CreateTables(fixture.Connection);
        DbUnsafeCodeInputState.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodePairState.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeTrigger.SelectList(fixture.Connection).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("codes", "ConceptMap-R5-filter-operator-R4.json", false)]
    [InlineData("elements", "ConceptMap-elements-5to4.json", false)]
    [InlineData("codes", "ConceptMap-R5-filter-operator-R4.json", true)]
    [InlineData("elements", "ConceptMap-elements-5to4.json", true)]
    public void MidLoadSourceFailureCannotPublishCompletion(string category, string filename, bool unreadable)
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        string snapshot = fixture.InputState.SnapshotId;
        string path = Path.Combine(fixture.SourceRoot, "input", category, filename);
        using FileStream? locked = unreadable ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        if (!unreadable)
        {
            File.WriteAllText(path, "{");
        }

        InvalidDataException exception = Should.Throw<InvalidDataException>(() => fixture.Load());
        exception.Message.ShouldContain(path);
        exception.Message.ShouldContain("->");
        fixture.InputState.SnapshotId.ShouldNotBe(snapshot);
        fixture.InputState.CompletedInputRevision.ShouldBeNull();
        DbUnsafeCodePairState.SelectList(fixture.Connection).ShouldAllBe(row => row.InputRevision == null && row.ComparisonRevision == null && row.OutcomeInputRevision == null);
    }

    [Fact]
    public void ContentResetInvalidatesBeforeDroppingContent()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(fixture.Connection);
        foreach (DbUnsafeCodePairState pair in pairs)
        {
            pair.ComparisonRevision = "comparison";
            pair.OutcomeInputRevision = pair.InputRevision;
            pair.OutcomeComparisonRevision = "comparison";
            pair.OutcomeFormatVersion = 1;
        }

        pairs.Update(fixture.Connection);
        DbContentClasses.DropTables(fixture.Connection, forPackages: false, forTerminologies: false, forStructures: false, forSupport: true);
        fixture.InputState.CompletedInputRevision.ShouldBeNull();
        DbUnsafeCodePairState.SelectList(fixture.Connection).ShouldAllBe(row => row.InputRevision == null && row.ComparisonRevision == null && row.OutcomeInputRevision == null && row.OutcomeComparisonRevision == null && row.OutcomeFormatVersion == null);
        DbFhirPackage.SelectList(fixture.Connection).Count.ShouldBe(3);
    }

    [Fact]
    public void SourceFreeProcessorExplicitlyCompletesEmptyInputsWithoutResettingLegacyMaps()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.WriteConfiguration("""[{"system":"urn:configured","code":"only"}]""");
        fixture.Load().ShouldBeTrue();
        int legacyCount = DbElementMapping.SelectList(fixture.Connection).Count;
        string? oldRevision = fixture.InputState.CompletedInputRevision;
        CreateProcessor(fixture, string.Empty).LoadFhirCrossVersionMaps();
        fixture.InputState.SourceMode.ShouldBe("none");
        fixture.InputState.SourceRoot.ShouldBeNull();
        fixture.InputState.CompletedInputRevision.ShouldNotBeNullOrEmpty();
        fixture.InputState.CompletedInputRevision.ShouldNotBe(oldRevision);
        DbUnsafeCodeRule.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeMapEvidence.SelectList(fixture.Connection).ShouldBeEmpty();
        DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldBeEmpty();
        DbElementMapping.SelectList(fixture.Connection).Count.ShouldBe(legacyCount);
    }

    [Fact]
    public void ProcessorRejectsInvalidConfiguredSourceInsteadOfDiscardingFalse()
    {
        using UnsafeCodeTestFixture fixture = new();
        string path = Path.Combine(fixture.SourceRoot, "not-present");
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => CreateProcessor(fixture, path).LoadFhirCrossVersionMaps());
        exception.Message.ShouldContain(path);
        DbUnsafeCodeInputState.SelectList(fixture.Connection).ShouldBeEmpty();
    }

    [Fact]
    public void StructuredPathsAndArbitraryVersionsUseLosslessSerialization()
    {
        ImmutableArray<UnsafeCodePathStep> path = [new("ValueSet.compose:some,slice", "code", false), new("quoted'|path", null, true)];
        string serialized = JsonSerializer.Serialize(path);
        JsonSerializer.Deserialize<ImmutableArray<UnsafeCodePathStep>>(serialized).ShouldBe(path);
        UnsafeCodeVersionMatch match = new(true, ["v,1", "v|'2"]);
        JsonSerializer.Deserialize<UnsafeCodeVersionMatch>(JsonSerializer.Serialize(match))!.Versions.ShouldBe(match.Versions);
    }

    private static ImmutableArray<UnsafeCodeRule> Parse(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return UnsafeCodeConfigurationLoader.Parse(stream, "config.json");
    }

    private static XVerProcessor CreateProcessor(UnsafeCodeTestFixture fixture, string sourcePath)
    {
        // The production database constructors open files. This narrow adapter keeps caller tests in-memory.
        ComparisonDatabase database = (ComparisonDatabase)RuntimeHelpers.GetUninitializedObject(typeof(ComparisonDatabase));
        typeof(ComparisonDatabase).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(database, fixture.Connection);
        XVerProcessor processor = new(new ConfigXVer
        {
            CrossVersionDbPath = fixture.SourceRoot,
            CrossVersionMapSourcePath = sourcePath,
            LogFactory = NullLoggerFactory.Instance,
        });
        typeof(XVerProcessor).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(processor, database);
        return processor;
    }
}
