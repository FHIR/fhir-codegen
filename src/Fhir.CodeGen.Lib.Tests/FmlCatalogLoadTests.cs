// <copyright file="FmlCatalogLoadTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;
using ObservedConnection = Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.ObservedOperandConnection;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class FmlCatalogLoadTests(ITestOutputHelper output)
{
    private enum Corpus { Alias, Native, Dependent, Reverse, Complete }
    private static readonly string[] Tables = ["Structures", "FhirPackages", "Elements", "StructureDefinitionFacts", "ElementTypes"];
    private const string SlicedRoot = "Quantity:simplequantity";

    [Fact]
    public void CharacterizeIncrementalTraversal()
    {
        foreach (Corpus corpus in Enum.GetValues<Corpus>())
        {
            Run(corpus, wholeLoad: false, observe: true, persisted: false);
        }
    }

    [Fact]
    public void CharacterizeWholeLoad() => Run(Corpus.Complete, wholeLoad: true, observe: true, persisted: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CountFixedWorkload(bool wholeLoad) => Run(Corpus.Complete, wholeLoad, observe: true, persisted: true);

    [Fact]
    public void MeasureFixedFmlTraversal() => Run(Corpus.Complete, wholeLoad: false, observe: false, persisted: true);

    [Fact]
    public void MeasureFixedMapLoad() => Run(Corpus.Complete, wholeLoad: true, observe: false, persisted: true);

    private void Run(Corpus corpus, bool wholeLoad, bool observe, bool persisted)
    {
        using UnsafeCodeTestFixture fixture = new(initializeIndices: true, persistDatabase: persisted);
        ArrangeInventory(fixture, persisted);
        using ObservedConnection? observer = observe ? new(fixture.Connection) : null;
        IDbConnection db = (IDbConnection?)observer ?? fixture.Connection;
        using MessageLog log = new();
        using MappingLoader loader = new(db, log);
        FmlLoader traversal = new(loader, db, fixture.Packages, log);
        if (!wholeLoad)
        {
            loader.TryLoadCrossVersionSourceMaps(fixture.SourceRoot, true).ShouldBeTrue();
        }
        WriteCorpus(fixture, corpus);
        output.WriteLine($"scope={(wholeLoad ? "map" : "fml")} corpus={corpus} fileBacked={persisted} internalTypeMaps=true " +
            $"packages={string.Join(",", fixture.Packages.Select(p => $"{p.PackageId}@{p.PackageVersion}"))} logger=FmlLoader:Information");
        if (persisted)
        {
            DescribeFixture(fixture);
        }
        log.Messages.Clear();
        observer?.Statements.Clear();
        bool success = true;
        long allocated = GC.GetTotalAllocatedBytes(true);
        long start = Stopwatch.GetTimestamp();
        if (wholeLoad)
        {
            success = loader.TryLoadCrossVersionSourceMaps(fixture.SourceRoot, true);
        }
        else
        {
            traversal.LoadSourceFml(fixture.SourceRoot);
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        success.ShouldBeTrue();
        if (observer != null)
        {
            foreach (string table in Tables)
            {
                string[] reads = [.. observer.Statements.Where(sql => Regex.IsMatch(sql,
                    $@"^\s*SELECT\b.*?\bFROM\s+{table}\b", RegexOptions.Singleline | RegexOptions.IgnoreCase))];
                int filtered = reads.Count(sql => Regex.IsMatch(sql, @"\bWHERE\b", RegexOptions.IgnoreCase));
                output.WriteLine($"SQL {table}: full={reads.Length - filtered} filtered={filtered}");
            }
        }
        else
        {
            output.WriteLine($"MEASURE elapsedMs={elapsed.TotalMilliseconds:F3} allocatedBytes={allocated}");
        }
        CheckMessages(log, start, corpus, fixture.SourceRoot);
        CheckEdges(fixture, corpus);
        if (corpus == Corpus.Complete)
        {
            foreach ((string table, string[] rows) in fixture.MixedChoiceRepresentationState()
                .Where(kv => kv.Key.Contains("Map", StringComparison.Ordinal) || kv.Key == "UnsafeCodeElementAnnotations"))
            {
                output.WriteLine($"LOGICAL {table} rows={rows.Length} sha256={Digest(string.Join("\n", rows))}");
                if (observe)
                {
                    foreach (string row in rows)
                    {
                        output.WriteLine($"ROW {table} {row}");
                    }
                }
            }
        }
    }

    private static void ArrangeInventory(UnsafeCodeTestFixture fixture, bool padded)
    {
        foreach (DbFhirPackage p in fixture.Packages)
        {
            DbStructureDefinition quantity = fixture.AddStructure(p, "Quantity");
            fixture.AddElement(quantity, "Quantity"); // deliberately no .unit on the native projection
            DbStructureDefinition profile = fixture.AddStructure(p, "SimpleQuantity", FhirArtifactClassEnum.Profile);
            fixture.DeclareProfile(profile, quantity);
            DbElement root = fixture.AddElement(profile, SlicedRoot);
            root.Path = "Quantity";
            DbElement.Update(fixture.Connection, [root]);
            DbElement unit = fixture.AddElement(profile, SlicedRoot + ".unit", "string");
            unit.Path = "Quantity.unit";
            DbElement.Update(fixture.Connection, [unit]);
            DbStructureDefinition carrier = fixture.AddStructure(p, "Carrier");
            fixture.AddElement(carrier, "Carrier");
            fixture.AddElement(carrier, "Carrier.value[x]", "string");
            DbElement measure = fixture.AddElement(carrier, "Carrier.measure", "Quantity");
            fixture.AddType(measure, "Quantity", quantity, profile);
            DbStructureDefinition passive = fixture.AddStructure(p, "PassiveCarrier", FhirArtifactClassEnum.Profile);
            fixture.DeclareProfile(passive, carrier);
            fixture.AddElement(passive, "Carrier");
            fixture.AddElement(passive, "Carrier.value[x]", "string");
            if (padded)
            {
                DbStructureDefinition padding = fixture.AddStructure(p, "Padding");
                fixture.AddElement(padding, "Padding");
                for (int i = 0; i < 128; i++)
                {
                    DbElement item = fixture.AddElement(padding, $"Padding.field{i}", "string");
                    item.Definition = new string('p', 1024);
                    DbElement.Update(fixture.Connection, [item]);
                }
            }
        }
        fixture.InsertFilterElements(); // also publishes the completed inventory
    }

    private static void WriteCorpus(UnsafeCodeTestFixture fixture, Corpus corpus)
    {
        string body = "src.value -> tgt.value;";
        if (corpus >= Corpus.Dependent)
        {
            body += "\nsrc.measure as m -> tgt.measure as n then Details(m, n);";
        }
        string groups = corpus >= Corpus.Dependent ? """
            group Details(source m, target n) {
              m.unit -> n.unit;
            }
            """ : "";
        if (corpus == Corpus.Complete)
        {
            body += "\nsrc.measure.missing -> tgt.measure.missing;";
        }
        Write(0, 1, "Forward", "Carrier", body, groups, native: corpus >= Corpus.Native);
        if (corpus >= Corpus.Reverse)
        {
            Write(1, 0, "Sliced", "SimpleQuantity", "src.unit -> tgt.unit;");
        }
        if (corpus == Corpus.Complete)
        {
            Write(0, 2, "Passive", "PassiveCarrier", "src.value -> tgt.value;");
            Write(2, 0, "Authored", "ValueSet", "src.compose.include.filter.op -> tgt.compose.include.filter.op;");
        }

        void Write(int s, int t, string name, string owner, string rule, string dependent = "", bool native = false)
        {
            DbFhirPackage src = fixture.Packages[s], tgt = fixture.Packages[t];
            string directory = Directory.CreateDirectory(Path.Combine(fixture.SourceRoot, "input", $"{src.ShortName}to{tgt.ShortName}")).FullName;
            File.WriteAllText(Path.Combine(directory, name + ".fml"), $$"""
                /// url = "http://example.org/StructureMap/{{name}}"
                /// name = "{{name}}"
                uses "{{fixture.Structure(src, owner).VersionedUrl}}" alias SourceOwner as source
                uses "{{fixture.Structure(tgt, owner).VersionedUrl}}" alias TargetOwner as target
                group Main(source src : {{(native ? owner : "SourceOwner")}}, target tgt : TargetOwner) {
                  {{rule}}
                }
                {{dependent}}
                """);
        }
    }

    private void CheckMessages(MessageLog log, long start, Corpus corpus, string root)
    {
        List<string> expected = [$"Maps from FML for Forward: 1 structures, {(corpus >= Corpus.Dependent ? 3 : 1)} elements"];
        if (corpus >= Corpus.Reverse)
        {
            expected.Add("Maps from FML for Sliced: 1 structures, 1 elements");
        }
        if (corpus == Corpus.Complete)
        {
            expected.Add("Maps from FML for Authored: 1 structures, 1 elements");
        }
        List<string> successes = [];
        long previous = start;
        foreach ((long stamp, LogLevel level, string message) in log.Messages)
        {
            if (message.StartsWith("Maps from FML for ", StringComparison.Ordinal))
            {
                output.WriteLine($"MESSAGE {successes.Count + 1} {(successes.Count == 0 ? "firstMs" : "gapMs")}={Stopwatch.GetElapsedTime(previous, stamp).TotalMilliseconds:F3} {message}");
                successes.Add(message);
                previous = stamp;
            }
            else
            {
                output.WriteLine($"LOG {level} {message.Replace(root, "<fixture>", StringComparison.Ordinal)}");
            }
        }
        log.Messages.ShouldNotContain(m => m.Level >= LogLevel.Warning);
        successes.ShouldBe(expected);
        log.Messages.Count(m => m.Message.Contains("Failed to resolve", StringComparison.Ordinal)).ShouldBe(corpus == Corpus.Complete ? 1 : 0);
        if (corpus == Corpus.Complete)
        {
            log.Messages.ShouldContain(m => m.Message.Contains("Failed to resolve source `src.measure.missing` in group Main"));
        }
    }

    private void CheckEdges(UnsafeCodeTestFixture fixture, Corpus corpus)
    {
        List<DbElementMapping> rows = DbElementMapping.SelectList(fixture.Connection).Where(r => r.FmlSourceKey != null).ToList();
        Edge(0, 1, "Forward", "Carrier", "Carrier.value[x]");
        if (corpus >= Corpus.Dependent)
        {
            Edge(0, 1, "Forward", "Carrier", "Carrier.measure", simple: false);
            Edge(0, 1, "Forward", "Carrier", "Carrier.measure.unit", stored: false);
        }
        if (corpus >= Corpus.Reverse)
        {
            Edge(1, 0, "Sliced", "SimpleQuantity", SlicedRoot + ".unit");
        }
        if (corpus == Corpus.Complete)
        {
            Edge(2, 0, "Authored", "ValueSet", UnsafeCodeTestFixture.FilterElement);
            DbMappingSourceFile passive = DbMappingSourceFile.SelectSingle(fixture.Connection, Filename: "Passive.fml")!;
            DbStructureMapping.SelectList(fixture.Connection, FmlSourceKey: passive.Key).ShouldBeEmpty();
            List<DbUnsafeCodeElementAnnotation> annotations = DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection);
            annotations.Count.ShouldBe(2);
            annotations.ShouldAllBe(r => r.RequiresOmittedMandatoryCode && r.TargetIndex == 0 &&
                r.SourceElementId == UnsafeCodeTestFixture.FilterElement && r.SourceRelativeFilename.EndsWith(".json"));
            List<DbUnsafeCodeMapEvidence> evidence = DbUnsafeCodeMapEvidence.SelectList(fixture.Connection);
            evidence.Count.ShouldBe(8);
            evidence.Count(r => r.ExplicitNoMap).ShouldBe(4);
        }
        rows.ShouldBeEmpty(); // includes the missing postfix and any accidental passive edge

        void Edge(int s, int t, string name, string owner, string path, bool simple = true, bool stored = true)
        {
            DbFhirPackage src = fixture.Packages[s], tgt = fixture.Packages[t];
            DbStructureDefinition from = fixture.Structure(src, owner), to = fixture.Structure(tgt, owner);
            DbElementMapping edge = rows.Single(r => r.SourceElementId == path && r.SourceFhirPackageKey == src.Key);
            rows.Remove(edge);
            edge.TargetElementId.ShouldBe(path);
            edge.TargetFhirPackageKey.ShouldBe(tgt.Key);
            edge.SourceStructureKey.ShouldBe(from.Key);
            edge.TargetStructureKey.ShouldBe(to.Key);
            edge.SourceElementKey.ShouldBe(stored ? DbElement.SelectSingle(fixture.Connection, StructureKey: from.Key, Id: path)!.Key : (int?)null);
            edge.TargetElementKey.ShouldBe(stored ? DbElement.SelectSingle(fixture.Connection, StructureKey: to.Key, Id: path)!.Key : (int?)null);
            edge.FmlIsSimpleCopy.ShouldBe(simple);
            DbMappingSourceFile file = DbMappingSourceFile.SelectSingle(fixture.Connection, Key: edge.FmlSourceKey)!;
            string relative = Path.Combine($"{src.ShortName}to{tgt.ShortName}", name + ".fml");
            file.SourceRelativeFilename.ShouldBe(Path.Combine("input", relative));
            file.IsFml.ShouldBeTrue();
            file.Url.ShouldBe("http://example.org/StructureMap/" + name);
            DbStructureMapping mapping = DbStructureMapping.SelectSingle(fixture.Connection, Key: edge.StructureMappingKey)!;
            mapping.SourceStructureKey.ShouldBe(from.Key);
            mapping.TargetStructureKey.ShouldBe(to.Key);
            mapping.FmlSourceKey.ShouldBe(file.Key);
            if (name == "Authored")
            {
                edge.FmlFilename.ShouldBeNull(); // existing ConceptMap edge, updated rather than replaced
                const string authoredName = "ConceptMap-elements-5to4.json";
                edge.ConceptMapFilename.ShouldBe(authoredName);
                DbMappingSourceFile authored = DbMappingSourceFile.SelectSingle(fixture.Connection, Key: edge.ConceptMapSourceKey)!;
                authored.IsConceptMap.ShouldBeTrue();
                authored.SourceRelativeFilename.ShouldBe(Path.Combine("input", "elements", authoredName));
                edge.Relationship.ShouldBe(Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent);
            }
            else
            {
                edge.FmlFilename.ShouldBe(relative);
                edge.ConceptMapSourceKey.ShouldBeNull();
            }
            output.WriteLine($"EDGE {src.PackageId}@{src.PackageVersion}:{from.VersionedUrl}#{path} -> " +
                $"{tgt.PackageId}@{tgt.PackageVersion}:{to.VersionedUrl}#{path} stored={stored} simple={simple} file={relative} conceptMap={edge.ConceptMapFilename}");
        }
    }

    private void DescribeFixture(UnsafeCodeTestFixture fixture)
    {
        using IDbCommand cmd = fixture.Connection.CreateCommand();
        foreach (string table in Tables)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            output.WriteLine($"INVENTORY {table}={cmd.ExecuteScalar()}");
        }
        cmd.CommandText = "SELECT SUM(length(CAST(StructureDefinitionJson AS BLOB))) FROM StructureDefinitionFacts";
        output.WriteLine($"INVENTORY factsBytes={cmd.ExecuteScalar()}");
        string input = Path.Combine(fixture.SourceRoot, "input");
        foreach (string filename in Directory.GetFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            output.WriteLine($"INPUT {Path.GetRelativePath(input, filename)} sha256={Digest(File.ReadAllText(filename).ReplaceLineEndings("\n"))}");
        }
        cmd.CommandText = "SELECT name, sql FROM sqlite_schema WHERE type='index' ORDER BY name";
        using IDataReader reader = cmd.ExecuteReader();
        List<string> indexes = [];
        while (reader.Read())
        {
            indexes.Add($"{reader[0]}:{reader[1]}");
        }
        output.WriteLine($"INDEXES count={indexes.Count} sha256={Digest(string.Join("\n", indexes))} names={string.Join(",", indexes.Select(i => i.Split(':')[0]))}");
        output.WriteLine($"HOST {Environment.MachineName} os={Environment.OSVersion} runtime={Environment.Version} processors={Environment.ProcessorCount}");
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MessageLog : ILoggerFactory, ILogger
    {
        internal List<(long Stamp, LogLevel Level, string Message)> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => categoryName == typeof(FmlLoader).FullName ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Add((Stopwatch.GetTimestamp(), logLevel, formatter(state, exception)));
            }
        }
        public void Dispose() { }
    }
}
