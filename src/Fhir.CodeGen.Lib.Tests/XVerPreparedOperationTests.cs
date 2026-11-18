// <copyright file="XVerPreparedOperationTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
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
    public void CharacterizedPipelineProducesStableSemantics()
    {
        RunSyntheticPipeline();
    }

    [Fact]
    public void IdenticalWorkloadsReportStageCosts()
    {
        RunSyntheticPipeline();
    }

    private void RunSyntheticPipeline()
    {
        using WorkloadDirectory directory = new();
        List<StageCost> costs = [];
        ComparisonDatabase? database = null;
        Stopwatch total = Stopwatch.StartNew();
        string stage = "synthetic definitions/import";
        try
        {
            Measure(stage, () =>
            {
                DefinitionCollection[] definitions = [.. _sequences.Select(SyntheticDefinitions)];
                database = new(definitions, directory.Root, "workload.sqlite", NullLoggerFactory.Instance);
                database.TryLoadFromDefinitionCollections(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes)
                    .ShouldBeTrue();
            });
            IDbConnection connection = database!.DbConnection;
            XVerStructureCatalog catalog = new(connection, XVerProcessor._exclusionSet);
            List<DbFhirPackage> packages = DbFhirPackage.SelectList(connection,
                orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);
            packages.Select(package => package.PackageVersion).ShouldBe(["1.0.2", "3.0.2", "4.0.1", "4.3.0", "5.0.0"]);
            foreach (DbFhirPackage package in packages)
            {
                catalog.GetDefinitions(package.Key).Count(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
                    .ShouldBe(4);
                catalog.GetProcessedStructures(package.Key).Count(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
                    .ShouldBe(2);
                catalog.ResolveRoot(catalog.ResolveDefinition(package.Key, StructureUrl + "SimpleQuantity")!).Id
                    .ShouldBe(package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.STU3
                        ? "Quantity:simplequantity" : "Quantity");
            }
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
            stage = "semantic characterization";
            output.WriteLine($"W1 rows: definitions={DbStructureDefinition.SelectList(connection).Count}, " +
                $"facts={DbStructureDefinitionFacts.SelectList(connection).Count}, elements={DbElement.SelectList(connection).Count}, " +
                $"types={DbElementType.SelectList(connection).Count}, negatives={negatives.Length}");
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
        }

        void Measure(string name, Action action)
        {
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
                process.Refresh();
                costs.Add(new(name, elapsed.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
                    entryPrivate, process.PrivateMemorySize64, entryManaged, GC.GetTotalMemory(false),
                    process.PeakWorkingSet64, new FileInfo(Path.Combine(directory.Root, "workload.sqlite")) is { Exists: true } file
                        ? file.Length : 0));
            }
        }
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

    private sealed record StageCost(string Stage, double Milliseconds, long AllocatedBytes,
        long EntryPrivateBytes, long ExitPrivateBytes, long EntryManagedBytes, long ExitManagedBytes,
        long ProcessPeakWorkingSetBytes, long DatabaseBytes);

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

        public void Dispose()
        {
            if (Completed)
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
