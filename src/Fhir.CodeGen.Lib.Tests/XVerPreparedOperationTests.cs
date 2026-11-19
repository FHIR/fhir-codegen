// <copyright file="XVerPreparedOperationTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Hl7.FhirPath.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class XVerPreparedOperationTests(ITestOutputHelper output)
{
    private const string StructureUrl = "http://hl7.org/fhir/StructureDefinition/";
    private const string ConsumerName = "PreparedConsumer";
    private const string ComparatorTerminologyVersion = "1.0.0";
    private const int Repetitions = 3;
    private static readonly FhirReleases.FhirSequenceCodes[] _sequences =
    [
        FhirReleases.FhirSequenceCodes.DSTU2, FhirReleases.FhirSequenceCodes.STU3,
        FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R4B,
        FhirReleases.FhirSequenceCodes.R5,
    ];

    [Fact]
    public void PreparedComparisonUsesOneValidationAndNoFallbackCatalogs()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        fixture.Load().ShouldBeTrue();
        DbComparisonClasses.CreateTables(fixture.Connection);
        using ObservedOperandConnection connection = new(fixture.Connection);
        StructureComparer comparer = new(connection, NullLoggerFactory.Instance);
        HashSet<XVerPreparedOperation> operations = [];
        HashSet<XVerStructureCatalog> catalogs = [];
        int legacyCatalogReads = 0;
        int legacyValidationReads = 0;
        connection.BeforeExecute = sql =>
        {
            XVerPreparedOperation? current = PrivateField<XVerPreparedOperation?>(comparer, "_operation");
            if (current is not null)
            {
                operations.Add(current);
                catalogs.Add(current.Catalog);
            }
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                System.Reflection.MethodBase[] callers = [.. new StackTrace().GetFrames().Select(frame => frame.GetMethod()!)];
                if (callers.Any(method => method.DeclaringType == typeof(XVerStructureCatalog) &&
                    method.IsConstructor && method.GetParameters()[0].ParameterType == typeof(IDbConnection)))
                {
                    legacyCatalogReads++;
                }
                if (callers.Any(method => method.DeclaringType == typeof(DefinitionInventoryStore) &&
                    method.Name == "RequireReady" && method.GetParameters()[0].ParameterType == typeof(IDbConnection)))
                {
                    legacyValidationReads++;
                }
            }
        };
        comparer.CompareStructures(maxStepSize: 1);
        operations.ShouldHaveSingleItem();
        catalogs.ShouldHaveSingleItem();
        legacyCatalogReads.ShouldBe(0);
        legacyValidationReads.ShouldBe(0);
        // The sole prepared entry validates the captured image once. No consumer
        // may call that validator again or acquire a live catalog.
        CountCalls(typeof(XVerPreparedOperation).GetMethod("Prepare", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!, typeof(DefinitionInventoryStore), "RequireReady").ShouldBe(1);
        DbUnsafeCodePairState.SelectList(fixture.Connection).Count(pair => pair.ComparisonRevision != null).ShouldBe(4);
        connection.Subscriptions.ShouldBe(0);
    }

    [Fact]
    public void RepeatedConstrainedUsesReusePairFacts()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        (FhirPackageComparisonPair pair, _, DbElement source, DbElement target) = fixture.AddOwnerProjectionAlternatives(0, "profiles");
        AddCodingTypeMapping(fixture, pair);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.TypeComparison));
        XVerProfileWorkPacket packet = operation.GetComparisonPacket(pair.SourcePackageKey, pair.TargetPackageKey);
        ProfileUseFacts facts = packet.Facts;
        DbElementType sourceType = operation.Catalog.GetTypeUses(source)[0];
        ElementTypeComparer comparer = new(fixture.Connection, NullLoggerFactory.Instance, new());
        comparer.DoTypeComparisons(pair, source, target, 1, operation, packet);
        EffectiveProfileUse first = facts.AtUse(source, sourceType);
        StructureDefinition payload = operation.Catalog.ReadPayload(operation.Catalog.GetStructure(source.StructureKey));
        int definitions = PrivateField<System.Collections.IDictionary>(facts, "_definitions").Count;
        for (int index = 2; index < 5; index++)
        {
            comparer.DoTypeComparisons(pair, source, target, index, operation, packet);
            packet.Facts.ShouldBeSameAs(facts);
            facts.AtUse(source, sourceType).ShouldBeSameAs(first);
            PrivateField<System.Collections.IDictionary>(facts, "_definitions").Count.ShouldBe(definitions);
            operation.Catalog.ReadPayload(operation.Catalog.GetStructure(source.StructureKey)).ShouldBeSameAs(payload);
        }
        definitions.ShouldBeGreaterThan(0);
        operation.ReleasePairWorkingFacts(pair.SourcePackageKey, pair.TargetPackageKey);
        AssertNoPayloads(operation.Catalog);
        PrivateField<System.Collections.IDictionary>(facts, "_definitions").Count.ShouldBe(0);
    }

    [Fact]
    public void ReusedComparerEntersFresh()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        fixture.Load().ShouldBeTrue();
        DbComparisonClasses.CreateTables(fixture.Connection);
        using ObservedOperandConnection connection = new(fixture.Connection);
        StructureComparer comparer = new(connection, NullLoggerFactory.Instance);
        HashSet<XVerPreparedOperation> operations = [];
        connection.BeforeExecute = _ =>
        {
            XVerPreparedOperation? current = PrivateField<XVerPreparedOperation?>(comparer, "_operation");
            if (current is not null)
            {
                operations.Add(current);
            }
        };
        HashSet<(FhirReleases.FhirSequenceCodes, FhirReleases.FhirSequenceCodes)> pairs =
            [(fixture.Packages[2].DefinitionFhirSequence, fixture.Packages[1].DefinitionFhirSequence)];
        comparer.CompareStructures(specificPairs: pairs);
        DbElement element = DbElement.SelectList(fixture.Connection).First();
        element.Short = "fresh invocation, unchanged revision";
        DbElement.Update(fixture.Connection, [element]);
        comparer.CompareStructures(specificPairs: pairs);
        operations.Count.ShouldBe(2);
        DbStructureDefinitionFacts facts = DbStructureDefinitionFacts.SelectList(fixture.Connection).First();
        facts.StructureDefinitionJson = "{}";
        DbStructureDefinitionFacts.Update(fixture.Connection, [facts]);
        Should.Throw<InvalidOperationException>(() => comparer.CompareStructures(specificPairs: pairs));
        connection.Subscriptions.ShouldBe(0);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("stable", false)]
    [InlineData("update", false)]
    [InlineData("insert", false)]
    [InlineData("delete", false)]
    [InlineData("update", true)]
    [InlineData("stable", true)]
    [InlineData("insert", true)]
    [InlineData("delete", true)]
    [InlineData("second-update", false)]
    [InlineData("second-update", true)]
    public void LegitimateComparisonWritesDoNotRebaseStableInputs(string mutation, bool secondConnection)
    {
        using UnsafeCodeTestFixture fixture = new(persistDatabase: true);
        fixture.Load().ShouldBeTrue();
        AddRelationshipMappings(fixture);
        using ComparisonDatabase database = new(fixture.SourceRoot, "snapshot.sqlite", NullLoggerFactory.Instance);
        SqliteConnection.ClearPool((SqliteConnection)database.DbConnection);
        using ObservedOperandConnection connection = new((SqliteConnection)database.DbConnection);
        FhirDbComparer comparer = new(database, NullLoggerFactory.Instance);
        typeof(FhirDbComparer).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(comparer, connection);
        using SqliteConnection other = new(fixture.Connection.ConnectionString);
        other.Open();
        IDbConnection writer = secondConnection ? other : connection;
        bool injected = false;
        List<string> relationshipPasses = [];
        connection.AfterExecute = sql =>
        {
            if (!sql.TrimStart().StartsWith("UPDATE ValueSetConceptComparisons", StringComparison.Ordinal))
            {
                return;
            }
            relationshipPasses.Add(sql);
            if (mutation == "none" || injected || mutation == "second-update" && relationshipPasses.Count != 2)
            {
                return;
            }
            injected = true;
            long count = ReadOperandInteger(writer, "SELECT COUNT(*) FROM ValueSetConceptComparisons");
            string input = ReadOperandText(writer, "SELECT CompletedInputRevision FROM UnsafeCodeInputState");
            string changes = mutation switch
            {
                "stable" => "UPDATE Elements SET Short = 'unexpected stable change' WHERE Key = (SELECT min(Key) FROM Elements)",
                "update" or "second-update" => "UPDATE ValueSetConceptComparisons SET UserMessage = 'unexpected same-pair change' WHERE Key = (SELECT min(Key) FROM ValueSetConceptComparisons)",
                "delete" => "DELETE FROM ValueSetConceptComparisons WHERE Key = (SELECT min(Key) FROM ValueSetConceptComparisons)",
                "insert" => string.Empty,
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
            if (mutation == "insert")
            {
                DbValueSetConceptComparison row = DbValueSetConceptComparison.SelectList(writer).First();
                row.Key += 100000;
                DbValueSetConceptComparison.Insert(writer, [row], insertPrimaryKey: true);
            }
            else
            {
                ExecuteOperandSql(writer, changes);
            }
            if (mutation is "update" or "second-update" or "stable")
            {
                ReadOperandInteger(writer, "SELECT COUNT(*) FROM ValueSetConceptComparisons").ShouldBe(count);
            }
            ReadOperandText(writer, "SELECT CompletedInputRevision FROM UnsafeCodeInputState").ShouldBe(input);
        };
        if (mutation == "none")
        {
            comparer.Compare(maxStepSize: 1);
            relationshipPasses.Count.ShouldBe(8);
            for (int index = 0; index < relationshipPasses.Count; index += 2)
            {
                relationshipPasses[index].ShouldContain("SourceIsNarrowerThanTarget");
                relationshipPasses[index + 1].ShouldContain("SourceIsBroaderThanTarget");
            }
            DbValueSetConceptComparison.SelectList(connection).ShouldContain(row =>
                row.Relationship == ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget);
            DbValueSetConceptComparison.SelectList(connection).ShouldContain(row =>
                row.Relationship == ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget);
            DbValueSetConceptComparison[] ordered = [.. DbValueSetConceptComparison.SelectList(connection,
                SourceFhirPackageKey: fixture.Packages[0].Key, TargetFhirPackageKey: fixture.Packages[1].Key)];
            ordered.Where(row => row.SourceCode == "local-a").ShouldAllBe(row =>
                row.Relationship == ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget);
            ordered.Where(row => row.SourceCode == "local-b").ShouldHaveSingleItem().Relationship
                .ShouldBe(ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget);
            DbUnsafeCodePairState.SelectList(connection).Count(row => row.ComparisonRevision != null).ShouldBe(4);
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => comparer.Compare(maxStepSize: 1)).Message.ShouldContain("changed");
            injected.ShouldBeTrue();
            DbUnsafeCodePairState.SelectList(connection).ShouldAllBe(row => row.ComparisonRevision == null);
        }
        connection.Subscriptions.ShouldBe(0);
    }

    [Theory]
    [InlineData("update", false)]
    [InlineData("insert", false)]
    [InlineData("delete", false)]
    [InlineData("update", true)]
    public void ComparisonFlushRejectsSamePairOutputDriftBeforeStamp(string mutation, bool secondConnection)
    {
        using UnsafeCodeTestFixture fixture = new(persistDatabase: true);
        fixture.Load().ShouldBeTrue();
        DbComparisonClasses.CreateTables(fixture.Connection);
        using ObservedOperandConnection connection = new(fixture.Connection);
        using SqliteConnection other = new(fixture.Connection.ConnectionString);
        other.Open();
        IDbConnection writer = secondConnection ? other : connection;
        bool injected = false;
        bool flushed = false;
        connection.AfterExecute = sql =>
        {
            if (sql.TrimStart().StartsWith("INSERT OR IGNORE INTO ElementComparisons", StringComparison.Ordinal))
            {
                flushed = true;
            }
        };
        connection.BeforeExecute = sql =>
        {
            // Generated batch helpers commit before the auditor's next observation.
            if (injected || !flushed || sql != "PRAGMA data_version")
            {
                return;
            }
            injected = true;
            long count = ReadOperandInteger(writer, "SELECT COUNT(*) FROM ElementComparisons");
            switch (mutation)
            {
                case "update":
                    ExecuteOperandSql(writer, "UPDATE ElementComparisons SET UserMessage = '' WHERE Key = (SELECT min(Key) FROM ElementComparisons)");
                    ReadOperandInteger(writer, "SELECT COUNT(*) FROM ElementComparisons").ShouldBe(count);
                    break;
                case "delete":
                    ExecuteOperandSql(writer, "DELETE FROM ElementComparisons WHERE Key = (SELECT min(Key) FROM ElementComparisons)");
                    break;
                case "insert":
                    DbElementComparison row = DbElementComparison.SelectList(writer).First();
                    row.Key += 100000;
                    DbElementComparison.Insert(writer, [row], insertPrimaryKey: true);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        };
        StructureComparer comparer = new(connection, NullLoggerFactory.Instance);
        FhirPackageComparisonPair pair = new(fixture.Packages[0], fixture.Packages[1]);
        Should.Throw<InvalidOperationException>(() => comparer.CompareStructures(specificPairs: [pair.SequencePair]))
            .Message.ShouldContain("ElementComparisons");
        injected.ShouldBeTrue();
        DbUnsafeCodePairState.SelectList(connection).ShouldAllBe(row => row.ComparisonRevision == null);
        connection.Subscriptions.ShouldBe(0);
    }

    [Fact]
    public void VocabularyProducerFlushCopiesInsertsAndUpdatesBeforeClearing()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        AddRelationshipMappings(fixture);
        DbComparisonClasses.CreateTables(fixture.Connection);
        ValueSetComparer vocabulary = new(fixture.Connection, NullLoggerFactory.Instance);
        vocabulary.CompareValueSets(maxStepSize: 1);
        DbValueSetComparison valueSet = DbValueSetComparison.SelectList(fixture.Connection,
            SourceFhirPackageKey: fixture.Packages[0].Key, TargetFhirPackageKey: fixture.Packages[1].Key).First();
        DbValueSetConceptComparison concept = DbValueSetConceptComparison.SelectList(fixture.Connection,
            SourceFhirPackageKey: fixture.Packages[0].Key, TargetFhirPackageKey: fixture.Packages[1].Key).First();
        valueSet.UserMessage = "intended value set update";
        concept.UserMessage = "intended concept update";
        // The current algorithm only enqueues additions. Exercise its existing
        // update queues explicitly without changing the production algorithm.
        PrivateField<DbComparisonCache<DbValueSetComparison>>(vocabulary, "_vsComparisonCache").CacheUpdate(valueSet);
        PrivateField<DbComparisonCache<DbValueSetConceptComparison>>(vocabulary, "_conceptComparisonCache").CacheUpdate(concept);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Comparison, maxStepSize: 1));
        vocabulary.CompareValueSets(operation, maxStepSize: 1);
        DbValueSetComparison.SelectSingle(fixture.Connection, Key: valueSet.Key)!.UserMessage.ShouldBe(valueSet.UserMessage);
        DbValueSetConceptComparison.SelectSingle(fixture.Connection, Key: concept.Key)!.UserMessage.ShouldBe(concept.UserMessage);
        PrivateField<DbComparisonCache<DbValueSetComparison>>(vocabulary, "_vsComparisonCache").Count.ShouldBe(0);
        PrivateField<DbComparisonCache<DbValueSetConceptComparison>>(vocabulary, "_conceptComparisonCache").Count.ShouldBe(0);
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(operation, maxStepSize: 1);
        operation.Audit();
        DbUnsafeCodePairState.SelectList(fixture.Connection).Count(row => row.ComparisonRevision != null).ShouldBe(4);
    }

    [Theory]
    [InlineData("inputs")]
    [InlineData("comparison")]
    [InlineData("begin")]
    [InlineData("complete")]
    public void PreparedComparisonGuardsAuditRawOutputsAtEveryBoundary(string boundary)
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        DbComparisonClasses.CreateTables(fixture.Connection);
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(maxStepSize: 1);
        FhirPackageComparisonPair pair = new(fixture.Packages[0], fixture.Packages[1]);
        using ObservedOperandConnection connection = new(fixture.Connection);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(connection, new(XVerPreparationKind.Comparison));
        string revision = UnsafeCodeInputStore.RequireInputs(connection, operation);
        UnsafeCodeInputStore.RequireComparison(connection, pair.SourcePackageKey, pair.TargetPackageKey, operation)
            .ComparisonRevision.ShouldNotBeNullOrEmpty();
        if (boundary == "complete")
        {
            UnsafeCodeInputStore.BeginComparison(connection, pair.SourcePackageKey, pair.TargetPackageKey, operation).ShouldBe(revision);
        }
        ExecuteOperandSql(connection, "UPDATE ElementComparisons SET UserMessage = 'same count, unchanged stamps' " +
            "WHERE Key = (SELECT min(Key) FROM ElementComparisons)");
        Should.Throw<InvalidOperationException>(() =>
        {
            switch (boundary)
            {
                case "inputs": UnsafeCodeInputStore.RequireInputs(connection, operation); break;
                case "comparison": UnsafeCodeInputStore.RequireComparison(connection, pair.SourcePackageKey, pair.TargetPackageKey, operation); break;
                case "begin": UnsafeCodeInputStore.BeginComparison(connection, pair.SourcePackageKey, pair.TargetPackageKey, operation); break;
                case "complete": UnsafeCodeInputStore.CompleteComparison(connection, pair.SourcePackageKey, pair.TargetPackageKey, revision, operation); break;
                default: throw new ArgumentOutOfRangeException(nameof(boundary));
            }
        }).Message.ShouldContain("ElementComparisons");
        connection.Subscriptions.ShouldBe(0);
    }

    [Fact]
    public void PreparedComparisonCannotBorrowAnotherConnection()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        using UnsafeCodeTestFixture other = new();
        using ObservedOperandConnection connection = new(fixture.Connection);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(connection, new(XVerPreparationKind.Comparison));
        long changes = ReadOperandInteger(other.Connection, "SELECT total_changes()");
        Should.Throw<InvalidOperationException>(() => UnsafeCodeInputStore.RequireInputs(other.Connection, operation))
            .Message.ShouldContain("exact connection");
        ReadOperandInteger(other.Connection, "SELECT total_changes()").ShouldBe(changes);
        connection.Subscriptions.ShouldBe(0);
    }

    [Fact]
    public void VocabularyPostProcessingRetainsRawUnchangedFieldsAndSqlNullGroups()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        AddRelationshipMappings(fixture);
        DbComparisonClasses.CreateTables(fixture.Connection);
        new ValueSetComparer(fixture.Connection, NullLoggerFactory.Instance).CompareValueSets(maxStepSize: 1);
        FhirPackageComparisonPair pair = new(fixture.Packages[0], fixture.Packages[1]);
        string filter = $"SourceFhirPackageKey = {pair.SourcePackageKey} AND TargetFhirPackageKey = {pair.TargetPackageKey}";
        ExecuteOperandSql(fixture.Connection, $"UPDATE ValueSetConceptComparisons SET Relationship = 'Equivalent', " +
            $"NotMapped = 2, UserMessage = '', TechnicalMessage = '{{  }}', TargetValueSetKey = NULL WHERE {filter}");
        ExecuteOperandSql(fixture.Connection, $"UPDATE ValueSetConceptComparisons SET TargetConceptKey = NULL " +
            $"WHERE {filter} AND SourceCode = 'local-a' AND TargetCode = 'local-b'");
        List<string> unchanged = ReadUnchanged();
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Comparison));
        ValueSetComparer vocabulary = new(fixture.Connection, NullLoggerFactory.Instance);
        typeof(ValueSetComparer).GetField("_operation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(vocabulary, operation);
        typeof(ValueSetComparer).GetMethod("doConceptPostProcessing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vocabulary, [pair.SourcePackage, pair.TargetPackage]);
        ReadUnchanged().ShouldBe(unchanged);
        List<DbValueSetConceptComparison> rows = DbValueSetConceptComparison.SelectList(fixture.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
        rows.Where(row => row.TargetConceptKey != null).ShouldAllBe(row =>
            row.Relationship == ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget);
        rows.Single(row => row.TargetConceptKey == null).Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.Equivalent);
        operation.Audit();

        List<string> ReadUnchanged()
        {
            XVerOperandEvidence.XVerTableManifest manifest = XVerOperandEvidence.Manifest.Single(table => table.RowType == typeof(DbValueSetConceptComparison));
            using IDbCommand command = fixture.Connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", manifest.PersistedFields.Where(field => field != "Relationship"))} " +
                "FROM ValueSetConceptComparisons ORDER BY Key";
            using IDataReader reader = command.ExecuteReader();
            List<string> result = [];
            while (reader.Read())
            {
                object?[] fields = new object?[reader.FieldCount];
                for (int index = 0; index < fields.Length; index++)
                {
                    fields[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }
                result.Add(JsonSerializer.Serialize(fields));
            }
            return result;
        }
    }

    [Fact]
    public void FilteredComparisonStillInvalidatesUnscheduledPairs()
    {
        using UnsafeCodeTestFixture fixture = new(persistDatabase: true);
        fixture.Load().ShouldBeTrue();
        using ComparisonDatabase database = new(fixture.SourceRoot, "snapshot.sqlite", NullLoggerFactory.Instance);
        SqliteConnection.ClearPool((SqliteConnection)database.DbConnection);
        FhirDbComparer comparer = new(database, NullLoggerFactory.Instance);
        comparer.Compare(maxStepSize: 1);
        List<DbUnsafeCodePairState> pairs = DbUnsafeCodePairState.SelectList(database.DbConnection);
        foreach (DbUnsafeCodePairState pair in pairs)
        {
            pair.OutcomeInputRevision = pair.InputRevision;
            pair.OutcomeComparisonRevision = pair.ComparisonRevision;
            pair.OutcomeFormatVersion = UnsafeCodeConstants.OutcomeFormatVersion;
        }
        DbUnsafeCodePairState.Update(database.DbConnection, pairs);
        FhirPackageComparisonPair selected = new(fixture.Packages[2], fixture.Packages[1]);
        comparer.Compare(processValueSets: false, specificPairs: [selected.SequencePair]);
        DbUnsafeCodePairState.SelectList(database.DbConnection).Where(row =>
            row.SourceFhirPackageKey != selected.SourcePackageKey || row.TargetFhirPackageKey != selected.TargetPackageKey)
            .ShouldAllBe(row => row.ComparisonRevision == null && row.OutcomeInputRevision == null &&
                row.OutcomeComparisonRevision == null && row.OutcomeFormatVersion == null);
        DbUnsafeCodePairState.SelectList(database.DbConnection).Single(row =>
            row.SourceFhirPackageKey == selected.SourcePackageKey && row.TargetFhirPackageKey == selected.TargetPackageKey)
            .ComparisonRevision.ShouldNotBeNullOrEmpty();
        DbStructureComparison.SelectList(database.DbConnection).ShouldAllBe(row =>
            row.SourceFhirPackageKey == selected.SourcePackageKey && row.TargetFhirPackageKey == selected.TargetPackageKey);
    }

    private static void AddCodingTypeMapping(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair)
    {
        DbStructureDefinition source = fixture.Structure(pair.SourcePackage, "Coding");
        DbStructureDefinition target = fixture.Structure(pair.TargetPackage, "Coding");
        DbStructureMapping.Insert(fixture.Connection, new DbStructureMapping
        {
            SourceFhirPackageKey = pair.SourcePackageKey, TargetFhirPackageKey = pair.TargetPackageKey,
            SourceFhirSequence = pair.SourceFhirSequence, TargetFhirSequence = pair.TargetFhirSequence,
            SourceStructureKey = source.Key, TargetStructureKey = target.Key,
            SourceStructureId = source.Id, TargetStructureId = target.Id,
            SourceStructureUrl = source.UnversionedUrl, TargetStructureUrl = target.UnversionedUrl,
            ConceptMapSourceKey = null, ConceptMapFilename = null, FmlSourceKey = null, FmlFilename = null,
            Relationship = ConceptMap.ConceptMapRelationship.Equivalent, ExplicitNoMap = false, IsFallback = false,
            ConceptDomainRelationship = null, ValueDomainRelationship = null,
        });
    }

    private static void AddRelationshipMappings(UnsafeCodeTestFixture fixture)
    {
        // Many-to-many equivalent edges make the two ordered SQL passes observable.
        foreach (DbFhirPackage package in fixture.Packages)
        {
            DbValueSet valueSet = DbValueSet.SelectList(fixture.Connection, FhirPackageKey: package.Key).Single();
            foreach (string code in new[] { "local-a", "local-b" })
            {
                DbValueSetConcept concept = DbValueSetConcept.EmptyCopy;
                concept.Key = DbValueSetConcept.GetIndex();
                concept.FhirPackageKey = package.Key;
                concept.ValueSetKey = valueSet.Key;
                concept.System = "urn:prepared-relationship-codes";
                concept.SystemVersion = "1";
                concept.Code = code;
                concept.Display = code;
                DbValueSetConcept.Insert(fixture.Connection, [concept], insertPrimaryKey: true);
            }
        }
        FhirPackageComparisonPair pair = new(fixture.Packages[0], fixture.Packages[1]);
        DbValueSet source = DbValueSet.SelectList(fixture.Connection, FhirPackageKey: pair.SourcePackageKey).Single();
        DbValueSet target = DbValueSet.SelectList(fixture.Connection, FhirPackageKey: pair.TargetPackageKey).Single();
        DbValueSetMapping mapping = new()
        {
            SourceFhirPackageKey = pair.SourcePackageKey, TargetFhirPackageKey = pair.TargetPackageKey,
            SourceFhirSequence = pair.SourceFhirSequence, TargetFhirSequence = pair.TargetFhirSequence,
            SourceValueSetKey = source.Key, SourceValueSetId = source.Id,
            SourceValueSetUrl = source.UnversionedUrl, SourceValueSetVersion = source.Version,
            TargetValueSetKey = target.Key, TargetValueSetId = target.Id,
            TargetValueSetUrl = target.UnversionedUrl, TargetValueSetVersion = target.Version,
            ConceptMapSourceKey = null, ConceptMapFilename = null, FmlSourceKey = null, FmlFilename = null,
            Relationship = ConceptMap.ConceptMapRelationship.Equivalent, ExplicitNoMap = false,
        };
        mapping.Key = DbValueSetMapping.Insert(fixture.Connection, mapping);
        List<DbValueSetConcept> sources = DbValueSetConcept.SelectList(fixture.Connection, ValueSetKey: source.Key);
        List<DbValueSetConcept> targets = DbValueSetConcept.SelectList(fixture.Connection, ValueSetKey: target.Key);
        foreach ((int from, int to) in new[] { (0, 0), (0, 1), (1, 0) })
        {
            DbValueSetConceptMapping.Insert(fixture.Connection, new DbValueSetConceptMapping
            {
                SourceFhirPackageKey = pair.SourcePackageKey, TargetFhirPackageKey = pair.TargetPackageKey,
                SourceFhirSequence = pair.SourceFhirSequence, TargetFhirSequence = pair.TargetFhirSequence,
                ValueSetMappingKey = mapping.Key, SourceValueSetKey = source.Key, TargetValueSetKey = target.Key,
                SourceValueSetConceptKey = sources[from].Key, TargetValueSetConceptKey = targets[to].Key,
                SourceSystem = sources[from].System, TargetSystem = targets[to].System,
                SourceCode = sources[from].Code, TargetCode = targets[to].Code,
                ConceptMapSourceKey = null, ConceptMapFilename = null, FmlSourceKey = null, FmlFilename = null,
                Relationship = ConceptMap.ConceptMapRelationship.Equivalent, ExplicitNoMap = false,
            });
        }
    }

    private static int CountCalls(System.Reflection.MethodInfo method, Type owner, string name)
    {
        byte[] code = method.GetMethodBody()!.GetILAsByteArray()!;
        int count = 0;
        for (int index = 0; index < code.Length - 4; index++)
        {
            if (code[index] != 0x28)
            {
                continue;
            }
            try
            {
                System.Reflection.MethodBase? called = method.Module.ResolveMethod(BitConverter.ToInt32(code, index + 1));
                if (called?.DeclaringType == owner && called.Name == name)
                {
                    count++;
                }
            }
            catch (ArgumentException)
            {
                // Operand bytes are not opcodes; only resolved calls count.
            }
        }
        return count;
    }

    [Fact]
    public void BorrowerDisposalDoesNotClearAnotherBorrowersPayloads()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        using XVerStructureCatalog catalog = new(fixture.Connection);
        DbStructureDefinition owner = fixture.Structure(fixture.Packages[2], "Coding");
        StructureDefinition payload = catalog.ReadPayload(owner);
        using ProfileUseFacts first = new(catalog, fixture.Packages[2].Key, fixture.Packages[0].Key);
        using ProfileUseFacts second = new(catalog, fixture.Packages[2].Key, fixture.Packages[0].Key);
        EffectiveProfileBranch branch = second.ForDefinition(owner);
        first.ForDefinition(owner);
        first.Dispose();
        catalog.ReadPayload(owner).ShouldBeSameAs(payload);
        second.ForDefinition(owner).Elements.ShouldBeSameAs(branch.Elements);
        catalog.ReleasePayloads();
        catalog.ReadPayload(owner).ShouldNotBeSameAs(payload);
        branch.Elements.ShouldContainKey(".code");
    }

    [Fact]
    public void ComparisonClassificationAndProjectionContextsNeverAlias()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Construction));
        XVerDirectionalOperands operands = operation.CaptureDirection(pair.SourcePackageKey, pair.TargetPackageKey);
        XVerProfileWorkPacket comparison = operation.GetComparisonPacket(pair.SourcePackageKey, pair.TargetPackageKey);
        XVerProfileWorkPacket classification = operation.GetPacket(operands, XVerProfileContext.Classification);
        int ownerKey = fixture.Structure(pair.TargetPackage).Key;
        XVerProfileContext projectionContext = XVerProfileContext.ForOwner(ownerKey, 100, [ownerKey]);
        XVerProfileWorkPacket projection = operation.GetPacket(operands, projectionContext);
        comparison.Facts.ShouldNotBeSameAs(classification.Facts);
        classification.Facts.ShouldNotBeSameAs(projection.Facts);
        comparison.Facts.ShouldNotBeSameAs(projection.Facts);
        Should.Throw<InvalidOperationException>(() => operation.GetPacket(operands, XVerProfileContext.Comparison));
        operation.GetPacket(operands, XVerProfileContext.ForOwner(ownerKey, 100, [ownerKey])).ShouldBeSameAs(projection);
        operation.GetPacket(operands, XVerProfileContext.ForOwner(ownerKey, 101, [ownerKey])).ShouldNotBeSameAs(projection);
        operation.GetPacket(operands, XVerProfileContext.ForOwner(ownerKey, 100, [ownerKey, ownerKey + 1])).ShouldNotBeSameAs(projection);
        Should.Throw<InvalidOperationException>(() => comparison.Classify());
        Should.Throw<InvalidOperationException>(() => projection.Classify());
        using XVerPreparedOperation independent = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Construction));
        Should.Throw<InvalidOperationException>(() => independent.GetPacket(operands, XVerProfileContext.Classification));
        operation.Dispose();
        Should.Throw<ObjectDisposedException>(() => { _ = classification.Facts; });
    }

    [Fact]
    public void ComparisonPacketsDoNotAcquireCorrespondencePrerequisites()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        DefinitionInventoryStore.TableExists(fixture.Connection, DbElementComparison.DefaultTableName).ShouldBeFalse();
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.TypeComparison));
        XVerProfileWorkPacket packet = operation.GetComparisonPacket(fixture.Packages[2].Key, fixture.Packages[0].Key);
        packet.Operands.HasCorrespondence.ShouldBeFalse();
        packet.Facts.ForDefinition(fixture.Structure(fixture.Packages[2], "Coding")).Elements.ShouldContainKey(".code");
        operation.GetComparisonPacket(fixture.Packages[2].Key, fixture.Packages[0].Key).ShouldBeSameAs(packet);
        operation.ReleasePairWorkingFacts(fixture.Packages[2].Key, fixture.Packages[0].Key);
        AssertNoPayloads(operation.Catalog);
        DefinitionInventoryStore.TableExists(fixture.Connection, DbElementComparison.DefaultTableName).ShouldBeFalse();
    }

    [Fact]
    public void DirectionalProductsTrackExactReverseOperandEpochs()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        FhirPackageComparisonPair reversePair = new(pair.TargetPackage, pair.SourcePackage);
        DbElement source = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.SourcePackageKey, Id: "Coding.code")!;
        DbElement target = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: pair.TargetPackageKey, Id: "Coding.code")!;
        DbStructureOutcome owner = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage, "Coding"), fixture.Structure(pair.TargetPackage, "Coding"));
        fixture.Connect(pair, owner, source, target);
        DbStructureOutcome reverseOwner = fixture.AddOutcome(reversePair, fixture.Structure(pair.TargetPackage, "Coding"), fixture.Structure(pair.SourcePackage, "Coding"));
        fixture.Connect(reversePair, reverseOwner, target, source);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Construction));
        XVerDirectionalProduct Read(FhirPackageComparisonPair direction) => operation.GetPacket(
            operation.CaptureDirection(direction.SourcePackageKey, direction.TargetPackageKey),
            XVerProfileContext.Classification).Classify();
        XVerDirectionalProduct forward = Read(pair);
        XVerDirectionalProduct reverse = Read(reversePair);
        operation.ComposePair(forward, reverse).ShouldBe(forward.Decisions);
        Read(pair).ShouldBeSameAs(forward);
        Read(reversePair).ShouldBeSameAs(reverse);

        DbElementComparison row = DbElementComparison.SelectList(fixture.Connection,
            SourceFhirPackageKey: reversePair.SourcePackageKey, TargetFhirPackageKey: reversePair.TargetPackageKey).ShouldHaveSingleItem();
        row.UserMessage = ""; // Null and empty are different operand epochs, even when proof meaning agrees.
        DbElementComparison.Update(fixture.Connection, [row]);
        XVerDirectionalOperands nextOperands = operation.CaptureDirection(reversePair.SourcePackageKey, reversePair.TargetPackageKey);
        operation.ReplaceScheduledDirection(nextOperands, new(XVerOperandFamily.StructureComparisons, XVerOutputTransitionKind.PairFlush,
            reversePair.SourcePackageKey, reversePair.TargetPackageKey));
        XVerDirectionalProduct nextReverse = Read(reversePair);
        nextReverse.ShouldNotBeSameAs(reverse);
        Read(pair).ShouldBeSameAs(forward);
        Should.Throw<InvalidOperationException>(() => operation.ComposePair(forward, reverse));
        operation.ComposePair(forward, nextReverse).ShouldBe(forward.Decisions);
        Should.Throw<InvalidOperationException>(() => operation.ComposePair(forward, forward));
        Should.Throw<InvalidOperationException>(operation.Audit); // Computing an epoch does not authorize an undeclared write.

        using XVerPreparedOperation fresh = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Construction));
        fresh.GetPacket(fresh.CaptureDirection(reversePair.SourcePackageKey, reversePair.TargetPackageKey), XVerProfileContext.Classification).Classify();
        XVerStructureCatalog freshCatalog = fresh.Catalog;
        freshCatalog.ReadPayload(fixture.Structure(pair.SourcePackage));
        row.UserMessage = null;
        DbElementComparison.Update(fixture.Connection, [row]);
        XVerDirectionalOperands unexpected = fresh.CaptureDirection(reversePair.SourcePackageKey, reversePair.TargetPackageKey);
        Should.Throw<InvalidOperationException>(() => fresh.GetPacket(unexpected, XVerProfileContext.Classification));
        AssertNoPayloads(freshCatalog);
        Should.Throw<ObjectDisposedException>(() => { _ = fresh.Catalog; });
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("facts")]
    [InlineData("native-facts")]
    [InlineData("reasons")]
    [InlineData("children")]
    [InlineData("owner")]
    [InlineData("pair")]
    [InlineData("occurrence")]
    [InlineData("canonical")]
    [InlineData("usability")]
    [InlineData("target-context")]
    [InlineData("target-usability")]
    [InlineData("structure-usability")]
    [InlineData("structure-canonical")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("malformed")]
    public void PersistedVerifierRejectsTamperAfterProductWarmup(string mutation)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        (FhirPackageComparisonPair pair, DbStructureOutcome owner, DbElement source, DbElement target) =
            fixture.AddOwnerProjectionAlternatives(0, "profiles");
        fixture.GenerateSafety(pair);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(fixture.Connection, new(XVerPreparationKind.Construction));
        XVerDirectionalProduct Read(int from, int to) => operation.GetPacket(
            operation.CaptureDirection(from, to),
            XVerProfileContext.Classification).Classify();
        IReadOnlyList<ProfileRepresentationProof> expected = operation.ComposePair(
            Read(pair.SourcePackageKey, pair.TargetPackageKey), Read(pair.TargetPackageKey, pair.SourcePackageKey));
        operation.ReleasePairWorkingFacts(pair.SourcePackageKey, pair.TargetPackageKey);
        AssertNoPayloads(operation.Catalog);
        ProfileUseFacts.RequirePersistedDecisions(fixture.Connection, pair, expected, operation.Catalog);
        DbElementOutcome outcome = DbElementOutcome.SelectSingle(fixture.Connection, SourceElementKey: source.Key)!;
        JsonObject document = JsonNode.Parse(outcome.ExtensionProhibitionReason!)!.AsObject();
        JsonObject decision = document["Decisions"]![0]!.AsObject();
        switch (mutation)
        {
            case "envelope": document["ProfileDecisionFormat"] = 2; break;
            case "facts": decision["RequiredFacts"]![""]!["Maximum"] = 19; break;
            case "native-facts": decision["NativeFacts"]![""]!["Maximum"] = 19; break;
            case "reasons": decision["Reasons"]![0]!["Detail"] = "well-formed but wrong"; break;
            case "children": decision["Children"]!.AsArray().Add(decision.DeepClone()); break;
            case "owner": decision["Input"]!["OwnerKey"] = owner.TargetStructureKey; break;
            case "pair": decision["TargetPackageKey"] = pair.SourcePackageKey; break;
            case "occurrence": decision["Input"]!["ElementKey"] = target.Key; break;
            case "canonical": decision["TargetCanonical"] = "urn:forged"; break;
            case "usability": outcome.FullyMapsToThisTarget = true; break;
            case "target-context":
                DbElementOutcomeTarget row = DbElementOutcomeTarget.SelectList(fixture.Connection, ElementOutcomeKey: outcome.Key).ShouldHaveSingleItem();
                row.CardinalityContextParentExtensionUrl = "urn:forged";
                DbElementOutcomeTarget.Update(fixture.Connection, [row]);
                break;
            case "target-usability":
                DbElementOutcomeTarget usable = DbElementOutcomeTarget.SelectList(fixture.Connection, ElementOutcomeKey: outcome.Key).ShouldHaveSingleItem();
                usable.FullyMapsToThisTarget = true;
                DbElementOutcomeTarget.Update(fixture.Connection, [usable]);
                break;
            case "structure-usability":
                owner.IsEquivalent = true;
                DbStructureOutcome.Update(fixture.Connection, [owner]);
                break;
            case "structure-canonical":
                owner.TargetCanonicalUnversioned = "urn:forged";
                DbStructureOutcome.Update(fixture.Connection, [owner]);
                break;
            case "missing": document["Decisions"] = new JsonArray(); break;
            case "duplicate": document["Decisions"]!.AsArray().Add(decision.DeepClone()); break;
            case "extra":
                JsonObject extra = decision.DeepClone().AsObject();
                extra["Input"]!["ProfileAlternative"] = "urn:forged";
                document["Decisions"]!.AsArray().Add(extra);
                break;
            case "malformed": break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        outcome.ExtensionProhibitionReason = mutation == "malformed" ? "{\"ProfileDecisionFormat\":" :
            document.ToJsonString(new() { WriteIndented = false });
        DbElementOutcome.Update(fixture.Connection, [outcome]);
        Should.Throw<InvalidOperationException>(() =>
            ProfileUseFacts.RequirePersistedDecisions(fixture.Connection, pair, expected, operation.Catalog));
    }

    [Fact]
    public void LegacyTypeComparerReleasesPayloadsOnSuccessExceptionAndReuse()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        (FhirPackageComparisonPair pair, _, DbElement source, DbElement target) = fixture.AddOwnerProjectionAlternatives(0, "profiles");
        DbStructureDefinition sourceType = fixture.Structure(pair.SourcePackage, "Coding");
        DbStructureDefinition targetType = fixture.Structure(pair.TargetPackage, "Coding");
        DbStructureMapping.Insert(fixture.Connection, new DbStructureMapping
        {
            SourceFhirPackageKey = pair.SourcePackageKey, TargetFhirPackageKey = pair.TargetPackageKey,
            SourceFhirSequence = pair.SourceFhirSequence, TargetFhirSequence = pair.TargetFhirSequence,
            SourceStructureKey = sourceType.Key, TargetStructureKey = targetType.Key,
            SourceStructureId = sourceType.Id, TargetStructureId = targetType.Id,
            SourceStructureUrl = sourceType.UnversionedUrl, TargetStructureUrl = targetType.UnversionedUrl,
            ConceptMapSourceKey = null, ConceptMapFilename = null, FmlSourceKey = null, FmlFilename = null,
            Relationship = ConceptMap.ConceptMapRelationship.Equivalent, ExplicitNoMap = false, IsFallback = false,
            ConceptDomainRelationship = null, ValueDomainRelationship = null,
        });
        DbComparisonCache<DbElementTypeComparison> comparisons = new();
        ElementTypeComparer comparer = new(fixture.Connection, NullLoggerFactory.Instance, comparisons);
        comparer.DoTypeComparisons(pair, source, target, 1);
        comparisons.Values.ShouldContain(comparison => comparison.TechnicalMessage != null &&
            comparison.TechnicalMessage.Contains("ProfileDecisionFormat", StringComparison.Ordinal));
        PrivateField<XVerStructureCatalog?>(comparer, "_catalog").ShouldBeNull();
        comparer.DoTypeComparisons(pair, source, null, 2);
        PrivateField<XVerStructureCatalog?>(comparer, "_catalog").ShouldBeNull();
        DbStructureDefinitionFacts facts = DbStructureDefinitionFacts.SelectSingle(fixture.Connection, StructureKey: sourceType.Key)!;
        string original = facts.StructureDefinitionJson;
        facts.StructureDefinitionJson = "{}";
        DbStructureDefinitionFacts.Update(fixture.Connection, [facts]);
        Should.Throw<InvalidOperationException>(() => comparer.DoTypeComparisons(pair, source, target, 3));
        PrivateField<XVerStructureCatalog?>(comparer, "_catalog").ShouldBeNull();
        facts.StructureDefinitionJson = original;
        DbStructureDefinitionFacts.Update(fixture.Connection, [facts]);
        comparer.DoTypeComparisons(pair, source, target, 4);
        PrivateField<XVerStructureCatalog?>(comparer, "_catalog").ShouldBeNull();
    }

    [Fact]
    public void LegacyElementOutcomesReleasePayloadsOnSuccessExceptionAndReuse()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        ElementOutcomeGenerator generator = new(fixture.Connection, NullLoggerFactory.Instance, pair, new(), new());
        XVerStructureCatalog catalog = PrivateField<XVerStructureCatalog>(generator, "_catalog");
        AssertNoPayloads(catalog);
        DbStructureDefinition owner = fixture.Structure(pair.SourcePackage);
        catalog.ReadPayload(owner);
        generator.ProcessSourceStructure(owner, []);
        AssertNoPayloads(catalog);
        catalog.ReadPayload(owner);
        Should.Throw<NullReferenceException>(() => generator.ProcessSourceStructure(null!, []));
        AssertNoPayloads(catalog);
        catalog.ReadPayload(owner);
        generator.ProcessSourceStructure(owner, []);
        AssertNoPayloads(catalog);
        generator.DoPostProcessing(pair);
        AssertNoPayloads(catalog);
    }

    [Fact]
    public void LegacyBuildersAndRepresentationQueriesReleasePayloadsOnSuccessExceptionAndReuse()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        DbStructureOutcome owner = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        DbElement source = fixture.AddTypedElement(fixture.Structure(pair.SourcePackage), "ValueSet.status", "code");
        DbElement target = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.status", "code");
        fixture.Connect(pair, owner, source, target);
        fixture.CompleteAuthoredArrangement(pair);
        fixture.GenerateSafety(pair);
        StructureFhirExporter builder = new(new XVerExporter(fixture.Connection, new ConfigXVer { XverArtifactVersion = "0.1.0-test" }),
            fixture.Connection, NullLoggerFactory.Instance);
        IgExporter.XVerIgExportTrackingRecord Tracking() => new()
        {
            PackagePair = pair, PackageId = "hl7.fhir.uv.xver-r5.r4",
        };
        StructureFhirExporter.BuiltDefinitions built = builder.BuildDefinitions(Tracking());
        AssertNoPayloads(PrivateField<XVerStructureCatalog>(builder, "_catalog"));
        XVerStructureCatalog representationCatalog = PrivateField<XVerStructureCatalog>(built.Representations, "_catalog");
        AssertNoPayloads(representationCatalog);
        UnsafeCodePathStep[] path = [new("ValueSet", null, false), new(source.Id, null, false)];
        built.Representations.Resolve(owner.Key, path).ShouldNotBeEmpty();
        AssertNoPayloads(representationCatalog);
        built.Representations.ResolveSafety(owner.Key, [.. path]);
        AssertNoPayloads(representationCatalog);
        built.Representations.Occurrences(owner.Key).Take(1).ToArray().ShouldNotBeEmpty();
        AssertNoPayloads(representationCatalog);
        StructureFhirExporter.BuiltDefinitions next = builder.BuildDefinitions(Tracking());
        next.Representations.ShouldNotBeSameAs(built.Representations);
        AssertNoPayloads(PrivateField<XVerStructureCatalog>(builder, "_catalog"));
        built.Representations.Resolve(owner.Key, path).ShouldNotBeEmpty();
        AssertNoPayloads(representationCatalog);

        DbStructureDefinitionFacts facts = DbStructureDefinitionFacts.SelectSingle(fixture.Connection, StructureKey: owner.SourceStructureKey)!;
        string original = facts.StructureDefinitionJson;
        facts.StructureDefinitionJson = "{}";
        DbStructureDefinitionFacts.Update(fixture.Connection, [facts]);
        Should.Throw<InvalidOperationException>(() => builder.BuildDefinitions(Tracking()));
        AssertNoPayloads(PrivateField<XVerStructureCatalog>(builder, "_catalog"));
        facts.StructureDefinitionJson = original;
        DbStructureDefinitionFacts.Update(fixture.Connection, [facts]);
        builder.BuildExtensions(Tracking());
        AssertNoPayloads(PrivateField<XVerStructureCatalog>(builder, "_catalog"));
        builder.BuildProfiles(Tracking());
        AssertNoPayloads(PrivateField<XVerStructureCatalog>(builder, "_catalog"));

        DbStructureDefinitionFacts captured = PrivateField<Dictionary<int, DbStructureDefinitionFacts>>(representationCatalog, "_facts")[owner.SourceStructureKey];
        string capturedJson = captured.StructureDefinitionJson;
        captured.StructureDefinitionJson = "{}";
        Should.Throw<InvalidOperationException>(() => built.Representations.Occurrences(owner.Key).ToArray());
        AssertNoPayloads(representationCatalog);
        captured.StructureDefinitionJson = capturedJson;
        built.Representations.Resolve(owner.Key, path).ShouldNotBeEmpty();
        AssertNoPayloads(representationCatalog);
    }

    [Fact]
    public void LegacyUnsafePairFactsReleaseTheirOwnPayloadsIncludingProvenanceOnlyWork()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        Type type = typeof(UnsafeCodeOutcomeGenerator).GetNestedType("PairFacts", System.Reflection.BindingFlags.NonPublic)!;
        object instance = Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [fixture.Connection, pair], System.Globalization.CultureInfo.InvariantCulture)!;
        XVerStructureCatalog catalog = PrivateField<XVerStructureCatalog>(instance, "_catalog");
        try
        {
            catalog.ReadPayload(fixture.Structure(pair.SourcePackage));
            type.GetMethod("ValidateAnnotationProvenance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(instance, [Array.Empty<DbUnsafeCodeTrigger>()]);
        }
        finally
        {
            ((IDisposable)instance).Dispose();
        }
        AssertNoPayloads(catalog);
        Should.Throw<ObjectDisposedException>(() => catalog.GetDefinitions(pair.SourcePackageKey));
        object failed = Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [fixture.Connection, pair], System.Globalization.CultureInfo.InvariantCulture)!;
        XVerStructureCatalog failedCatalog = PrivateField<XVerStructureCatalog>(failed, "_catalog");
        DbUnsafeCodeTrigger[] invalidTriggers = [null!];
        try
        {
            failedCatalog.ReadPayload(fixture.Structure(pair.SourcePackage));
            Should.Throw<System.Reflection.TargetInvocationException>(() =>
                type.GetMethod("ValidateAnnotationProvenance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(failed, [invalidTriggers]));
        }
        finally
        {
            ((IDisposable)failed).Dispose();
        }
        AssertNoPayloads(failedCatalog);
        new UnsafeCodeOutcomeGenerator(fixture.Connection).ValidateAnnotationProvenance(pair, []);
        new UnsafeCodeOutcomeGenerator(fixture.Connection).GenerateForPair(pair);
    }

    private static T PrivateField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void AssertNoPayloads(XVerStructureCatalog catalog)
    {
        PrivateField<Dictionary<int, StructureDefinition>>(catalog, "_payloads").ShouldBeEmpty();
        PrivateField<Dictionary<int, Dictionary<string, ElementDefinition>>>(catalog, "_payloadElements").ShouldBeEmpty();
    }

    [Fact]
    public void OperandEvidenceCoversAllPersistedFields()
    {
        using UnsafeCodeTestFixture fixture = new();
        DbComparisonClasses.CreateTables(fixture.Connection);
        DbOutcomeClasses.CreateTables(fixture.Connection);
        ExecuteOperandSql(fixture.Connection, """
            UPDATE Structures SET VersionAlgorithmCoding = '{ "system": "urn:fixture", "code": "original" }';
            UPDATE ValueSets SET Compose = '{ "include": [ { "system": "urn:original" } ] }';
            """);
        using ObservedOperandConnection connection = new(fixture.Connection);
        using XVerOperandEvidence evidence = XVerOperandEvidence.Capture(connection, new(XVerPreparationKind.Comparison));

        foreach (XVerOperandEvidence.XVerTableManifest table in XVerOperandEvidence.Manifest)
        {
            string[] persisted = [.. table.RowType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(property => property.SetMethod is not null &&
                    !property.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CgSQLiteIgnore"))
                .Select(property => property.Name).Order(StringComparer.Ordinal)];
            table.PersistedFields.Order(StringComparer.Ordinal).ShouldBe(persisted, ignoreOrder: false, customMessage: table.RowType.Name);
            table.PersistedFields.ShouldContain(nameof(DbRecordBase.Key));
            connection.Statements.Any(sql =>
            {
                Match match = Regex.Match(sql, @"^SELECT (.+) FROM (\w+) ORDER BY Key ASC$");
                return match.Success && match.Groups[2].Value == table.TableName &&
                    match.Groups[1].Value.Split(", ").Order(StringComparer.Ordinal).SequenceEqual(persisted);
            }).ShouldBeTrue(table.TableName);
        }

        foreach (string sql in connection.Statements)
        {
            (sql.StartsWith("SELECT ", StringComparison.Ordinal) || sql.StartsWith("PRAGMA ", StringComparison.Ordinal)).ShouldBeTrue(sql);
            Match match = Regex.Match(sql, @"^SELECT (.+) FROM (\w+) ORDER BY Key(?: ASC)?$");
            if (match.Success)
            {
                XVerOperandEvidence.XVerTableManifest table = XVerOperandEvidence.Manifest.Single(table => table.TableName == match.Groups[2].Value);
                match.Groups[1].Value.Split(", ").ShouldAllBe(field => table.PersistedFields.Contains(field));
            }
        }

        DbStructureDefinition copy = evidence.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName)[0];
        copy.VersionAlgorithmCoding!.Code = "mutated";
        copy.Key = -100;
        evidence.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName)[0].VersionAlgorithmCoding!.Code.ShouldBe("original");
        evidence.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName)[0].Key.ShouldBeGreaterThan(0);
        DbValueSet valueSet = evidence.Rows<DbValueSet>(DbValueSet.DefaultTableName)[0];
        valueSet.Compose!.Include[0].System = "urn:mutated";
        evidence.Rows<DbValueSet>(DbValueSet.DefaultTableName)[0].Compose!.Include[0].System.ShouldBe("urn:original");
        List<DbFhirPackage> packages = evidence.Rows<DbFhirPackage>(DbFhirPackage.DefaultTableName);
        packages.Select(package => package.Key).ShouldBe(packages.Select(package => package.Key).Order());
        evidence.Audit();
        output.WriteLine($"Operand manifest: {XVerOperandEvidence.Manifest.Length} tables, {XVerOperandEvidence.Manifest.Sum(table => table.PersistedFields.Length)} persisted fields; reflected properties and observed SELECTs are a coverage guardrail, not a proof of reader closure.");
    }

    [Theory]
    [InlineData("same-count", false)]
    [InlineData("same-count", true)]
    [InlineData("payload-json", false)]
    [InlineData("payload-json", true)]
    [InlineData("metadata-json", false)]
    [InlineData("metadata-json", true)]
    [InlineData("null-empty", false)]
    [InlineData("null-empty", true)]
    [InlineData("version", false)]
    [InlineData("version", true)]
    [InlineData("enum-text", false)]
    [InlineData("enum-text", true)]
    [InlineData("boolean-integer", false)]
    [InlineData("boolean-integer", true)]
    public void OperandAuditRejectsSameCountAndJsonOnlyChanges(string mutation, bool secondConnection)
    {
        using UnsafeCodeTestFixture fixture = new(persistDatabase: true);
        DbOutcomeClasses.CreateTables(fixture.Connection);
        ExecuteOperandSql(fixture.Connection, """
            UPDATE Structures SET VersionAlgorithmCoding = '{"code":"same"}';
            UPDATE ValueSets SET CanExpand = 1;
            INSERT INTO UnsafeCodeTriggers
                (Key, StructureOutcomeKey, SourceFhirPackageKey, TargetFhirPackageKey, SourceElementId, TargetElementId,
                 SourcePathJson, System, Code, IsWildcardVersion, VersionsJson, EvidenceKeysJson, RuleKeysJson, AnnotationKeysJson)
                VALUES (1, 1, 1, 2, 'sentinel', 'sentinel', '[]', 'urn:sentinel', 'sentinel', 0, '[]', '[]', '[]', '[]');
            """);
        using XVerOperandEvidence evidence = XVerOperandEvidence.Capture(fixture.Connection, new(XVerPreparationKind.Comparison));
        using SqliteConnection writer = new(fixture.Connection.ConnectionString);
        writer.Open();
        IDbConnection target = secondConnection ? writer : fixture.Connection;
        int[] capturedFactKeys = [.. evidence.Rows<DbStructureDefinitionFacts>(DbStructureDefinitionFacts.DefaultTableName)
            .Select(facts => facts.Key).Order()];
        capturedFactKeys.ShouldNotBeEmpty();
        int replacementFactKey = capturedFactKeys[^1] + 1;
        string sql = mutation switch
        {
            "same-count" => $"UPDATE StructureDefinitionFacts SET Key = {replacementFactKey} WHERE Key = {capturedFactKeys[0]}",
            "payload-json" => "UPDATE StructureDefinitionFacts SET StructureDefinitionJson = StructureDefinitionJson || ' '",
            "metadata-json" => """UPDATE Structures SET VersionAlgorithmCoding = '{ "code" : "same" }'""",
            "null-empty" => "UPDATE FhirPackages SET Dependencies = ''",
            "version" => "UPDATE FhirPackages SET PackageVersion = PackageVersion || '-changed'",
            "enum-text" => $"UPDATE FhirPackages SET DefinitionFhirSequence = '{(int)fixture.Packages[0].DefinitionFhirSequence}' WHERE Key = {fixture.Packages[0].Key}",
            "boolean-integer" => "UPDATE ValueSets SET CanExpand = 2",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        ExecuteOperandSql(target, sql);
        if (mutation == "same-count")
        {
            int[] liveFactKeys = [.. DbStructureDefinitionFacts.SelectList(fixture.Connection).Select(facts => facts.Key).Order()];
            liveFactKeys.Length.ShouldBe(capturedFactKeys.Length);
            liveFactKeys.ShouldNotContain(capturedFactKeys[0]);
            liveFactKeys.ShouldContain(replacementFactKey);
        }
        long changes = ReadOperandInteger(fixture.Connection, "SELECT total_changes()");
        long schema = ReadOperandInteger(fixture.Connection, "PRAGMA schema_version");
        string triggers = ReadOperandText(fixture.Connection, "SELECT SourcePathJson || VersionsJson || EvidenceKeysJson || RuleKeysJson || AnnotationKeysJson FROM UnsafeCodeTriggers");
        Should.Throw<InvalidOperationException>(evidence.Audit).Message.ShouldContain("changed");
        ReadOperandInteger(fixture.Connection, "SELECT total_changes()").ShouldBe(changes);
        ReadOperandInteger(fixture.Connection, "PRAGMA schema_version").ShouldBe(schema);
        ReadOperandText(fixture.Connection, "SELECT SourcePathJson || VersionsJson || EvidenceKeysJson || RuleKeysJson || AnnotationKeysJson FROM UnsafeCodeTriggers").ShouldBe(triggers);
        Should.Throw<ObjectDisposedException>(evidence.Audit);
    }

    [Theory]
    [InlineData("local", false)]
    [InlineData("external", false)]
    [InlineData("schema", false)]
    [InlineData("local", true)]
    [InlineData("external", true)]
    [InlineData("schema", true)]
    public void CaptureRejectsConcurrentOrSchemaDrift(string mutation, bool audit)
    {
        using UnsafeCodeTestFixture fixture = new(persistDatabase: true);
        using SqliteConnection writer = new(fixture.Connection.ConnectionString);
        writer.Open();
        using ObservedOperandConnection connection = new(fixture.Connection);
        using XVerOperandEvidence? evidence = audit
            ? XVerOperandEvidence.Capture(connection, new(XVerPreparationKind.Comparison)) : null;
        bool injected = false;
        connection.BeforeExecute = sql =>
        {
            if (!sql.Contains(" FROM Structures", StringComparison.Ordinal))
            {
                return;
            }
            connection.BeforeExecute = null;
            injected = true;
            if (mutation == "schema")
            {
                ExecuteOperandSql(fixture.Connection, "CREATE TABLE Phase2SchemaDrift (Key INTEGER PRIMARY KEY)");
            }
            else
            {
                IDbConnection target = mutation == "external" ? writer : fixture.Connection;
                ExecuteOperandSql(target, "UPDATE FhirPackages SET Name = Name || '-changed'; UPDATE FhirPackages SET Name = replace(Name, '-changed', '')");
            }
        };
        Action read = audit ? evidence!.Audit : () => XVerOperandEvidence.Capture(connection, new(XVerPreparationKind.Comparison)).Dispose();
        Should.Throw<InvalidOperationException>(read).Message.ShouldContain("during a read pass");
        injected.ShouldBeTrue();
        connection.Subscriptions.ShouldBe(0);
        DefinitionInventoryStore.TableExists(fixture.Connection, DbStructureOutcome.DefaultTableName).ShouldBeFalse();
        DefinitionInventoryStore.TableExists(fixture.Connection, DbUnsafeCodeTrigger.DefaultTableName).ShouldBeFalse();
    }

    [Fact]
    public void PhysicalReopenInvalidatesEvenIdenticalEvidence()
    {
        using UnsafeCodeTestFixture fixture = new(persistDatabase: true);
        fixture.Connection.Close();
        fixture.Connection.Open();
        using ObservedOperandConnection connection = new(fixture.Connection);
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(connection, new(XVerPreparationKind.Catalog));
        XVerStructureCatalog catalog = operation.Catalog;
        long dataVersion = ReadOperandInteger(fixture.Connection, "PRAGMA data_version");
        long changes = ReadOperandInteger(fixture.Connection, "SELECT total_changes()");
        long schema = ReadOperandInteger(fixture.Connection, "PRAGMA schema_version");
        connection.Subscriptions.ShouldBe(1);
        fixture.Connection.Close();
        fixture.Connection.Open();
        ReadOperandInteger(fixture.Connection, "PRAGMA data_version").ShouldBe(dataVersion);
        ReadOperandInteger(fixture.Connection, "SELECT total_changes()").ShouldBe(changes);
        ReadOperandInteger(fixture.Connection, "PRAGMA schema_version").ShouldBe(schema);
        connection.Subscriptions.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => catalog.GetDefinitions(fixture.Packages[0].Key)).Message.ShouldContain("physical connection");
        Should.Throw<InvalidOperationException>(operation.Audit).Message.ShouldContain("physical connection");
        Should.Throw<ObjectDisposedException>(() => { _ = operation.Catalog; });
        using XVerPreparedOperation fresh = XVerPreparedOperation.Prepare(connection, new(XVerPreparationKind.Catalog));
        fresh.Catalog.GetDefinitions(fixture.Packages[0].Key).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData((int)XVerPreparationKind.Catalog)]
    [InlineData((int)XVerPreparationKind.TypeComparison)]
    [InlineData((int)XVerPreparationKind.ElementOutcomes)]
    [InlineData((int)XVerPreparationKind.Construction)]
    public void NarrowPreparationDoesNotAcquirePipelinePrerequisites(int preparationKind)
    {
        using UnsafeCodeTestFixture fixture = new();
        DbDefinitionInventoryState.DropTable(fixture.Connection);
        DbUnsafeCodeInputState.DropTable(fixture.Connection);
        DbUnsafeCodePairState.DropTable(fixture.Connection);
        DbUnsafeCodeMapEvidence.DropTable(fixture.Connection);
        using ObservedOperandConnection connection = new(fixture.Connection);
        long changes = ReadOperandInteger(connection, "SELECT total_changes()");
        long schema = ReadOperandInteger(connection, "PRAGMA schema_version");
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(connection, new((XVerPreparationKind)preparationKind));
        operation.InventoryRevision.ShouldBeNull();
        operation.Catalog.GetDefinitions(fixture.Packages[0].Key).ShouldNotBeEmpty();
        operation.Evidence.ContainsTable(DbUnsafeCodeMapEvidence.DefaultTableName).ShouldBeFalse();
        ReadOperandInteger(connection, "SELECT total_changes()").ShouldBe(changes);
        ReadOperandInteger(connection, "PRAGMA schema_version").ShouldBe(schema);
        DefinitionInventoryStore.TableExists(connection, DbDefinitionInventoryState.DefaultTableName).ShouldBeFalse();
        DefinitionInventoryStore.TableExists(connection, DbUnsafeCodePairState.DefaultTableName).ShouldBeFalse();
        DefinitionInventoryStore.TableExists(connection, DbStructureOutcome.DefaultTableName).ShouldBeFalse();
        operation.Dispose();
        connection.Subscriptions.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(() => { _ = operation.Catalog; });
    }

    [Fact]
    public void PreparationValidatesCapturedRowsWithoutASecondCatalogOrMapBindingValidation()
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.Load().ShouldBeTrue();
        using ObservedOperandConnection connection = new(fixture.Connection);
        HashSet<string> exclusions = ["urn:excluded"];
        ConfigXVer config = new() { OutputDirectory = "original", XverArtifactVersion = "original" };
        XVerOperationOptions options = new(XVerPreparationKind.Comparison, exclusions, configuration: config);
        exclusions.Add(fixture.Structure(fixture.Packages[0]).UnversionedUrl);
        config.OutputDirectory = "changed";
        config.XverArtifactVersion = "changed";
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(connection, options);
        operation.InventoryRevision.ShouldBe(DefinitionInventoryStore.ReadState(fixture.Connection)!.CompletedRevision);
        connection.Statements.Count(sql => sql.Contains("StructureDefinitionJson", StringComparison.Ordinal) &&
            sql.Contains(" FROM StructureDefinitionFacts", StringComparison.Ordinal)).ShouldBe(2);
        operation.Evidence.Options.Exclusions.ShouldBe(["urn:excluded"], ignoreOrder: true);
        operation.Evidence.Options.Construction!.OutputDirectory.ShouldBe("original");
        operation.Evidence.Options.Construction.ArtifactVersion.ShouldBe("original");

        XVerStructureCatalog independent = new(fixture.Connection);
        DbStructureDefinition owner = independent.GetDefinitions(fixture.Packages[0].Key)[0];
        StructureDefinition payload = independent.ReadPayload(owner);
        XVerStructureCatalog captured = operation.Catalog;
        captured.ReadPayload(captured.GetStructure(owner.Key)).ShouldNotBeSameAs(payload);
        operation.Dispose();
        independent.ReadPayload(owner).ShouldBeSameAs(payload);
        Should.Throw<ObjectDisposedException>(() => captured.ReadPayload(owner));
        independent.Dispose();
    }

    [Theory]
    [InlineData("missing-table")]
    [InlineData("missing-column")]
    public void RequiredOperandSchemaFailuresAreContextualAndReadOnly(string mutation)
    {
        using UnsafeCodeTestFixture fixture = new();
        if (mutation == "missing-table")
        {
            DbStructureDefinitionFacts.DropTable(fixture.Connection);
        }
        else
        {
            ExecuteOperandSql(fixture.Connection, "ALTER TABLE StructureDefinitionFacts RENAME COLUMN StructureDefinitionJson TO FormerPayload");
        }
        using ObservedOperandConnection connection = new(fixture.Connection);
        long changes = ReadOperandInteger(connection, "SELECT total_changes()");
        long schema = ReadOperandInteger(connection, "PRAGMA schema_version");
        Should.Throw<InvalidOperationException>(() => XVerPreparedOperation.Prepare(connection, new(XVerPreparationKind.Comparison)))
            .Message.ShouldContain(DbStructureDefinitionFacts.DefaultTableName);
        ReadOperandInteger(connection, "SELECT total_changes()").ShouldBe(changes);
        ReadOperandInteger(connection, "PRAGMA schema_version").ShouldBe(schema);
        connection.Subscriptions.ShouldBe(0);
        DefinitionInventoryStore.TableExists(connection, DbStructureOutcome.DefaultTableName).ShouldBeFalse();
    }

    private static void ExecuteOperandSql(IDbConnection connection, string sql)
    {
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadOperandInteger(IDbConnection connection, string sql)
    {
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadOperandText(IDbConnection connection, string sql)
    {
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private sealed class ObservedOperandConnection(SqliteConnection connection) : DbConnection
    {
        internal List<string> Statements { get; } = [];
        internal Action<string>? BeforeExecute { get; set; }
        internal Action<string>? AfterExecute { get; set; }
        internal int Subscriptions { get; private set; }

        public override event StateChangeEventHandler? StateChange
        {
            add
            {
                connection.StateChange += value;
                Subscriptions++;
            }
            remove
            {
                connection.StateChange -= value;
                Subscriptions--;
            }
        }

        [AllowNull]
        public override string ConnectionString { get => connection.ConnectionString; set => connection.ConnectionString = value; }
        public override string Database => connection.Database;
        public override string DataSource => connection.DataSource;
        public override string ServerVersion => connection.ServerVersion;
        public override ConnectionState State => connection.State;
        public override int ConnectionTimeout => connection.ConnectionTimeout;
        public override void Open() => connection.Open();
        public override void Close() => connection.Close();
        public override void ChangeDatabase(string databaseName) => connection.ChangeDatabase(databaseName);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => connection.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new ObservedOperandCommand(connection.CreateCommand(), Observe,
            sql => AfterExecute?.Invoke(sql));

        protected override void Dispose(bool disposing)
        {
            // The fixture, not this observation wrapper, owns the physical connection.
        }

        private void Observe(string sql)
        {
            Statements.Add(sql);
            BeforeExecute?.Invoke(sql);
        }
    }

    private sealed class ObservedOperandCommand(DbCommand command, Action<string> observe, Action<string> after) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get => command.CommandText; set => command.CommandText = value; }
        public override int CommandTimeout { get => command.CommandTimeout; set => command.CommandTimeout = value; }
        public override CommandType CommandType { get => command.CommandType; set => command.CommandType = value; }
        public override bool DesignTimeVisible { get => command.DesignTimeVisible; set => command.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => command.UpdatedRowSource; set => command.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => command.Connection; set => command.Connection = value; }
        protected override DbTransaction? DbTransaction { get => command.Transaction; set => command.Transaction = value; }
        protected override DbParameterCollection DbParameterCollection => command.Parameters;
        public override void Cancel() => command.Cancel();
        public override void Prepare() => command.Prepare();
        protected override DbParameter CreateDbParameter() => command.CreateParameter();

        public override int ExecuteNonQuery()
        {
            observe(CommandText);
            int result = command.ExecuteNonQuery();
            after(CommandText);
            return result;
        }

        public override object? ExecuteScalar()
        {
            observe(CommandText);
            return command.ExecuteScalar();
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            observe(CommandText);
            return command.ExecuteReader(behavior);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                command.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void CharacterizedPipelineProducesStableSemantics()
    {
        RunSyntheticPipeline(captureSemantics: true);
    }

    [Fact]
    public async System.Threading.Tasks.Task IdenticalWorkloadsReportStageCosts()
    {
        RunSyntheticPipeline();
        await RunRealCorePipeline();
    }

    [Fact]
    public async System.Threading.Tasks.Task CharacterizedRealCoreConsumersProduceStableSemantics()
    {
        await RunRealCorePipeline(captureSemantics: true);
    }

    private async System.Threading.Tasks.Task RunRealCorePipeline(bool captureSemantics = false)
    {
        using WorkloadDirectory directory = new() { RetainCharacterization = captureSemantics };
        List<StageCost> costs = [];
        RealCoreWorkload? workload = null;
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch operation = new();
        string stage = "cached definitions";
        try
        {
            List<DefinitionCollection> definitions = [];
            foreach (FhirReleases.FhirSequenceCodes sequence in _sequences)
            {
                definitions.Add(await LoadCore(sequence));
            }
            foreach (DefinitionCollection collection in definitions.Where(collection =>
                collection.FhirSequence is FhirReleases.FhirSequenceCodes.R5 or FhirReleases.FhirSequenceCodes.R4B))
            {
                AddCoreCarrier(collection);
            }
            stage = "full retained core import";
            Measure(stage, () => workload = new(directory, definitions));
            workload!.AssertRetainedInventory(definitions);
            workload.Reopen();
            FhirPackageComparisonPair pair = workload.RangePair;
            stage = "core local maps";
            Measure(stage, () => workload.LoadRangeMaps(pair));
            workload.Reopen();
            operation.Start();
            stage = "actual Range element/type comparison";
            Measure(stage, () => workload.CompareRangeElements(pair));
            workload.Reopen();
            workload.AssertRangeComparisons(pair);
            stage = "actual Range element outcomes";
            Measure(stage, () => workload.GenerateRangeOutcomes(pair));
            workload.Reopen();
            stage = "export readiness and write-free build";
            StructureFhirExporter.BuiltDefinitions? built = null;
            Measure(stage, () => built = workload.BuildRangeDefinitions(pair));
            stage = "frozen semantic verification";
            Measure(stage, () =>
            {
                built!.Profiles.ShouldHaveSingleItem().Definition.BaseDefinition.ShouldBe(CoreConsumerUrl);
                built.Profiles.ShouldHaveSingleItem().Projections.ShouldBeEmpty();
                built.Extensions.ShouldBeEmpty();
                AssertGeneratedResource(built.Profiles.Single().Definition, pair, CoreConsumerName,
                    "consumer-witness", "QuantityConsumerWitnessProfile", CoreConsumerUrl);
                AssertCoreRows(workload.Connection, pair);
                workload.ConnectionReopens.ShouldBe(4);
            });
            stage = "actual-core expected-rejection witnesses";
            PersistedProfileDecision[] witnesses = [];
            Measure(stage, () => witnesses = AssertCoreNegativeWitnesses(workload.Connection));
            operation.Stop();
            RecordMeasurementIdentity(workload.Connection, directory, "W2");
            if (captureSemantics)
            {
                File.WriteAllText(Path.Combine(directory.Root, "built-definitions.json"), JsonSerializer.Serialize(new
                {
                    Profiles = built!.Profiles.Select(item => new
                    {
                        item.FileName, item.OutcomeKey, item.SourceId, Resource = item.Definition.ToJson(),
                        Projections = item.Projections.Select(PersistedProfileDecision.FromProof),
                    }),
                    Extensions = built.Extensions.Select(item => new
                    {
                        item.FileName, item.OutcomeKey, Resource = item.Definition.ToJson(),
                        Projections = item.Projections.Select(PersistedProfileDecision.FromProof),
                    }),
                    ExpectedRejections = witnesses,
                }, new JsonSerializerOptions { WriteIndented = true }));
                stage = "semantic characterization";
                CaptureWorkloadSemantics(workload.Connection, directory, "W2");
            }
            directory.Completed = true;
        }
        finally
        {
            if (workload is not null)
            {
                Measure("database disposal", workload.Dispose);
            }
            total.Stop();
            output.WriteLine($"W2 last stage={stage}; total including setup/capture/disposal={total.Elapsed.TotalMilliseconds:F3} ms; root={directory.Root}");
            foreach (StageCost cost in costs)
            {
                output.WriteLine(JsonSerializer.Serialize(cost));
            }
            File.WriteAllText(Path.Combine(directory.Root, "stage-costs.json"), JsonSerializer.Serialize(new
            {
                Workload = "W2", LastStage = stage, directory.Completed,
                TotalIncludingSetupCaptureAndDisposalMilliseconds = total.Elapsed.TotalMilliseconds, Costs = costs,
                NetConsumerAndDisposalMilliseconds = operation.Elapsed.TotalMilliseconds +
                    costs.Where(cost => cost.Stage == "database disposal").Sum(cost => cost.Milliseconds),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Measure(string name, Action action)
        {
            using StageMemorySampler memory = new();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long entryPrivate = process.PrivateMemorySize64;
            long entryManaged = GC.GetTotalMemory(false);
            long allocated = GC.GetTotalAllocatedBytes(true);
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                elapsed.Stop();
                memory.Finish();
                process.Refresh();
                costs.Add(new(name, elapsed.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
                    entryPrivate, process.PrivateMemorySize64, entryManaged, GC.GetTotalMemory(false),
                    process.PeakWorkingSet64, new FileInfo(Path.Combine(directory.Root, "workload.sqlite")) is { Exists: true } file
                        ? file.Length : 0, memory.Samples, memory.PeakPrivateBytes, memory.PeakWorkingSetBytes));
            }
        }
    }

    private static async System.Threading.Tasks.Task<DefinitionCollection> LoadCore(FhirReleases.FhirSequenceCodes sequence)
    {
        string moniker = sequence.ToCorePackageId() + "#" + sequence.ToLongVersion();
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fhir", "packages");
        List<string> prerequisites = [moniker];
        if (sequence != FhirReleases.FhirSequenceCodes.DSTU2)
        {
            prerequisites.Add(moniker.Replace(".core#", ".expansions#", StringComparison.Ordinal));
        }
        foreach (string prerequisite in prerequisites)
        {
            File.Exists(Path.Combine(cache, prerequisite, "package", "package.json")).ShouldBeTrue(
                $"Required cached-core prerequisite absent: {prerequisite} under {cache}; no package acquisition attempted.");
        }
        using PackageLoader loader = new(new()
        {
            OfflineMode = true, ResolvePackageDependencies = false, AutoLoadExpansions = true,
            LogFactory = NullLoggerFactory.Instance,
        });
        DefinitionCollection? definitions = await loader.LoadPackages([moniker]);
        definitions.ShouldNotBeNull();
        return definitions;
    }

    private const string CoreConsumerName = "QuantityConsumerWitness";
    private const string CoreConsumerUrl = "http://example.org/StructureDefinition/" + CoreConsumerName;

    private static void AddCoreCarrier(DefinitionCollection definitions)
    {
        StructureDefinition basis = definitions.ResourcesByName["DomainResource"];
        StructureDefinition carrier = (StructureDefinition)basis.DeepCopy();
        carrier.Id = carrier.Name = CoreConsumerName;
        carrier.Url = CoreConsumerUrl;
        carrier.Version = definitions.MainPackageVersion;
        carrier.Type = CoreConsumerName;
        carrier.Abstract = false;
        carrier.BaseDefinition = basis.Url + "|" + basis.Version;
        carrier.Derivation = StructureDefinition.TypeDerivationRule.Specialization;
        foreach (ElementDefinition element in carrier.Snapshot.Element)
        {
            element.ElementId = CoreConsumerName + element.ElementId[basis.Snapshot.Element[0].ElementId.Length..];
            element.Path = CoreConsumerName + element.Path["DomainResource".Length..];
        }
        ElementDefinition use = new()
        {
            ElementId = CoreConsumerName + ".measure", Path = CoreConsumerName + ".measure",
            Min = 0, Max = "1", Short = "Local carrier of the unchanged actual core datatype",
            Base = new() { Path = CoreConsumerName + ".measure", Min = 0, Max = "1" },
            Type = [new() { Code = "Range" }],
        };
        carrier.Snapshot.Element.Add(use);
        carrier.Differential = new()
        {
            Element = [new() { ElementId = CoreConsumerName, Path = CoreConsumerName }, (ElementDefinition)use.DeepCopy()],
        };
        definitions.AddResourceDefinition(carrier, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
    }

    private static PersistedProfileDecision[] AssertCoreNegativeWitnesses(IDbConnection connection)
    {
        XVerStructureCatalog catalog = new(connection, XVerProcessor._exclusionSet);
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(connection);
        int r2 = packages.Single(package => package.PackageVersion == "1.0.2").Key;
        int r4 = packages.Single(package => package.PackageVersion == "4.0.1").Key;
        int r5 = packages.Single(package => package.PackageVersion == "5.0.0").Key;
        using ProfileUseFacts ageFacts = new(catalog, r2, r4);
        DbStructureDefinition sourceAge = catalog.ResolveDefinition(r2, StructureUrl + "Age|1.0.2")!;
        DbStructureDefinition targetAge = catalog.ResolveDefinition(r4, StructureUrl + "Age|4.0.1")!;
        EffectiveProfileBranch source = ageFacts.ForDefinition(sourceAge);
        EffectiveProfileBranch target = ageFacts.ForDefinition(targetAge);
        ProfileInvariant sourceInvariant = source.Elements[""].Invariants.Single(invariant => invariant.Key == "age-1");
        ProfileInvariant targetInvariant = target.Elements[""].Invariants.First(invariant => invariant.Key == "age-1");
        sourceInvariant.XPath.ShouldNotBeNull();
        targetInvariant.Expression.ShouldNotBeNull();
        sourceInvariant.XPath.ShouldContain("not(contains(f:value/@value, '-'))");
        targetInvariant.Expression.ShouldContain("value > 0");
        XmlNamespaceManager namespaces = new(new NameTable());
        namespaces.AddNamespace("f", "http://hl7.org/fhir");
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        CompiledExpression expression = compiler.Compile(targetInvariant.Expression);
        foreach (int value in new[] { -1, 0, 1 })
        {
            XDocument instance = XDocument.Parse(
                $"""<Quantity xmlns="http://hl7.org/fhir"><value value="{value}"/><system value="http://unitsofmeasure.org"/><code value="a"/></Quantity>""");
            ((bool)instance.Root!.XPathEvaluate(sourceInvariant.XPath.Replace("exists(", "boolean(", StringComparison.Ordinal),
                namespaces)).ShouldBe(value >= 0);
            Quantity quantity = new() { Value = value, System = "http://unitsofmeasure.org", Code = "a" };
            expression(quantity.ToTypedElement(), new EvaluationContext()).ShouldHaveSingleItem().Value.ShouldBe(value > 0);
        }
        ProfileRepresentationProof age = ageFacts.Prove(source, targetAge);
        age.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        age.TargetCanonical.ShouldBeNull();
        age.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.InvariantTransformation);
        ageFacts.Prove(source, catalog.ResolveNativeType(r4, "Quantity")!).PreservesRestrictions.ShouldBeFalse();
        const string comparatorSystem = "http://hl7.org/fhir/quantity-comparator";
        const string comparatorValueSet = "http://hl7.org/fhir/ValueSet/quantity-comparator";
        DbValueSet sourceDomain = DbValueSet.SelectList(connection, FhirPackageKey: r5).Single(row =>
            row.UnversionedUrl == comparatorValueSet && row.Version == "5.0.0");
        DbValueSet targetDomain = DbValueSet.SelectList(connection, FhirPackageKey: r4).Single(row =>
            row.UnversionedUrl == comparatorValueSet && row.Version == "4.0.1");
        UnsafeCodeIdentity[] sourceMembers = [.. DbValueSetConcept.SelectList(connection, ValueSetKey: sourceDomain.Key)
            .Select(member => new UnsafeCodeIdentity(member.System, member.SystemVersion, member.Code))];
        UnsafeCodeIdentity[] targetMembers = [.. DbValueSetConcept.SelectList(connection, ValueSetKey: targetDomain.Key)
            .Select(member => new UnsafeCodeIdentity(member.System, member.SystemVersion, member.Code))];
        sourceMembers.Length.ShouldBe(5);
        targetMembers.Length.ShouldBe(4);
        foreach (string comparator in new[] { "<", "<=", ">=", ">", "ad" })
        {
            ISourceNode witness = FhirJsonNode.Parse(
                $$$"""{"resourceType":"Parameters","parameter":[{"name":"witness","valueQuantity":{"value":1,"comparator":"{{{comparator}}}"}}]}""")
                .Children("parameter").Single().Children("valueQuantity").Single();
            string actual = witness.Children("comparator").Single().Text!;
            sourceMembers.ShouldContain(new UnsafeCodeIdentity(comparatorSystem, "5.0.0", actual));
            targetMembers.Contains(new UnsafeCodeIdentity(comparatorSystem, "4.0.1", actual)).ShouldBe(actual != "ad");
        }
        using ProfileUseFacts quantityFacts = new(catalog, r5, r4);
        DbStructureDefinition targetQuantity = catalog.ResolveNativeType(r4, "Quantity")!;
        ProfileRepresentationProof binding = quantityFacts.Prove(
            quantityFacts.ForDefinition(catalog.ResolveNativeType(r5, "Quantity")!), targetQuantity);
        binding.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        binding.TargetCanonical.ShouldBeNull();
        binding.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.Binding);
        foreach (DbStructureDefinition candidate in catalog.GetDefinitions(r4).Where(owner =>
            owner.ArtifactClass == FhirArtifactClassEnum.ComplexType ||
            owner.ArtifactClass == FhirArtifactClassEnum.Profile && catalog.GetDeclaredType(owner) == "Quantity")
            .Where(owner => owner.Key == targetQuantity.Key || catalog.GetAncestors(owner).Any(ancestor => ancestor.Key == targetQuantity.Key)))
        {
            EffectiveProfileElement comparator = quantityFacts.ForDefinition(candidate).Elements[".comparator"];
            (comparator.Prohibited || comparator.Restrictions.Any(restriction =>
                restriction.Kind == ProfileRestrictionKind.Binding && ProfileUseFacts.Text(restriction.Value, "strength") == "required" &&
                ProfileUseFacts.Text(restriction.Value, "valueSet") == comparatorValueSet + "|4.0.1")).ShouldBeTrue(candidate.VersionedUrl);
        }
        return [PersistedProfileDecision.FromProof(age), PersistedProfileDecision.FromProof(binding)];
    }

    private void RunSyntheticPipeline(bool captureSemantics = false)
    {
        using WorkloadDirectory directory = new();
        directory.RetainCharacterization = captureSemantics;
        List<StageCost> costs = [];
        ComparisonDatabase? database = null;
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch operation = new();
        string stage = "synthetic definitions/import";
        try
        {
            Measure(stage, () =>
            {
                DefinitionCollection[] definitions = [.. _sequences.Select(SyntheticDefinitions)];
                database = new(definitions, directory.Root, "workload.sqlite", NullLoggerFactory.Instance);
                database.TryLoadFromDefinitionCollections(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes)
                    .ShouldBeTrue();
                AssertEffectiveRootTypeUses(database.DbConnection, definitions);
            });
            IDbConnection connection = database!.DbConnection;
            List<DbFhirPackage> packages = DbFhirPackage.SelectList(connection,
                orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);
            packages.Select(package => package.PackageVersion).ShouldBe(["1.0.2", "3.0.2", "4.0.1", "4.3.0", "5.0.0"]);
            stage = "fixture maps";
            Measure(stage, () =>
            {
                WriteSyntheticMaps(directory.Root, packages);
                MappingLoader loader = new(connection, NullLoggerFactory.Instance);
                loader.TryLoadCrossVersionSourceMaps(directory.Root, useInternalTypeMaps: false).ShouldBeTrue();
            });
            DbStructureMapping.SelectList(connection).ShouldContain(map => map.IsFallback);
            DbStructureMapping.SelectList(connection).ShouldContain(map => !map.IsFallback && map.ConceptMapSourceKey != null);
            stage = "FhirDbComparer.Compare";
            operation.Start();
            Measure(stage, () => new FhirDbComparer(database, NullLoggerFactory.Instance).Compare(
                processValueSets: true, processStructures: true, maxStepSize: 4));
            DbUnsafeCodePairState.SelectList(connection).Count.ShouldBe(20);
            DbUnsafeCodePairState.SelectList(connection).ShouldAllBe(pair => !string.IsNullOrEmpty(pair.ComparisonRevision));
            stage = "OutcomeGenerator.GenerateOutcomes";
            Measure(stage, () => new OutcomeGenerator(database, NullLoggerFactory.Instance).GenerateOutcomes(
                processValueSets: true, processStructures: true, maxStepSize: 4));
            DbUnsafeCodePairState.SelectList(connection).ShouldAllBe(pair =>
                pair.OutcomeComparisonRevision == pair.ComparisonRevision && pair.OutcomeFormatVersion == 2);
            DbElementOutcome[] negatives = [.. DbElementOutcome.SelectList(connection)
                .Where(outcome => outcome.ExtensionProhibitionReason?.Contains("\"ProfileDecisionFormat\":", StringComparison.Ordinal) == true)];
            negatives.ShouldNotBeEmpty();
            foreach (DbElementOutcome negative in negatives)
            {
                negative.ExtensionDefinitionIsProhibited.ShouldBeTrue();
                negative.IsEquivalent.ShouldBeFalse();
                negative.GenUrl.ShouldBeNull();
                ProfileUseFacts.ReadDecisions(negative.ExtensionProhibitionReason!).ShouldAllBe(decision =>
                    decision.Kind == ProfileProofKind.Unrepresentable && decision.TargetCanonical == null &&
                    decision.RequiredFacts.ContainsKey("") && decision.NativeFacts.ContainsKey("") &&
                    decision.InputOwnerRoot != null && decision.InputOwnerRoot.Canonical == StructureUrl + "NegativeQuantity" &&
                    decision.NativeOwnerRoot != null &&
                    decision.Reasons.Any(reason => reason.Category == ProfileReasonCategory.InvariantTransformation));
            }
            stage = "XVerExporter.Export";
            Measure(stage, () =>
            {
                ConfigXVer config = new()
                {
                    OutputDirectory = Path.Combine(directory.Root, "output"),
                    LogFactory = NullLoggerFactory.Instance,
                    ExportR2 = false, ExportR3 = true, ExportR4 = true, ExportR4B = true,
                    ExportR5 = false, ExportR6 = false,
                };
                XVerExporter exporter = new(connection, config);
                IgExporter scheduleBuilder = new(connection, NullLoggerFactory.Instance, config.OutputDirectory,
                    null, exporter, config);
                scheduleBuilder.BuildExportSchedule(maxStepSize: 4).PackagePairs.Count.ShouldBe(12);
                exporter.Export(includeIgScripts: false, processVocabulary: true, processStructures: true, maxStepSize: 4);
                string[] resources = Directory.GetFiles(config.OutputDirectory, "StructureDefinition-*.json", SearchOption.AllDirectories);
                resources.ShouldNotBeEmpty();
                output.WriteLine($"W1 emitted StructureDefinitions={resources.Length}");
            });
            stage = "frozen semantic verification";
            Measure(stage, () => AssertSyntheticSemantics(connection, directory));
            output.WriteLine($"W1 rows: definitions={DbStructureDefinition.SelectList(connection).Count}, " +
                $"facts={DbStructureDefinitionFacts.SelectList(connection).Count}, elements={DbElement.SelectList(connection).Count}, " +
                $"types={DbElementType.SelectList(connection).Count}, negatives={negatives.Length}");
            operation.Stop();
            RecordMeasurementIdentity(connection, directory, "W1");
            if (captureSemantics)
            {
                CaptureWorkloadSemantics(connection, directory, "W1");
            }
            directory.Completed = true;
        }
        finally
        {
            if (database is not null)
            {
                Measure("database disposal", () =>
                {
                    SqliteConnection connection = (SqliteConnection)database.DbConnection;
                    ((IDisposable)database).Dispose();
                    SqliteConnection.ClearPool(connection);
                });
            }
            total.Stop();
            output.WriteLine($"W1 last stage={stage}; total including setup/disposal={total.Elapsed.TotalMilliseconds:F3} ms; root={directory.Root}");
            foreach (StageCost cost in costs)
            {
                output.WriteLine(JsonSerializer.Serialize(cost));
            }
            File.WriteAllText(Path.Combine(directory.Root, "stage-costs.json"), JsonSerializer.Serialize(new
            {
                Workload = "W1", LastStage = stage, directory.Completed,
                TotalIncludingSetupCaptureAndDisposalMilliseconds = total.Elapsed.TotalMilliseconds, Costs = costs,
                NetConsumerAndDisposalMilliseconds = operation.Elapsed.TotalMilliseconds +
                    costs.Where(cost => cost.Stage == "database disposal").Sum(cost => cost.Milliseconds),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Measure(string name, Action action)
        {
            using StageMemorySampler memory = new();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long entryPrivate = process.PrivateMemorySize64;
            long entryManaged = GC.GetTotalMemory(false);
            long allocated = GC.GetTotalAllocatedBytes(true);
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                elapsed.Stop();
                memory.Finish();
                process.Refresh();
                costs.Add(new(name, elapsed.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
                    entryPrivate, process.PrivateMemorySize64, entryManaged, GC.GetTotalMemory(false),
                    process.PeakWorkingSet64, new FileInfo(Path.Combine(directory.Root, "workload.sqlite")) is { Exists: true } file
                        ? file.Length : 0, memory.Samples, memory.PeakPrivateBytes, memory.PeakWorkingSetBytes));
            }
        }
    }

    private void AssertEffectiveRootTypeUses(IDbConnection connection, DefinitionCollection[] definitions)
    {
        XVerStructureCatalog catalog = new(connection, XVerProcessor._exclusionSet);
        DefinitionInventoryPackageCounts[] declarations = JsonSerializer.Deserialize<DefinitionInventoryPackageCounts[]>(
            DefinitionInventoryStore.ReadState(connection)!.ExpectedPackageCountsJson)!;
        try
        {
            foreach (DefinitionCollection collection in definitions)
            {
                DbFhirPackage package = DbFhirPackage.SelectList(connection).Single(row =>
                    row.PackageId == collection.MainPackageId && row.PackageVersion == collection.MainPackageVersion);
                StructureDefinition[] authored =
                [
                    .. collection.PrimitiveTypesByName.Values, .. collection.ComplexTypesByName.Values,
                    .. collection.ResourcesByName.Values, .. collection.ProfilesByUrl.Values,
                ];
                ElementDefinition[] elements = [.. authored.SelectMany(owner => owner.Snapshot.Element)];
                int expectedTypes = authored.Sum(owner => owner.Snapshot.Element.Sum(element =>
                    element.Type.Sum(type => Math.Max(1, type.Profile.Count()) * Math.Max(1, type.TargetProfile.Count())) +
                    (ReferenceEquals(element, owner.Snapshot.Element[0]) && element.Type.Count == 0 ? 1 : 0)));
                DefinitionInventoryPackageCounts expected = new(package.Key,
                    collection.PrimitiveTypesByName.Count, collection.ComplexTypesByName.Count,
                    collection.ResourcesByName.Count, collection.ProfilesByUrl.Count, elements.Length, expectedTypes,
                    elements.Sum(element => element.Binding?.Additional.Count ?? 0));
                declarations.Single(row => row.FhirPackageKey == package.Key).ShouldBe(expected);
                catalog.GetDefinitions(package.Key).Count.ShouldBe(authored.Length);
                DbStructureDefinitionFacts.SelectList(connection, FhirPackageKey: package.Key).Count.ShouldBe(authored.Length);
                DbElement.SelectList(connection, FhirPackageKey: package.Key).Count.ShouldBe(expected.ElementCount);
                DbElementType.SelectList(connection, FhirPackageKey: package.Key).Count.ShouldBe(expected.ElementTypeCount);
                DbElementAdditionalBinding.SelectList(connection, FhirPackageKey: package.Key).Count
                    .ShouldBe(expected.AdditionalBindingCount);
                catalog.GetProcessedStructures(package.Key).Count(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
                    .ShouldBe(2);
                using ProfileUseFacts facts = new(catalog, package.Key, package.Key);
                foreach (StructureDefinition source in authored)
                {
                    DbStructureDefinition owner = catalog.ResolveDefinition(package.Key, source.Url + "|" + source.Version)!;
                    StructureDefinition payload = catalog.ReadPayload(owner);
                    payload.ToJson().ShouldBe(source.ToJson());
                    payload.Type.ShouldBe(source.Type);
                    ElementDefinition normalizedRoot = payload.Snapshot.Element[0];
                    source.Snapshot.Element[0].Type.ShouldBeEmpty();
                    normalizedRoot.Type.ShouldBeEmpty();
                    DbElement root = catalog.ResolveRoot(owner);
                    root.FhirPackageKey.ShouldBe(package.Key);
                    root.StructureKey.ShouldBe(owner.Key);
                    root.Id.ShouldBe(source.Snapshot.Element[0].ElementId);
                    root.Path.ShouldBe(source.Type);
                    root.ParentElementKey.ShouldBeNull();
                    DbElementType type = catalog.GetTypeUses(root).ShouldHaveSingleItem();
                    type.FhirPackageKey.ShouldBe(package.Key);
                    type.StructureKey.ShouldBe(owner.Key);
                    type.ElementKey.ShouldBe(root.Key);
                    string.Equals(type.TypeName, source.Type, StringComparison.Ordinal).ShouldBeTrue();
                    type.TypeProfile.ShouldBeNull();
                    type.TypeProfileStructureKey.ShouldBeNull();
                    type.TargetProfile.ShouldBeNull();
                    type.TargetProfileStructureKey.ShouldBeNull();
                    type.TypeStructureKey.ShouldBe(catalog.ResolveNativeType(package.Key, source.Type)!.Key);
                    EffectiveProfileBranch effective = facts.ForDefinition(owner);
                    effective.Diagnostics.ShouldBeEmpty();
                    string.Equals(effective.Elements[""].Types.ShouldHaveSingleItem().Code, source.Type, StringComparison.Ordinal)
                        .ShouldBeTrue();
                }
                foreach (string name in new[] { "Quantity", "SimpleQuantity" })
                {
                    DbStructureDefinition owner = catalog.ResolveDefinition(package.Key, StructureUrl + name)!;
                    DbElement root = catalog.ResolveRoot(owner);
                    string expectedId = name == "SimpleQuantity" && collection.FhirSequence == FhirReleases.FhirSequenceCodes.STU3
                        ? "Quantity:simplequantity" : "Quantity";
                    root.Id.ShouldBe(expectedId);
                    EffectiveProfileBranch definition = facts.ForDefinition(owner);
                    definition.Diagnostics.ShouldBeEmpty();
                    string.Equals(definition.Elements[""].Types.ShouldHaveSingleItem().Code, "Quantity", StringComparison.Ordinal)
                        .ShouldBeTrue();
                    EffectiveProfileUse use = facts.AtUse(root, catalog.GetTypeUses(root).ShouldHaveSingleItem());
                    use.Diagnostics.ShouldBeEmpty();
                    EffectiveProfileBranch branch = use.Branches.ShouldHaveSingleItem();
                    branch.Diagnostics.ShouldBeEmpty();
                    branch.Key.PackageKey.ShouldBe(package.Key);
                    branch.Key.OwnerKey.ShouldBe(owner.Key);
                    branch.Key.ElementKey.ShouldBe(root.Key);
                    branch.Key.ProfileAlternative.ShouldBeNull();
                    branch.Key.TargetProfileAlternative.ShouldBeNull();
                    branch.Elements[""].ElementId.ShouldBe(expectedId);
                    string.Equals(branch.Elements[""].Types.ShouldHaveSingleItem().Code, "Quantity", StringComparison.Ordinal)
                        .ShouldBeTrue();
                    foreach (EffectiveProfileBranch actual in new[] { definition, branch })
                    {
                        actual.Elements[".comparator"].Maximum.ShouldBe(name == "SimpleQuantity" ? 0 : 1);
                        actual.Elements[".comparator"].Origins.ShouldContain(origin =>
                            origin.PackageKey == package.Key && origin.OwnerKey == owner.Key &&
                            origin.ElementId == expectedId + ".comparator");
                        if (name == "SimpleQuantity")
                        {
                            actual.Elements[""].Invariants.ShouldContain(invariant =>
                                invariant.Key == "sqty-1" && invariant.Expression == "comparator.empty()" &&
                                invariant.Origin.PackageKey == package.Key && invariant.Origin.OwnerKey == owner.Key &&
                                invariant.Origin.ElementId == expectedId);
                            catalog.GetOwnedElements(owner).ShouldAllBe(element =>
                                element.Id == expectedId || element.Id.StartsWith(expectedId + ".", StringComparison.Ordinal));
                        }
                    }
                }
                output.WriteLine($"W1 independently authored inventory: {JsonSerializer.Serialize(expected)}");
            }
        }
        finally
        {
            catalog.ReleasePayloads();
        }
    }

    private static void AssertGeneratedResource(StructureDefinition actual, FhirPackageComparisonPair pair,
        string type, string id, string name, string basis)
    {
        DateTimeOffset.TryParse(actual.Date, out _).ShouldBeTrue();
        string title = $"Cross-version Profile for {pair.SourceFhirSequence}.{type} resource for use in FHIR {pair.TargetFhirSequence}";
        string description = $"This cross-version profile allows {pair.SourceFhirSequence} {type} resource content to be represented via FHIR {pair.TargetFhirSequence} {type} resources.";
        StructureDefinition expected = new()
        {
            Id = id, Url = type == CoreConsumerName ? "http://example.org/StructureDefinition/consumer-witness"
                : $"http://hl7.org/fhir/{pair.SourcePackage.FhirVersionShort}/StructureDefinition/{id}",
            Version = "0.1.0", Name = name, Title = title, Status = PublicationStatus.Active,
            Experimental = false, Date = actual.Date, Publisher = "FHIR Infrastructure",
            Contact = [new()
            {
                Name = "FHIR Infrastructure", Telecom = [new()
                {
                    System = ContactPoint.ContactPointSystem.Url,
                    Value = "http://www.hl7.org/Special/committees/fiwg",
                }],
            }],
            Description = description, FhirVersion = EnumUtilityVersion(pair.TargetPackage.PackageVersion),
            Kind = StructureDefinition.StructureDefinitionKind.Resource, Abstract = false, Type = type,
            BaseDefinition = basis, Derivation = StructureDefinition.TypeDerivationRule.Constraint,
            Extension =
            [
                new("http://hl7.org/fhir/StructureDefinition/structuredefinition-wg", new Code("fhir")),
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/package-source",
                    Extension =
                    [
                        new("packageId", new Id($"hl7.fhir.uv.xver-{pair.SourceFhirSequence.ToRLiteral().ToLowerInvariant()}.{pair.TargetFhirSequence.ToRLiteral().ToLowerInvariant()}")),
                        new("version", new FhirString("0.1.0")),
                    ],
                },
            ],
            Differential = new()
            {
                Element = [new()
                {
                    ElementId = type, Path = type, Short = title, Definition = description, Min = 0, Max = "*",
                }],
            },
        };
        actual.ToJson().ShouldBe(expected.ToJson());
    }

    private static void AssertSyntheticSemantics(IDbConnection connection, WorkloadDirectory directory)
    {
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(connection);
        XVerStructureCatalog catalog = new(connection, XVerProcessor._exclusionSet);
        Dictionary<int, DbElement> elementRows = DbElement.SelectList(connection).ToDictionary(row => row.Key);
        List<DbStructureComparison> structures = DbStructureComparison.SelectList(connection);
        List<DbElementComparison> elements = DbElementComparison.SelectList(connection);
        List<DbElementTypeComparison> types = DbElementTypeComparison.SelectList(connection);
        List<DbStructureOutcome> outcomes = DbStructureOutcome.SelectList(connection);
        List<DbElementOutcome> elementOutcomes = DbElementOutcome.SelectList(connection);
        List<DbElementOutcomeTarget> targets = DbElementOutcomeTarget.SelectList(connection);
        structures.Count.ShouldBe(360);
        elements.Count.ShouldBe(980);
        types.Count.ShouldBe(1100);
        outcomes.Count.ShouldBe(260);
        elementOutcomes.Count.ShouldBe(980);
        targets.Count.ShouldBe(980);
        string[] comparedOwners = ["code", "string", "uri", "decimal", "id", "Element", "BackboneElement",
            "Coding", "CodeableConcept", "Quantity", "Range", "Extension", "Resource", "DomainResource",
            "Basic", ConsumerName, "SimpleQuantity", "NegativeQuantity"];
        foreach (DbFhirPackage source in packages)
        {
            foreach (DbFhirPackage target in packages.Where(package => package.Key != source.Key))
            {
                DbStructureComparison[] pairRows = [.. structures.Where(row =>
                    row.SourceFhirPackageKey == source.Key && row.TargetFhirPackageKey == target.Key)];
                pairRows.Select(row => row.SourceId).Order(StringComparer.Ordinal).ShouldBe(comparedOwners.Order(StringComparer.Ordinal));
                foreach (DbStructureComparison row in pairRows)
                {
                    row.SourceCanonicalVersioned.ShouldBe(catalog.GetStructure(row.SourceStructureKey).VersionedUrl);
                    row.TargetCanonicalVersioned.ShouldBe(catalog.GetStructure(row.TargetStructureKey!.Value).VersionedUrl);
                    bool negative = row.SourceId == "NegativeQuantity" && (source.Key == 5 || target.Key == 5);
                    row.Relationship.ShouldBe(negative ? ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget
                        : row.SourceId == ConsumerName ? ConceptMap.ConceptMapRelationship.RelatedTo
                        : ConceptMap.ConceptMapRelationship.Equivalent);
                    row.NotMapped.ShouldBeFalse();
                    row.IsBasedOnFallbackMapping.ShouldBeFalse();
                    if (row.SourceId == "SimpleQuantity")
                    {
                        DbStructureMapping map = DbStructureMapping.SelectSingle(connection, Key: row.StructureMappingKey!.Value)!;
                        map.IsFallback.ShouldBeFalse();
                        map.Comments.ShouldBe("Primary authority");
                        map.ConceptMapSourceKey.ShouldNotBeNull();
                    }
                }
                DbElementComparison rename = elements.Single(row => row.SourceFhirPackageKey == source.Key &&
                    row.TargetFhirPackageKey == target.Key &&
                    row.SourceElementId == ConsumerName + (source.Key == 5 ? ".renamed" : ".label"));
                rename.TargetElementId.ShouldBe(ConsumerName + (target.Key == 5 ? ".renamed" : ".label"));
                rename.Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.Equivalent);
                int steps = Math.Abs(source.DefinitionFhirSequence - target.DefinitionFhirSequence);
                rename.Steps.ShouldBe(steps);
                if (steps == 1)
                {
                    rename.ElementMappingKey.ShouldNotBeNull();
                    DbElementMapping mapping = DbElementMapping.SelectSingle(connection, Key: rename.ElementMappingKey!.Value)!;
                    mapping.SourceElementKey.ShouldBe(rename.SourceElementKey);
                    mapping.TargetElementKey.ShouldBe(rename.TargetElementKey);
                    mapping.ConceptMapSourceKey.ShouldNotBeNull();
                }
                else
                {
                    rename.ElementMappingKey.ShouldBeNull();
                }
                outcomes.Count(row => row.SourceFhirPackageKey == source.Key && row.TargetFhirPackageKey == target.Key).ShouldBe(13);
            }
        }
        outcomes.ShouldAllBe(row => !row.RequiresXVerDefinition &&
            catalog.IsProcessed(row.SourceStructureKey) && catalog.IsProcessed(row.TargetStructureKey!.Value));
        elementOutcomes.ShouldAllBe(row => !row.RequiresXVerDefinition && !row.RequiresExtensionDefinition);
        foreach (DbElementOutcomeTarget target in targets)
        {
            DbElementOutcome owner = elementOutcomes.Single(row => row.Key == target.ElementOutcomeKey);
            owner.SourceFhirPackageKey.ShouldBe(target.SourceFhirPackageKey);
            owner.TargetFhirPackageKey.ShouldBe(target.TargetFhirPackageKey);
            DbElementComparison comparison = elements.Single(row => row.Key == target.ElementComparisonKey);
            comparison.SourceElementKey.ShouldBe(owner.SourceElementKey);
            comparison.TargetElementKey.ShouldBe(target.TargetElementKey);
            comparison.TargetElementId.ShouldBe(target.TargetElementId);
        }
        foreach (DbElementTypeComparison type in types)
        {
            type.Relationship.ShouldBe(type.SourceElementId == "Quantity" &&
                catalog.GetStructure(elementRows[type.SourceElementKey].StructureKey).Id == "NegativeQuantity" &&
                (type.SourceFhirPackageKey == 5 || type.TargetFhirPackageKey == 5)
                    ? ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget
                    : ConceptMap.ConceptMapRelationship.Equivalent);
        }
        AssertNegativeDocuments(connection, 8);
        string[] files = Directory.GetFiles(Path.Combine(directory.Root, "output"), "StructureDefinition-*.json",
            SearchOption.AllDirectories);
        List<string> expectedFiles = [];
        foreach (DbFhirPackage source in packages)
        {
            foreach (DbFhirPackage target in packages.Where(package => package.Key != source.Key &&
                package.DefinitionFhirSequence is FhirReleases.FhirSequenceCodes.STU3 or FhirReleases.FhirSequenceCodes.R4 or FhirReleases.FhirSequenceCodes.R4B))
            {
                FhirPackageComparisonPair pair = new(source, target);
                foreach (string resource in new[] { "Basic", "DomainResource", ConsumerName, "Resource" })
                {
                    string relative = $"output/hl7.fhir.uv.xver-{source.DefinitionFhirSequence.ToRLiteral().ToLowerInvariant()}.{target.DefinitionFhirSequence.ToRLiteral().ToLowerInvariant()}/input/resources/StructureDefinition-profile-{resource}.json";
                    expectedFiles.Add(relative);
                    string path = Path.Combine(directory.Root, relative.Replace('/', Path.DirectorySeparatorChar));
                    StructureDefinition definition = new FhirJsonParser().Parse<StructureDefinition>(File.ReadAllText(path));
                    AssertGeneratedResource(definition, pair, resource, "profile-" + resource,
                        $"Profile_{source.DefinitionFhirSequence.ToRLiteral()}_{resource}_{target.DefinitionFhirSequence.ToRLiteral()}",
                        StructureUrl + resource);
                }
            }
        }
        files.Select(path => Path.GetRelativePath(directory.Root, path).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .ShouldBe(expectedFiles.Order(StringComparer.Ordinal));
        catalog.ReleasePayloads();
    }

    private static void AssertNegativeDocuments(IDbConnection connection, int expectedCount)
    {
        XVerStructureCatalog catalog = new(connection, XVerProcessor._exclusionSet);
        Dictionary<(int, string), JsonElement> normalized = [];
        DbElementOutcome[] negatives = [.. DbElementOutcome.SelectList(connection)
            .Where(row => row.ExtensionProhibitionReason?.Contains("\"ProfileDecisionFormat\":", StringComparison.Ordinal) == true)];
        negatives.Length.ShouldBe(expectedCount);
        foreach (DbElementOutcome outcome in negatives)
        {
            outcome.ExtensionDefinitionIsProhibited.ShouldBeTrue();
            outcome.IsEquivalent.ShouldBeFalse();
            outcome.GenUrl.ShouldBeNull();
            foreach (PersistedProfileDecision decision in ProfileUseFacts.ReadDecisions(outcome.ExtensionProhibitionReason!))
            {
                decision.Input.OwnerKey.ShouldBe(outcome.SourceStructureKey);
                decision.Input.ElementKey.ShouldBe(outcome.SourceElementKey);
                decision.Input.PackageKey.ShouldBe(outcome.SourceFhirPackageKey);
                decision.TargetPackageKey.ShouldBe(outcome.TargetFhirPackageKey);
                CheckDecision(decision);
            }
        }
        catalog.ReleasePayloads();

        void CheckDecision(PersistedProfileDecision decision)
        {
            decision.FormatVersion.ShouldBe(1);
            decision.Diagnostics.IsDefault.ShouldBeFalse();
            decision.RequiredFacts.ShouldContainKey("");
            decision.NativeFacts.ShouldContainKey("");
            if (decision.Kind == ProfileProofKind.Unrepresentable)
            {
                decision.Reasons.ShouldNotBeEmpty();
                decision.TargetCanonical.ShouldBeNull();
            }
            else
            {
                decision.Kind.ShouldBe(ProfileProofKind.NativeReuse);
                decision.TargetCanonical.ShouldNotBeNullOrEmpty();
            }
            foreach (ProfileDecisionReason reason in decision.Reasons)
            {
                reason.Category.ShouldNotBe(ProfileReasonCategory.InvalidFacts);
                reason.Category.ShouldNotBe(ProfileReasonCategory.UnclassifiedRestriction);
                CheckOrigin(reason.Origin);
                if (reason.Restriction is not null)
                {
                    CheckRestriction(reason.Restriction);
                }
            }
            foreach (EffectiveProfileElement element in decision.RequiredFacts.Values.Concat(decision.NativeFacts.Values))
            {
                element.MinimumKnown.ShouldBeTrue();
                element.MaximumKnown.ShouldBeTrue();
                element.Origins.ShouldNotBeEmpty();
                foreach (ProfileFactOrigin origin in element.Origins)
                {
                    CheckOrigin(origin);
                }
                foreach (ProfileRestriction restriction in element.Restrictions)
                {
                    CheckRestriction(restriction);
                }
            }
            foreach (PersistedProfileDecision child in decision.Children)
            {
                CheckDecision(child);
            }
        }

        JsonElement CheckOrigin(ProfileFactOrigin origin)
        {
            DbStructureDefinition owner = catalog.GetStructure(origin.OwnerKey);
            owner.FhirPackageKey.ShouldBe(origin.PackageKey);
            owner.UnversionedUrl.ShouldBe(origin.Canonical);
            owner.Version.ShouldBe(origin.Version);
            if (!normalized.TryGetValue((owner.Key, origin.ElementId), out JsonElement element))
            {
                DbElement row = catalog.ResolveOwnedElement(owner, origin.ElementId)!;
                using JsonDocument document = JsonDocument.Parse(catalog.ReadElement(row).ToJson());
                element = document.RootElement.Clone();
                normalized.Add((owner.Key, origin.ElementId), element);
            }
            return element;
        }

        void CheckRestriction(ProfileRestriction restriction)
        {
            JsonElement element = CheckOrigin(restriction.Origin);
            bool found = element.EnumerateObject().Any(property =>
                JsonElement.DeepEquals(property.Value, restriction.Value) ||
                property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.EnumerateArray().Any(item => JsonElement.DeepEquals(item, restriction.Value)));
            found.ShouldBeTrue($"Restriction {restriction.Kind}/{restriction.Name} must retain exact normalized facts from {restriction.Origin}.");
        }
    }

    private static void AssertCoreRows(IDbConnection connection, FhirPackageComparisonPair pair)
    {
        DbStructureComparison.SelectList(connection).Select(row => row.SourceId).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { CoreConsumerName, "Range" }.Order(StringComparer.Ordinal));
        DbElementComparison[] comparisons = [.. DbElementComparison.SelectList(connection)];
        comparisons.Select(row => row.SourceElementId).Order(StringComparer.Ordinal).ShouldBe(new[]
        {
            CoreConsumerName, CoreConsumerName + ".meta", CoreConsumerName + ".implicitRules", CoreConsumerName + ".language",
            CoreConsumerName + ".text", CoreConsumerName + ".contained", CoreConsumerName + ".measure", "Range", "Range.low", "Range.high",
        }.Order(StringComparer.Ordinal));
        comparisons.ShouldAllBe(row => row.SourceElementId == row.TargetElementId &&
            row.Relationship == (row.SourceElementId == CoreConsumerName + ".contained"
                ? ConceptMap.ConceptMapRelationship.SourceIsBroaderThanTarget : ConceptMap.ConceptMapRelationship.Equivalent));
        DbElementTypeComparison.SelectList(connection).Count.ShouldBe(7);
        DbElementTypeComparison.SelectList(connection).ShouldAllBe(row =>
            row.Relationship == ConceptMap.ConceptMapRelationship.Equivalent && row.SourceTypeLiteral == row.TargetTypeLiteral);
        DbElementOutcomeTarget.SelectList(connection).Count.ShouldBe(10);
        DbStructureOutcome[] outcomes = [.. DbStructureOutcome.SelectList(connection)];
        outcomes.Length.ShouldBe(2);
        outcomes.Single(row => row.SourceId == CoreConsumerName).RequiresXVerDefinition.ShouldBeTrue();
        outcomes.Single(row => row.SourceId == "Range").RequiresXVerDefinition.ShouldBeFalse();
        DbUnsafeCodePairState completed = DbUnsafeCodePairState.SelectSingle(connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)!;
        completed.ComparisonRevision.ShouldNotBeNullOrEmpty();
        completed.OutcomeComparisonRevision.ShouldBe(completed.ComparisonRevision);
        completed.OutcomeFormatVersion.ShouldBe(2);
        AssertNegativeDocuments(connection, 129);
    }

    private void CaptureWorkloadSemantics(IDbConnection connection, WorkloadDirectory directory, string workload)
    {
        using StageMemorySampler memory = new();
        Stopwatch elapsed = Stopwatch.StartNew();
        List<TableSemantics> tables = [];
        using (IDbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using IDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), []));
            }
        }
        foreach (TableSemantics table in tables)
        {
            using IDbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY rowid";
            using IDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                Dictionary<string, object?> row = new(StringComparer.Ordinal);
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    row.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
                }
                table.Rows.Add(row);
            }
        }
        List<FileSemantics> files = [];
        foreach (string path in Directory.GetFiles(directory.Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains("workload.sqlite", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(path);
            files.Add(new(Path.GetRelativePath(directory.Root, path).Replace('\\', '/'),
                Convert.ToBase64String(bytes), Path.GetExtension(path) is ".json" or ".md" or ".xml" or ".ini" or ".txt" or ".yaml"
                    ? Encoding.UTF8.GetString(bytes) : null));
        }
        List<MetadataSubstitution> substitutions = [];
        AddSubstitution(Path.GetFileName(directory.Root)["xver-prepared-".Length..], "fixture-run-id", "fixture directory GUID");
        DbDefinitionInventoryState inventory = DefinitionInventoryStore.ReadState(connection)!;
        AddSubstitution(inventory.StartedRevision, "inventory-revision", "DefinitionInventoryState.StartedRevision");
        AddSubstitution(inventory.CompletedRevision, "inventory-completed-revision", "DefinitionInventoryState.CompletedRevision");
        foreach (DbUnsafeCodeInputState input in DbUnsafeCodeInputState.SelectList(connection).OrderBy(row => row.Key))
        {
            AddSubstitution(input.SnapshotId, $"input-snapshot-{input.Key}", "UnsafeCodeInputState.SnapshotId");
            AddSubstitution(input.CompletedInputRevision, $"input-revision-{input.Key}", "UnsafeCodeInputState.CompletedInputRevision");
        }
        foreach (DbUnsafeCodePairState pair in DbUnsafeCodePairState.SelectList(connection)
            .OrderBy(row => row.SourceFhirPackageKey).ThenBy(row => row.TargetFhirPackageKey))
        {
            AddSubstitution(pair.ComparisonRevision, $"comparison-{pair.SourceFhirPackageKey}-{pair.TargetFhirPackageKey}",
                "UnsafeCodePairState.ComparisonRevision");
        }
        foreach (FileSemantics file in files.Where(file =>
            file.RelativeFilename.StartsWith("output/", StringComparison.Ordinal) &&
            file.RelativeFilename.EndsWith(".json", StringComparison.Ordinal)))
        {
            JsonNode? resource = JsonNode.Parse(file.Text!);
            if (resource is JsonObject value && value["resourceType"] is not null)
            {
                AddTimestamp(value["date"], file.RelativeFilename + "#date");
                AddTimestamp(value["meta"]?["lastUpdated"], file.RelativeFilename + "#meta.lastUpdated");
                AddTimestamp(value["expansion"]?["timestamp"], file.RelativeFilename + "#expansion.timestamp");
            }
        }
        string repository = Directory.GetParent(Directory.GetParent(directory.Root)!.FullName)!.FullName;
        string harnessPath = Path.Combine(repository, "src", "Fhir.CodeGen.Lib.Tests", "XVerPreparedOperationTests.cs");
        byte[] harness = File.ReadAllBytes(harnessPath);
        string harnessSha256 = Convert.ToHexString(SHA256.HashData(harness));
        string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        const string productionBaseline = "d749a7b5e88889e3d5ac7b49e781de478b87e499";
        object identity = new
        {
            Workload = workload, ProductionBaseline = productionBaseline,
            HarnessPath = "src/Fhir.CodeGen.Lib.Tests/XVerPreparedOperationTests.cs",
            HarnessSha256 = harnessSha256, HarnessSource = Encoding.UTF8.GetString(harness),
            Runtime = runtime,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Packages = DbFhirPackage.SelectList(connection),
            Repetitions, ComparatorTerminologyVersion, MaxStepSize = 4,
            CompareVocabulary = workload == "W1", CompareStructures = workload == "W1",
            ConsumerScope = workload == "W1" ? "20 comparer/outcome directions; 12 exports"
                : "full five-core operands; R5->R4B Range/carrier element comparison/outcomes and one write-free build",
            ExportTargets = workload == "W1" ? new[] { "R3", "R4", "R4B" } : ["R4B"],
            Scripts = false, FixtureRoot = directory.Root,
        };
        NegativeSemantics[] negatives = [.. DbElementOutcome.SelectList(connection)
            .Where(row => row.ExtensionProhibitionReason?.Contains("\"ProfileDecisionFormat\":", StringComparison.Ordinal) == true)
            .Select(row => new NegativeSemantics(row.Key,
                JsonSerializer.Deserialize<ProfileDecisionDocument>(row.ExtensionProhibitionReason!)!))];
        string builtPath = Path.Combine(directory.Root, "built-definitions.json");
        if (File.Exists(builtPath))
        {
            JsonNode built = JsonNode.Parse(File.ReadAllText(builtPath))!;
            foreach (JsonNode? item in built["Profiles"]!.AsArray().Concat(built["Extensions"]!.AsArray()))
            {
                JsonNode resource = JsonNode.Parse(item!["Resource"]!.GetValue<string>())!;
                AddTimestamp(resource["date"], "built-definitions.json#" + item["FileName"]!.GetValue<string>() + ".date");
            }
        }
        string capturePath = Path.Combine(directory.Root, "baseline-semantics.json");
        using (FileStream stream = File.Create(capturePath))
        {
            JsonSerializer.Serialize(stream, new
            {
                Identity = identity, Substitutions = substitutions, Tables = tables, NegativeDocuments = negatives, Files = files,
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        elapsed.Stop();
        memory.Finish();
        File.WriteAllText(Path.Combine(directory.Root, "capture-costs.json"), JsonSerializer.Serialize(new
        {
            CaptureMilliseconds = elapsed.Elapsed.TotalMilliseconds, memory.Samples,
            memory.PeakPrivateBytes, memory.PeakWorkingSetBytes,
            RetainedRows = tables.Sum(table => table.Rows.Count),
            RetainedNormalizedJsonBytes = tables.Single(table => table.Name == DbStructureDefinitionFacts.DefaultTableName)
                .Rows.Sum(row => Encoding.UTF8.GetByteCount((string)row[nameof(DbStructureDefinitionFacts.StructureDefinitionJson)]!)),
            RetainedDecisionJsonBytes = DbElementOutcome.SelectList(connection)
                .Sum(row => Encoding.UTF8.GetByteCount(row.ExtensionProhibitionReason ?? "")),
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"{workload} characterization retained: {capturePath}; harness SHA256={harnessSha256}; " +
            $"production baseline={productionBaseline}; runtime={runtime}; capture-only ms={elapsed.Elapsed.TotalMilliseconds:F3}");
        output.WriteLine($"{workload} metadata substitutions={JsonSerializer.Serialize(substitutions)}");

        void AddSubstitution(string? value, string token, string source)
        {
            if (!string.IsNullOrEmpty(value) && substitutions.All(existing => existing.Value != value))
            {
                substitutions.Add(new(value, "<" + token + ">", source));
            }
        }

        void AddTimestamp(JsonNode? value, string source)
        {
            if (value is JsonValue scalar && scalar.TryGetValue(out string? timestamp) && timestamp is not null)
            {
                AddSubstitution(timestamp, $"runtime-timestamp-{substitutions.Count(row => row.Source.Contains('#'))}", source);
            }
        }
    }

    private sealed record TableSemantics(string Name, string? Schema, List<Dictionary<string, object?>> Rows);
    private sealed record FileSemantics(string RelativeFilename, string Base64Bytes, string? Text);
    private sealed record MetadataSubstitution(string Value, string Token, string Source);
    private sealed record NegativeSemantics(int OutcomeKey, ProfileDecisionDocument Document);

    private static void RecordMeasurementIdentity(IDbConnection connection, WorkloadDirectory directory, string workload)
    {
        string repository = Directory.GetParent(Directory.GetParent(directory.Root)!.FullName)!.FullName;
        string harnessPath = Path.Combine(repository, "src", "Fhir.CodeGen.Lib.Tests", "XVerPreparedOperationTests.cs");
        Dictionary<string, long> rows = new(StringComparer.Ordinal);
        List<string> tables = [];
        using (IDbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using IDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        foreach (string table in tables)
        {
            using IDbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            rows.Add(table, Convert.ToInt64(command.ExecuteScalar()));
        }
        using IncrementalHash payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long payloadBytes = 0;
        using (IDbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT FhirPackageKey, StructureKey, StructureDefinitionJson FROM StructureDefinitionFacts ORDER BY FhirPackageKey, StructureKey";
            using IDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                byte[] json = Encoding.UTF8.GetBytes(reader.GetString(2));
                payloadBytes += json.LongLength;
                payloadHash.AppendData(Encoding.UTF8.GetBytes($"{reader.GetInt64(0)}/{reader.GetInt64(1)}/{json.Length}:"));
                payloadHash.AppendData(json);
            }
        }
        using IDbCommand decisions = connection.CreateCommand();
        decisions.CommandText = "SELECT COALESCE(SUM(length(CAST(ExtensionProhibitionReason AS BLOB))), 0) FROM ElementOutcomes";
        long decisionBytes = Convert.ToInt64(decisions.ExecuteScalar());
        string mapRoot = Path.Combine(directory.Root, workload == "W1" ? "input" : "local-maps");
        object[] maps = [.. Directory.GetFiles(mapRoot, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (object)new
            {
                Path = Path.GetRelativePath(directory.Root, path).Replace('\\', '/'),
                Bytes = new FileInfo(path).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            })];
        File.WriteAllText(Path.Combine(directory.Root, "measurement-identity.json"), JsonSerializer.Serialize(new
        {
            Workload = workload, ProductionBaseline = "d749a7b5e88889e3d5ac7b49e781de478b87e499",
            HarnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(harnessPath))),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProcessId = Environment.ProcessId, ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fhir", "packages"),
            Packages = DbFhirPackage.SelectList(connection), Repetitions, ComparatorTerminologyVersion,
            MaxStepSize = 4, Scripts = false, FixtureRoot = directory.Root, Rows = rows,
            RetainedRows = rows.Values.Sum(), RetainedNormalizedJsonBytes = payloadBytes,
            NormalizedPayloadSha256 = Convert.ToHexString(payloadHash.GetHashAndReset()),
            RetainedDecisionJsonBytes = decisionBytes, Maps = maps,
            EvidenceScope = "Post-verification database image sizes and input identities, outside net operation clocks; not live object sizes.",
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DefinitionCollection SyntheticDefinitions(FhirReleases.FhirSequenceCodes sequence)
    {
        string version = sequence.ToLongVersion();
        DefinitionCollection definitions = new()
        {
            Logger = NullLogger.Instance, Name = sequence.ToCorePackageId(), FhirSequence = sequence,
            FhirVersion = EnumUtilityVersion(version), FhirVersionLiteral = version,
            MainPackageId = sequence.ToCorePackageId(), MainPackageVersion = version,
            MainPackageCanonical = "http://hl7.org/fhir",
        };
        foreach (string primitive in new[] { "code", "string", "uri", "decimal", "id" })
        {
            StructureDefinition definition = Definition(primitive, primitive, null);
            definition.Kind = StructureDefinition.StructureDefinitionKind.PrimitiveType;
            definitions.AddPrimitiveType(definition, sequence, definitions.MainPackageId, version);
        }
        Add(Definition("Element", "Element", null));
        Add(Definition("BackboneElement", "BackboneElement", "Element"));
        Add(Definition("Resource", "Resource", null), resource: true);
        Add(Definition("DomainResource", "DomainResource", "Resource"), resource: true);
        StructureDefinition coding = Definition("Coding", "Coding", "Element");
        Child(coding, "system", "uri");
        Child(coding, "version", "string");
        Child(coding, "code", "code");
        Add(coding);
        StructureDefinition concept = Definition("CodeableConcept", "CodeableConcept", "Element");
        Child(concept, "coding", "Coding");
        Add(concept);
        StructureDefinition quantity = Definition("Quantity", "Quantity", "Element");
        Child(quantity, "value", "decimal");
        Child(quantity, "comparator", "code").Binding = new()
        {
            Strength = BindingStrength.Required, ValueSet = "http://example.org/ValueSet/prepared-comparators|" + ComparatorTerminologyVersion,
        };
        Child(quantity, "unit", "string");
        Child(quantity, "system", "uri");
        Child(quantity, "code", "code");
        Add(quantity);
        StructureDefinition simple = CopyQuantity("SimpleQuantity");
        if (sequence == FhirReleases.FhirSequenceCodes.STU3)
        {
            foreach (ElementDefinition element in simple.Snapshot.Element)
            {
                element.ElementId = "Quantity:simplequantity" + element.ElementId["Quantity".Length..];
            }
        }
        simple.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        simple.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "sqty-1", Severity = ConstraintSeverity.Error, Expression = "comparator.empty()",
            Human = "No comparator is permitted.", Source = simple.Url,
        });
        Add(simple, profile: true);
        StructureDefinition negative = CopyQuantity("NegativeQuantity");
        negative.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "prepared-negative", Severity = ConstraintSeverity.Error,
            Expression = sequence == FhirReleases.FhirSequenceCodes.R5 ? "value.empty() or value >= 0" : "value.empty() or value > 0",
            Human = "An intentionally different retained invariant.", Source = negative.Url,
        });
        Add(negative, profile: true);
        foreach (string name in new[] { "PassiveCodingA", "PassiveCodingB" })
        {
            StructureDefinition passive = (StructureDefinition)coding.DeepCopy();
            passive.Id = passive.Name = name;
            passive.Url = StructureUrl + name;
            passive.Version = "profile-definition-7";
            passive.BaseDefinition = coding.Url + "|" + coding.Version;
            passive.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
            passive.Snapshot.Element.Single(element => element.Path == "Coding.code").Fixed = new Code("selected");
            Add(passive, profile: true);
        }
        StructureDefinition range = Definition("Range", "Range", "Element");
        foreach (string bound in new[] { "low", "high" })
        {
            Child(range, bound, "Quantity").Type[0].Profile = [simple.Url + "|" + simple.Version];
        }
        Add(range);
        StructureDefinition extension = Definition("Extension", "Extension", "Element");
        Child(extension, "url", "uri");
        ElementDefinition value = Child(extension, "value[x]", "code");
        value.Type = [.. new[] { "code", "string", "uri", "decimal", "id", "Coding", "CodeableConcept", "Quantity", "Range" }
            .Select(type => new ElementDefinition.TypeRefComponent { Code = type })];
        Add(extension);
        StructureDefinition basic = Definition("Basic", "Basic", "DomainResource");
        Child(basic, "code", "CodeableConcept");
        Add(basic, resource: true);
        StructureDefinition consumer = Definition(ConsumerName, ConsumerName, "DomainResource");
        Child(consumer, sequence == FhirReleases.FhirSequenceCodes.R5 ? "renamed" : "label", "string");
        for (int repetition = 0; repetition < Repetitions; repetition++)
        {
            Child(consumer, "quantity" + repetition, "Quantity").Type[0].Profile = [simple.Url + "|" + simple.Version];
            Child(consumer, "simple" + repetition, "Quantity").Type[0].Profile = [simple.Url + "|" + simple.Version];
            Child(consumer, "range" + repetition, "Range");
            Child(consumer, "coding" + repetition, "Coding").Type[0].Profile =
                [StructureUrl + "PassiveCodingA|profile-definition-7", StructureUrl + "PassiveCodingB|profile-definition-7"];
        }
        Add(consumer, resource: true);
        definitions.AddCodeSystem(new()
        {
            Id = "prepared-comparators", Url = "http://example.org/CodeSystem/prepared-comparators", Version = ComparatorTerminologyVersion,
            Name = "PreparedComparators", Status = PublicationStatus.Active, Content = CodeSystemContentMode.Complete,
            Concept = [new() { Code = "<" }, new() { Code = ">" }],
        }, definitions.MainPackageId, version);
        definitions.AddValueSet(new()
        {
            Id = "prepared-comparators", Url = "http://example.org/ValueSet/prepared-comparators", Version = ComparatorTerminologyVersion,
            Name = "PreparedComparators", Status = PublicationStatus.Active,
            Compose = new() { Include = [new() { System = "http://example.org/CodeSystem/prepared-comparators", Version = ComparatorTerminologyVersion }] },
            Expansion = new()
            {
                Contains = [.. new[] { "<", ">" }.Select(code => new ValueSet.ContainsComponent
                {
                    System = "http://example.org/CodeSystem/prepared-comparators", Version = ComparatorTerminologyVersion, Code = code,
                })],
            },
        }, sequence, definitions.MainPackageId, version);
        return definitions;

        StructureDefinition Definition(string id, string type, string? basis) => new()
        {
            Id = id, Name = id, Url = StructureUrl + id, Version = version, Status = PublicationStatus.Active,
            Kind = StructureDefinition.StructureDefinitionKind.ComplexType, Abstract = false, Type = type,
            BaseDefinition = basis is null ? null : StructureUrl + basis + "|" + version,
            Derivation = StructureDefinition.TypeDerivationRule.Specialization,
            Snapshot = new() { Element = [new() { ElementId = type, Path = type, Min = 0, Max = "*" }] },
        };

        StructureDefinition CopyQuantity(string id)
        {
            StructureDefinition profile = (StructureDefinition)quantity.DeepCopy();
            profile.Id = profile.Name = id;
            profile.Url = StructureUrl + id;
            profile.Version = "quantity-definition-7";
            profile.BaseDefinition = quantity.Url + "|" + quantity.Version;
            profile.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
            return profile;
        }

        void Add(StructureDefinition definition, bool resource = false, bool profile = false)
        {
            if (profile)
            {
                definitions.AddProfile(definition, sequence, definitions.MainPackageId, version);
            }
            else if (resource)
            {
                definition.Kind = StructureDefinition.StructureDefinitionKind.Resource;
                definitions.AddResourceDefinition(definition, sequence, definitions.MainPackageId, version);
            }
            else
            {
                definitions.AddComplexType(definition, sequence, definitions.MainPackageId, version);
            }
        }
    }

    private static FHIRVersion EnumUtilityVersion(string version) => version switch
    {
        "1.0.2" => FHIRVersion.N1_0_2, "3.0.2" => FHIRVersion.N3_0_2, "4.0.1" => FHIRVersion.N4_0_1,
        "4.3.0" => FHIRVersion.N4_3_0, "5.0.0" => FHIRVersion.N5_0_0,
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static ElementDefinition Child(StructureDefinition definition, string name, string type)
    {
        string path = definition.Type + "." + name;
        ElementDefinition element = new()
        {
            ElementId = path, Path = path, Min = 0, Max = "1",
            Base = new() { Path = path, Min = 0, Max = "1" },
            Type = [new() { Code = type }],
        };
        definition.Snapshot.Element.Add(element);
        return element;
    }

    private static void WriteSyntheticMaps(string root, List<DbFhirPackage> packages)
    {
        string[] types = ["code", "string", "uri", "decimal", "id", "Element", "BackboneElement", "Coding",
            "CodeableConcept", "Quantity", "SimpleQuantity", "NegativeQuantity", "Range", "Extension"];
        Write("types", "fallback", [new()
        {
            Source = "http://hl7.org/fhir/fhir-types", Target = "http://hl7.org/fhir/fhir-types",
            Element = [.. types.Select(type => MapElement(type, type, type == "SimpleQuantity"
                ? ConceptMap.ConceptMapRelationship.NotRelatedTo : ConceptMap.ConceptMapRelationship.Equivalent, "Fallback authority"))],
        }]);
        foreach (DbFhirPackage source in packages)
        {
            foreach (DbFhirPackage target in packages.Where(package => package.Key != source.Key))
            {
                string suffix = source.FhirVersionShort + "to" + target.FhirVersionShort;
                Write("types", suffix, [new()
                {
                    Source = $"http://hl7.org/fhir/{source.FhirVersionShort}/data-types",
                    Target = $"http://hl7.org/fhir/{target.FhirVersionShort}/data-types",
                    Element = [MapElement("SimpleQuantity", "SimpleQuantity", ConceptMap.ConceptMapRelationship.Equivalent, "Primary authority")],
                }]);
                Write("resources", suffix, [new()
                {
                    Source = $"http://hl7.org/fhir/{source.FhirVersionShort}/resource-types",
                    Target = $"http://hl7.org/fhir/{target.FhirVersionShort}/resource-types",
                    Element = [.. new[] { "Resource", "DomainResource", "Basic", ConsumerName }.Select(name =>
                        MapElement(name, name, name == ConsumerName ? ConceptMap.ConceptMapRelationship.RelatedTo
                            : ConceptMap.ConceptMapRelationship.Equivalent, "Resource authority"))],
                }]);
                Write("elements", suffix, [new()
                {
                    Source = StructureUrl + ConsumerName + "|" + source.PackageVersion,
                    Target = StructureUrl + ConsumerName + "|" + target.PackageVersion,
                    Element = [MapElement(ConsumerName + (source.PackageVersion == "5.0.0" ? ".renamed" : ".label"),
                        ConsumerName + (target.PackageVersion == "5.0.0" ? ".renamed" : ".label"),
                        ConceptMap.ConceptMapRelationship.Equivalent, "Mapped rename; all other elements use root fallback")],
                }]);
            }
        }
        return;

        void Write(string category, string suffix, ConceptMap.GroupComponent[] groups)
        {
            string directory = Path.Combine(root, "input", category);
            Directory.CreateDirectory(directory);
            ConceptMap map = new()
            {
                Id = "prepared-" + category + "-" + suffix,
                Url = "http://example.org/ConceptMap/prepared-" + category + "-" + suffix,
                Status = PublicationStatus.Active, Group = [.. groups],
            };
            File.WriteAllText(Path.Combine(directory, "ConceptMap-" + category + "-" + suffix + ".json"), map.ToJson());
        }
    }

    private static ConceptMap.SourceElementComponent MapElement(string source, string target,
        ConceptMap.ConceptMapRelationship relationship, string comment) => new()
    {
        Code = source, Target = [new() { Code = target, Relationship = relationship, Comment = comment }],
    };

    private sealed class RealCoreWorkload : IDisposable
    {
        private readonly WorkloadDirectory _directory;

        internal RealCoreWorkload(WorkloadDirectory directory, List<DefinitionCollection> definitions)
        {
            _directory = directory;
            Database = new([.. definitions], directory.Root, "workload.sqlite", NullLoggerFactory.Instance);
            Database.TryLoadFromDefinitionCollections(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes).ShouldBeTrue();
        }

        internal ComparisonDatabase Database { get; private set; }
        internal IDbConnection Connection => Database.DbConnection;
        internal XVerStructureCatalog Catalog => new(Connection, XVerProcessor._exclusionSet);
        internal int ConnectionReopens { get; private set; }
        internal FhirPackageComparisonPair RangePair
        {
            get
            {
                List<DbFhirPackage> packages = DbFhirPackage.SelectList(Connection);
                return new(packages.Single(package => package.PackageVersion == "5.0.0"),
                    packages.Single(package => package.PackageVersion == "4.3.0"));
            }
        }

        internal void AssertRetainedInventory(List<DefinitionCollection> definitions)
        {
            XVerStructureCatalog catalog = Catalog;
            List<DbFhirPackage> packages = DbFhirPackage.SelectList(Connection);
            packages.Count.ShouldBe(5);
            foreach (DefinitionCollection collection in definitions)
            {
                DbFhirPackage package = packages.Single(row => row.PackageId == collection.MainPackageId &&
                    row.PackageVersion == collection.MainPackageVersion);
                StructureDefinition[] authored =
                [
                    .. collection.PrimitiveTypesByName.Values, .. collection.ComplexTypesByName.Values,
                    .. collection.ResourcesByName.Values, .. collection.ProfilesByUrl.Values,
                ];
                catalog.GetDefinitions(package.Key).Select(owner => (owner.UnversionedUrl, owner.Version)).Order()
                    .ShouldBe(authored.Select(owner => (owner.Url, owner.Version)).Order());
                catalog.GetDefinitions(package.Key).Where(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
                    .Select(owner => (owner.UnversionedUrl, owner.Version)).Order().ShouldBe(
                        collection.ProfilesByUrl.Values.Select(owner => (owner.Url, owner.Version)).Order());
                DbStructureDefinitionFacts.SelectList(Connection, FhirPackageKey: package.Key).Count.ShouldBe(authored.Length);
                DbStructureDefinition range = catalog.ResolveNativeType(package.Key, "Range")!;
                DbStructureDefinition simple = catalog.ResolveDefinition(package.Key, StructureUrl + "SimpleQuantity")!;
                foreach (string bound in new[] { "low", "high" })
                {
                    DbElementType type = catalog.GetTypeUses(catalog.ResolveOwnedElement(range, "Range." + bound)!)
                        .ShouldHaveSingleItem();
                    type.TypeName.ShouldBe("Quantity");
                    type.TypeProfile.ShouldBe(simple.UnversionedUrl);
                    type.TypeProfileStructureKey.ShouldBe(simple.Key);
                    type.TypeStructureKey.ShouldBe(catalog.ResolveNativeType(package.Key, "Quantity")!.Key);
                }
                if (collection.FhirSequence == FhirReleases.FhirSequenceCodes.STU3)
                {
                    catalog.ResolveRoot(simple).Id.ShouldBe("Quantity:simplequantity");
                }
            }
            catalog.ReleasePayloads();
        }

        internal void Reopen()
        {
            Dispose();
            Database = new(_directory.Root, "workload.sqlite", NullLoggerFactory.Instance);
            DbMappingClasses.LoadIndices(Connection);
            DbComparisonClasses.LoadIndices(Connection);
            DbOutcomeClasses.LoadIndices(Connection);
            ConnectionReopens++;
        }

        internal void LoadRangeMaps(FhirPackageComparisonPair pair)
        {
            XVerStructureCatalog catalog = Catalog;
            string sourceRoot = Path.Combine(_directory.Root, "local-maps");
            string suffix = pair.SourcePackage.FhirVersionShort + "to" + pair.TargetPackage.FhirVersionShort;
            WriteMap("resources", new ConceptMap.GroupComponent
            {
                Source = $"http://hl7.org/fhir/{pair.SourcePackage.FhirVersionShort}/resource-types",
                Target = $"http://hl7.org/fhir/{pair.TargetPackage.FhirVersionShort}/resource-types",
                Element = [new()
                {
                    Code = CoreConsumerName,
                    Target = [new() { Code = CoreConsumerName, Relationship = ConceptMap.ConceptMapRelationship.RelatedTo }],
                }],
            });
            List<ConceptMap.GroupComponent> groups = [];
            foreach (string canonical in new[] { CoreConsumerUrl, StructureUrl + "Range" })
            {
                DbStructureDefinition source = catalog.ResolveDefinition(pair.SourcePackageKey, canonical)!;
                DbStructureDefinition target = catalog.ResolveDefinition(pair.TargetPackageKey, canonical)!;
                groups.Add(new()
                {
                    Source = source.VersionedUrl, Target = target.VersionedUrl,
                    Element = [.. catalog.GetOwnedElements(source).Select(element => new ConceptMap.SourceElementComponent
                    {
                        Code = element.Id,
                        Target = [new() { Code = element.Id, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                    })],
                });
            }
            WriteMap("elements", [.. groups]);
            WriteMap("types", new ConceptMap.GroupComponent
            {
                Source = $"http://hl7.org/fhir/{pair.SourcePackage.FhirVersionShort}/data-types",
                Target = $"http://hl7.org/fhir/{pair.TargetPackage.FhirVersionShort}/data-types",
                Element = [.. new[] { "Range", "Quantity", "SimpleQuantity", "Meta", "Narrative" }
                    .Select(name => new ConceptMap.SourceElementComponent
                    {
                        Code = name, Target = [new() { Code = name, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                    })],
            });
            new MappingLoader(Connection, NullLoggerFactory.Instance)
                .TryLoadCrossVersionSourceMaps(sourceRoot, useInternalTypeMaps: true).ShouldBeTrue();

            void WriteMap(string category, params ConceptMap.GroupComponent[] mapGroups)
            {
                string directory = Path.Combine(sourceRoot, "input", category);
                Directory.CreateDirectory(directory);
                ConceptMap map = new()
                {
                    Id = category + "-" + suffix,
                    Url = "http://example.org/ConceptMap/consumer-" + category + "-" + suffix,
                    Status = PublicationStatus.Active, Group = [.. mapGroups],
                };
                File.WriteAllText(Path.Combine(directory, "ConceptMap-" + category + "-" + suffix + ".json"), map.ToJson());
            }
        }

        internal void CompareRangeElements(FhirPackageComparisonPair pair)
        {
            DbComparisonClasses.CreateTables(Connection);
            string? revision = UnsafeCodeInputStore.BeginComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
            DbComparisonCache<DbElementComparison> elements = new();
            DbComparisonCache<DbElementTypeComparison> types = new();
            ElementComparer comparer = new(Connection, NullLoggerFactory.Instance, elements, types, DbFhirPackage.SelectList(Connection));
            XVerStructureCatalog catalog = Catalog;
            foreach (string canonical in new[] { CoreConsumerUrl, StructureUrl + "Range" })
            {
                DbStructureDefinition source = catalog.ResolveDefinition(pair.SourcePackageKey, canonical)!;
                DbStructureDefinition target = catalog.ResolveDefinition(pair.TargetPackageKey, canonical)!;
                DbStructureMapping map = DbStructureMapping.SelectList(Connection, SourceStructureKey: source.Key,
                    TargetStructureKey: target.Key).ShouldHaveSingleItem();
                CoreStructureComparison envelope = new(pair, source, target, map.Key);
                envelope.Key = DbStructureComparison.Insert(Connection, envelope);
                comparer.DoElementComparisons(new StructureComparer.StructureComparisonTrackingRecord
                {
                    PackagePair = pair, SourcePackage = pair.SourcePackage, TargetPackage = pair.TargetPackage,
                    SourceStructure = source, TargetStructure = target, TargetStructureKey = target.Key,
                    ExplicitMapping = map, ExplicitMappingSource = DbMappingSourceFile.SelectSingle(Connection, Key: map.ConceptMapSourceKey!.Value),
                    ComparisonRecordKey = envelope.Key,
                }).ShouldNotBeEmpty();
            }
            elements.ToAdd.Insert(Connection, insertPrimaryKey: true);
            types.ToAdd.Insert(Connection, insertPrimaryKey: true);
            UnsafeCodeInputStore.CompleteComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey, revision);
        }

        internal void AssertRangeComparisons(FhirPackageComparisonPair pair)
        {
            XVerStructureCatalog catalog = Catalog;
            DbStructureDefinition range = catalog.ResolveNativeType(pair.SourcePackageKey, "Range")!;
            DbStructureDefinition simple = catalog.ResolveDefinition(pair.TargetPackageKey, StructureUrl + "SimpleQuantity|4.3.0")!;
            foreach (string bound in new[] { "low", "high" })
            {
                DbElement element = catalog.ResolveOwnedElement(range, "Range." + bound)!;
                DbElementComparison comparison = DbElementComparison.SelectList(Connection, SourceElementKey: element.Key).ShouldHaveSingleItem();
                comparison.TargetElementId.ShouldBe(element.Id);
                DbElementMapping map = DbElementMapping.SelectSingle(Connection, Key: comparison.ElementMappingKey!.Value)!;
                map.SourceElementKey.ShouldBe(element.Key);
                map.TargetElementKey.ShouldBe(comparison.TargetElementKey);
                map.ConceptMapSourceKey.ShouldNotBeNull();
                DbElementTypeComparison type = DbElementTypeComparison.SelectList(Connection,
                    ElementComparisonKey: comparison.Key).ShouldHaveSingleItem();
                type.NotMapped.ShouldBeFalse();
                type.Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.Equivalent);
                ProfileDecisionDocument document = JsonSerializer.Deserialize<ProfileDecisionDocument>(type.TechnicalMessage!)!;
                document.ProfileDecisionFormat.ShouldBe(1);
                PersistedProfileDecision decision = document.Decisions.ShouldHaveSingleItem();
                decision.Kind.ShouldBe(ProfileProofKind.NativeReuse);
                decision.TargetCanonical.ShouldBe(simple.VersionedUrl);
                decision.RequiredFacts[".comparator"].Maximum.ShouldBe(0);
                decision.NativeFacts[".comparator"].Maximum.ShouldBe(0);
                ElementDefinition.ConstraintComponent invariant = catalog.ReadPayload(simple).Snapshot.Element[0]
                    .Constraint.Single(constraint => constraint.Key == "sqty-1");
                FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
                CompiledExpression expression = compiler.Compile(invariant.Expression!);
                foreach (bool prohibited in new[] { false, true })
                {
                    Quantity witness = new()
                    {
                        Value = 1, Comparator = prohibited ? Quantity.QuantityComparator.LessThan : null,
                    };
                    expression(witness.ToTypedElement(), new EvaluationContext()).ShouldHaveSingleItem().Value.ShouldBe(!prohibited);
                    (witness.Comparator is null).ShouldBe(!prohibited);
                }
            }
            catalog.ReleasePayloads();
        }

        internal void GenerateRangeOutcomes(FhirPackageComparisonPair pair)
        {
            DbOutcomeClasses.CreateTables(Connection);
            DbUnsafeCodePairState dependency = UnsafeCodeInputStore.BeginOutcomes(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
            DbRecordCache<DbElementOutcome> elements = new();
            DbRecordCache<DbElementOutcomeTarget> targets = new();
            ElementOutcomeGenerator generator = new(Connection, NullLoggerFactory.Instance, pair, elements, targets);
            XVerStructureCatalog catalog = Catalog;
            foreach (DbStructureComparison comparison in DbStructureComparison.SelectList(Connection))
            {
                DbStructureDefinition source = catalog.GetStructure(comparison.SourceStructureKey);
                DbStructureDefinition target = catalog.GetStructure(comparison.TargetStructureKey!.Value);
                CoreStructureOutcome schedule = new(pair, source, target, comparison.Key)
                {
                    RequiresXVerDefinition = source.UnversionedUrl == CoreConsumerUrl,
                };
                schedule.Key = DbStructureOutcome.Insert(Connection, schedule);
                generator.ProcessSourceStructure(source, new()
                {
                    [target.Key] = new()
                    {
                        SourceStructure = source, TargetStructure = target, StructureComparison = comparison,
                        SourceRootElement = catalog.ResolveRoot(source), TargetRootElement = catalog.ResolveRoot(target),
                        StructureOutcomeKey = schedule.Key,
                        IsFullyMappedAcrossAllTargets = true, IsFullyMappedToThisTarget = true,
                    },
                });
            }
            elements.ToAdd.Insert(Connection, insertPrimaryKey: true);
            targets.ToAdd.Insert(Connection, insertPrimaryKey: true);
            new UnsafeCodeOutcomeGenerator(Connection).GenerateForPair(pair, dependency);
        }

        internal StructureFhirExporter.BuiltDefinitions BuildRangeDefinitions(FhirPackageComparisonPair pair)
        {
            UnsafeCodeInputStore.RequireExportReady(Connection, [pair]);
            XVerStructureCatalog catalog = Catalog;
            DbStructureOutcome.SelectList(Connection).ShouldAllBe(outcome =>
                catalog.IsProcessed(outcome.SourceStructureKey) && catalog.IsProcessed(outcome.TargetStructureKey!.Value));
            DbElementOutcome.SelectList(Connection).ShouldAllBe(outcome => catalog.IsProcessed(outcome.SourceStructureKey));
            ConfigXVer config = new()
            {
                OutputDirectory = Path.Combine(_directory.Root, "core-output"),
                ExportR2 = false, ExportR3 = false, ExportR4 = true, ExportR4B = true,
                ExportR5 = false, ExportR6 = false, LogFactory = NullLoggerFactory.Instance,
            };
            XVerExporter exporter = new(Connection, config);
            IgExporter ig = new(Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
            StructureFhirExporter.BuiltDefinitions built = new StructureFhirExporter(exporter, Connection, NullLoggerFactory.Instance)
                .BuildDefinitions(ig.PrepareXVerIgs(ig.BuildExportSchedule(specificPairs: [pair.SequencePair])).XVerIgs.ShouldHaveSingleItem());
            Directory.Exists(config.OutputDirectory).ShouldBeFalse();
            catalog.ReleasePayloads();
            return built;
        }

        public void Dispose()
        {
            SqliteConnection connection = (SqliteConnection)Connection;
            ((IDisposable)Database).Dispose();
            SqliteConnection.ClearPool(connection);
        }
    }

    // Conservative structure envelopes and a single carrier artifact schedule; all
    // element/type comparisons, outcomes, profile proofs and builds use real consumers.
    private sealed class CoreStructureComparison : DbStructureComparison
    {
        [SetsRequiredMembers]
        internal CoreStructureComparison(FhirPackageComparisonPair pair, DbStructureDefinition source,
            DbStructureDefinition target, int mappingKey)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceStructureKey = source.Key;
            TargetStructureKey = target.Key;
            SourceCanonicalVersioned = source.VersionedUrl;
            SourceCanonicalUnversioned = source.UnversionedUrl;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceVersion = source.Version;
            TargetCanonicalVersioned = target.VersionedUrl;
            TargetCanonicalUnversioned = target.UnversionedUrl;
            TargetId = target.Id;
            TargetName = target.Name;
            TargetVersion = target.Version;
            StructureMappingKey = mappingKey;
            Steps = Math.Abs(pair.SourcePackage.PackageArrayIndex - pair.TargetPackage.PackageArrayIndex);
            Relationship = ConceptMap.ConceptMapRelationship.RelatedTo;
            int?[] keys = new int?[6];
            keys[pair.SourcePackage.PackageArrayIndex] = source.Key;
            keys[pair.TargetPackage.PackageArrayIndex] = target.Key;
            ContentKeys = keys;
        }
    }

    private sealed class CoreStructureOutcome : DbStructureOutcome
    {
        [SetsRequiredMembers]
        internal CoreStructureOutcome(FhirPackageComparisonPair pair, DbStructureDefinition source,
            DbStructureDefinition target, int comparisonKey)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirSequence = pair.TargetFhirSequence;
            StructureComparisonKey = comparisonKey;
            SourceStructureKey = source.Key;
            TargetStructureKey = target.Key;
            SourceArtifactClass = source.ArtifactClass;
            TargetArtifactClass = target.ArtifactClass;
            SourceCanonicalVersioned = source.VersionedUrl;
            SourceCanonicalUnversioned = source.UnversionedUrl;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceVersion = source.Version;
            TargetCanonicalVersioned = target.VersionedUrl;
            TargetCanonicalUnversioned = target.UnversionedUrl;
            TargetId = target.Id;
            TargetName = target.Name;
            TargetVersion = target.Version;
            RequiresXVerDefinition = true;
            TotalSourceCount = 1;
            TotalTargetCount = 1;
            GenShortId = "consumer-witness";
            GenLongId = GenShortId;
            GenName = "QuantityConsumerWitnessProfile";
            GenUrl = "http://example.org/StructureDefinition/consumer-witness";
            GenFileName = "StructureDefinition-consumer-witness";
            ElementConceptMapLongId = "consumer-witness-elements";
            ElementConceptMapShortId = ElementConceptMapLongId;
            ElementConceptMapName = "QuantityConsumerWitnessElements";
            ElementConceptMapUrl = "http://example.org/ConceptMap/consumer-witness-elements";
            ElementConceptMapFileName = "ConceptMap-consumer-witness-elements";
            Comments = "Explicit local carrier schedule; no whole-core export claim.";
        }
    }

    private sealed record StageCost(string Stage, double Milliseconds, long AllocatedBytes,
        long EntryPrivateBytes, long ExitPrivateBytes, long EntryManagedBytes, long ExitManagedBytes,
        long ProcessPeakWorkingSetBytes, long DatabaseBytes, int Samples, long SampledPeakPrivateBytes, long SampledPeakWorkingSetBytes);

    private sealed class StageMemorySampler : IDisposable
    {
        private readonly object _gate = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly System.Threading.Timer _timer;
        private bool _finished;

        internal StageMemorySampler()
        {
            Sample();
            _timer = new(_ => Sample(), null, 25, 25);
        }

        internal int Samples { get; private set; }
        internal long PeakPrivateBytes { get; private set; }
        internal long PeakWorkingSetBytes { get; private set; }

        private void Sample()
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }
                _process.Refresh();
                Samples++;
                PeakPrivateBytes = Math.Max(PeakPrivateBytes, _process.PrivateMemorySize64);
                PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, _process.WorkingSet64);
            }
        }

        internal void Finish()
        {
            _timer.Dispose();
            lock (_gate)
            {
                if (!_finished)
                {
                    Sample();
                    _finished = true;
                    _process.Dispose();
                }
            }
        }

        public void Dispose() => Finish();
    }

    private sealed class WorkloadDirectory : IDisposable
    {
        internal WorkloadDirectory()
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md")))
            {
                repository = repository.Parent;
            }
            repository.ShouldNotBeNull();
            Root = Path.Combine(repository.FullName, "temp", "xver-prepared-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }
        internal bool Completed { get; set; }
        internal bool RetainCharacterization { get; set; }

        public void Dispose()
        {
            // Measurement and characterization evidence survive disposal for review.
        }
    }
}
