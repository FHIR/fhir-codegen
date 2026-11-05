// <copyright file="UnsafeCodePipelineTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class UnsafeCodePipelineTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReloadedExportCommandRegeneratesPrerequisites(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = new(fixture.Packages[2], fixture.Packages[targetIndex]);
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand("export", true,
            () => calls.Add("database"),
            () => { fixture.LoadProfileMaps(); calls.Add("maps"); },
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            family =>
            {
                family.ShouldBeNull();
                fixture.CompareProfilePair(pair);
                new ValueSetComparer(fixture.Connection, NullLoggerFactory.Instance).CompareValueSets(specificPairs: [pair.SequencePair]);
                calls.Add("compare");
            },
            family =>
            {
                family.ShouldBeNull();
                new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance)
                    .GenerateOutcomes(specificPairs: [pair.SequencePair]);
                calls.Add("outcomes");
            },
            () =>
            {
                UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [pair]);
                DbUnsafeCodeTrigger.SelectList(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey,
                    TargetFhirPackageKey: pair.TargetPackageKey).ShouldNotBeEmpty();
                ConfigXVer config = ExportConfig(fixture, targetIndex, "reload-export");
                new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false,
                    specificPairs: [pair.SequencePair]);
                Directory.GetFiles(config.OutputDirectory, "StructureDefinition-*.json", SearchOption.AllDirectories).ShouldNotBeEmpty();
                calls.Add("export");
            });
        calls.ShouldBe(["database", "maps", "substitutions", "type-valuesets", "compare", "outcomes", "export"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LaterInvalidScheduledPairProtectsEarlierExistingIg(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair incomplete = fixture.PreparePair(2, targetIndex);
        ConfigXVer config = ExportConfig(fixture, targetIndex, "later-pair");
        XVerExporter exporter = new(fixture.Connection, config);
        IgExporter ig = new(fixture.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.ExportSchedule schedule = ig.BuildExportSchedule();
        FhirPackageComparisonPair first = schedule.PackagePairs[0];
        first.SequencePair.ShouldNotBe(incomplete.SequencePair);
        fixture.StampComparison(first);
        fixture.GenerateSafety(first);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, [first]);
        string root = Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-" + first.SourceFhirSequence.ToRLiteral().ToLowerInvariant() +
            "." + first.TargetFhirSequence.ToRLiteral().ToLowerInvariant());
        Directory.CreateDirectory(root);
        string sentinel = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve earlier IG");
        string before = DatabaseSnapshot(fixture);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => exporter.Export(includeIgScripts: false));
        error.Message.ShouldContain(incomplete.SourcePackage.PackageVersion);
        File.ReadAllText(sentinel).ShouldBe("preserve earlier IG");
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).ShouldBe([sentinel]);
        DatabaseSnapshot(fixture).ShouldBe(before);
    }

    [Fact]
    public void NonReloadExportDoesNotRegenerateAndReloadFailureStopsExport()
    {
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand("export", false,
            () => calls.Add("database"), () => calls.Add("maps"), () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"), _ => calls.Add("compare"), _ => calls.Add("outcomes"),
            () => calls.Add("export"));
        calls.ShouldBe(["export"]);
        calls.Clear();
        Should.Throw<InvalidOperationException>(() => XVerProcessor.ExecuteOutcomeCommand("export", true,
            () => calls.Add("database"), () => calls.Add("maps"), () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"), _ => calls.Add("compare"),
            _ => throw new InvalidOperationException("outcomes failed"), () => calls.Add("export")))
            .Message.ShouldBe("outcomes failed");
        calls.ShouldBe(["database", "maps", "substitutions", "type-valuesets", "compare"]);
    }

    [Theory]
    [InlineData("missing-schema")]
    [InlineData("old-schema")]
    [InlineData("missing-input")]
    [InlineData("partial-input")]
    [InlineData("incompatible-input")]
    [InlineData("missing-pair")]
    [InlineData("partial-comparison")]
    [InlineData("partial-outcome")]
    [InlineData("incompatible-outcome")]
    [InlineData("stale-input")]
    [InlineData("stale-comparison")]
    public void ExportRejectsInvalidScheduledPairsBeforeWriting(string invalid)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        fixture.GenerateSafety(pair);
        Corrupt(fixture, pair, invalid);
        string before = DatabaseSnapshot(fixture);
        ConfigXVer config = ExportConfig(fixture, 0, "fresh-export");
        XVerExporter exporter = new(fixture.Connection, config);
        // The vocabulary outcome schema is deliberately absent: combined export
        // must diagnose safety first, before even attempting vocabulary output.
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            exporter.Export(includeIgScripts: false, specificPairs: [pair.SequencePair]));
        error.Message.ShouldContain("Unsafe-code");
        error.Message.ShouldContain("compare");
        error.Message.ShouldContain("outcomes");
        error.Message.ShouldContain("export");
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        DatabaseSnapshot(fixture).ShouldBe(before);

        string igRoot = Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5.r4");
        Directory.CreateDirectory(igRoot);
        string sentinel = Path.Combine(igRoot, "sentinel.txt");
        byte[] contents = [0, 1, 2, 255];
        File.WriteAllBytes(sentinel, contents);
        Should.Throw<InvalidOperationException>(() =>
            exporter.Export(includeIgScripts: false, specificPairs: [pair.SequencePair]));
        File.ReadAllBytes(sentinel).ShouldBe(contents);
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).ShouldBe([sentinel]);
        DatabaseSnapshot(fixture).ShouldBe(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExportChecksAllAndOnlyActuallyScheduledPairs(int targetIndex)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair selected = fixture.PreparePair(2, targetIndex);
        fixture.GenerateSafety(selected);
        ConfigXVer config = ExportConfig(fixture, targetIndex, "selected");
        XVerExporter exporter = new(fixture.Connection, config);
        IgExporter ig = new(fixture.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.ExportSchedule schedule = ig.BuildExportSchedule(specificPairs: [selected.SequencePair]);
        schedule.PackagePairs.ShouldHaveSingleItem().SequencePair.ShouldBe(selected.SequencePair);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, schedule.PackagePairs);
        exporter.Export(includeIgScripts: false, processVocabulary: false, specificPairs: [selected.SequencePair]);
        Directory.Exists(Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5." +
            selected.TargetFhirSequence.ToRLiteral().ToLowerInvariant())).ShouldBeTrue();

        config.OutputDirectory = Path.Combine(fixture.SourceRoot, "all-pairs");
        exporter = new(fixture.Connection, config);
        Should.Throw<InvalidOperationException>(() => exporter.Export(includeIgScripts: false, processVocabulary: false));
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();

        // With a one-step window, the R5->R4 pair is excluded, not certified.
        schedule = ig.BuildExportSchedule(maxStepSize: 1, specificPairs: [selected.SequencePair]);
        schedule.PackagePairs.Count.ShouldBe(targetIndex == 0 ? 0 : 1);
        UnsafeCodeInputStore.RequireExportReady(fixture.Connection, schedule.PackagePairs);
        config.OutputDirectory = Path.Combine(fixture.SourceRoot, "window");
        new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false, processVocabulary: false,
            maxStepSize: 1, specificPairs: [selected.SequencePair]);
        Directory.GetDirectories(config.OutputDirectory, "hl7.fhir.uv.xver-*").Length.ShouldBe(targetIndex == 0 ? 0 : 1);
    }

    [Fact]
    public void VocabularyOnlyExportBypassesMissingSafetySchema()
    {
        using UnsafeCodeTestFixture fixture = new();
        DbComparisonClasses.CreateTables(fixture.Connection);
        DbOutcomeClasses.CreateTables(fixture.Connection, forStructures: false);
        DbUnsafeCodeInputState.DropTable(fixture.Connection);
        DbUnsafeCodePairState.DropTable(fixture.Connection);
        ConfigXVer config = ExportConfig(fixture, 0, "vocabulary");
        FhirPackageComparisonPair pair = new(fixture.Packages[2], fixture.Packages[0]);
        new XVerExporter(fixture.Connection, config).Export(includeIgScripts: false,
            processStructures: false, specificPairs: [pair.SequencePair]);
        Directory.Exists(Path.Combine(config.OutputDirectory, "hl7.fhir.uv.xver-r5.r4")).ShouldBeTrue();
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('UnsafeCodeInputState', 'UnsafeCodePairState')";
        Convert.ToInt32(command.ExecuteScalar()).ShouldBe(0);
    }

    private static ConfigXVer ExportConfig(UnsafeCodeTestFixture fixture, int targetIndex, string directory) => new()
    {
        OutputDirectory = Path.Combine(fixture.SourceRoot, directory),
        ExportR2 = false, ExportR3 = false, ExportR4 = targetIndex == 0, ExportR4B = targetIndex == 1,
        ExportR5 = false, ExportR6 = false, LogFactory = NullLoggerFactory.Instance,
    };

    private static void Corrupt(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair, string invalid)
    {
        DbUnsafeCodeInputState input = fixture.InputState;
        DbUnsafeCodePairState state = fixture.PairState(pair);
        switch (invalid)
        {
            case "missing-schema":
                DbUnsafeCodeInputState.DropTable(fixture.Connection);
                return;
            case "old-schema":
                DbUnsafeCodeInputState.DropTable(fixture.Connection);
                using (SqliteCommand command = fixture.Connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE UnsafeCodeInputState (Key INTEGER PRIMARY KEY)";
                    command.ExecuteNonQuery();
                }
                return;
            case "missing-input": DbUnsafeCodeInputState.Delete(fixture.Connection); return;
            case "partial-input": input.CompletedInputRevision = null; break;
            case "incompatible-input": input.FormatVersion++; break;
            case "missing-pair":
                DbUnsafeCodePairState.Delete(fixture.Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
                return;
            case "partial-comparison": state.ComparisonRevision = null; break;
            case "partial-outcome": state.OutcomeInputRevision = null; break;
            case "incompatible-outcome": state.OutcomeFormatVersion++; break;
            case "stale-input": input.CompletedInputRevision = "new-input"; break;
            case "stale-comparison": state.ComparisonRevision = "new-comparison"; break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }
        DbUnsafeCodeInputState.Update(fixture.Connection, [input]);
        DbUnsafeCodePairState.Update(fixture.Connection, [state]);
    }

    private static string DatabaseSnapshot(UnsafeCodeTestFixture fixture)
    {
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> schema = [];
        while (reader.Read())
        {
            schema.Add(reader.GetString(0) + ":" + (reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        reader.Close();
        command.CommandText = "SELECT total_changes()";
        return string.Join("\n", schema) + ":" + command.ExecuteScalar();
    }

    [Theory]
    [InlineData("outcomes", null)]
    [InlineData("outcomes-vs", FhirArtifactClassEnum.ValueSet)]
    [InlineData("outcomes-sd", FhirArtifactClassEnum.Resource)]
    public void ReloadRunsActualDispatchWithSameFamilyComparisonFirst(string command, FhirArtifactClassEnum? expected)
    {
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand(
            command, true,
            () => calls.Add("database"),
            () => calls.Add("maps"),
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            family => { family.ShouldBe(expected); calls.Add("compare"); },
            family => { family.ShouldBe(expected); calls.Add("outcomes"); });
        calls.ShouldBe(["database", "maps", "substitutions", "type-valuesets", "compare", "outcomes"]);
    }

    [Theory]
    [InlineData("outcomes")]
    [InlineData("outcomes-vs")]
    [InlineData("outcomes-sd")]
    public void WithoutReloadOnlyRequestedOutcomesRun(string command)
    {
        List<string> calls = [];
        XVerProcessor.ExecuteOutcomeCommand(
            command, false,
            () => calls.Add("database"),
            () => calls.Add("maps"),
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            _ => calls.Add("compare"),
            _ => calls.Add("outcomes"));
        calls.ShouldBe(["outcomes"]);
    }

    [Fact]
    public void ReloadFailureDoesNotDispatchComparisonOrOutcomes()
    {
        List<string> calls = [];
        Should.Throw<InvalidOperationException>(() => XVerProcessor.ExecuteOutcomeCommand(
            "outcomes-sd", true,
            () => calls.Add("database"),
            () => throw new InvalidOperationException("load failed"),
            () => calls.Add("substitutions"),
            () => calls.Add("type-valuesets"),
            _ => calls.Add("compare"),
            _ => calls.Add("outcomes"))).Message.ShouldBe("load failed");
        calls.ShouldBe(["database"]);
    }
}
