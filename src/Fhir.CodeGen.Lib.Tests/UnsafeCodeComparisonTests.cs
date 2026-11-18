// <copyright file="UnsafeCodeComparisonTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Lib.Loader;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class UnsafeCodeComparisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothEndpointsHonorProcessingEligibilityInAuthoredConceptMapsAndFml(bool fml)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProcessingEligibilityInputs();
        HashSet<int> passiveKeys = [.. DbStructureDefinition.SelectList(fixture.Connection, ArtifactClass: FhirArtifactClassEnum.Profile)
            .Where(owner => owner.Id != "SimpleQuantity").Select(owner => owner.Key)];
        List<(FhirPackageComparisonPair Pair, DbStructureDefinition Source, DbStructureDefinition Target, string Name)> controls = [];
        (int Source, int Target)[] neighbors = [(2, 1), (1, 0)];
        string[] nativeNames = ["Age", "Coding", "ValueSet", "Money"];
        foreach ((int sourceIndex, int targetIndex) in neighbors)
        {
            FhirPackageComparisonPair pair = new(fixture.Packages[sourceIndex], fixture.Packages[targetIndex]);
            foreach (string nativeName in nativeNames)
            {
                string profileName = nativeName == "Money" ? "MoneyQuantity" : "Passive" + nativeName;
                DbStructureDefinition nativeSource = fixture.Structure(pair.SourcePackage, nativeName);
                DbStructureDefinition nativeTarget = fixture.Structure(pair.TargetPackage, nativeName);
                DbStructureDefinition passiveSource = fixture.Structure(pair.SourcePackage, profileName);
                DbStructureDefinition passiveTarget = fixture.Structure(pair.TargetPackage, profileName);
                string controlName = "Native" + nativeName;
                controls.Add((pair, nativeSource, nativeTarget, controlName));
                fixture.WriteOwnerElementMap(pair, nativeSource, nativeTarget, controlName, fml);
                fixture.WriteOwnerElementMap(pair, passiveSource, nativeTarget, "From" + profileName, fml, annotate: !fml);
                fixture.WriteOwnerElementMap(pair, nativeSource, passiveTarget, "To" + profileName, fml, annotate: !fml);
                if (!fml)
                {
                    string category = nativeName == "ValueSet" ? "resources" : "types";
                    WriteStructureMap(fixture, pair, category,
                        [(nativeSource, nativeTarget), (passiveSource, nativeTarget), (nativeSource, passiveTarget), (passiveSource, null)]);
                }
            }
            DbStructureDefinition quantitySource = fixture.Structure(pair.SourcePackage, "SimpleQuantity");
            DbStructureDefinition quantityTarget = fixture.Structure(pair.TargetPackage, "SimpleQuantity");
            controls.Add((pair, quantitySource, quantityTarget, "SelectedQuantity"));
            fixture.WriteOwnerElementMap(pair, quantitySource, quantityTarget, "SelectedQuantity", fml);
            if (!fml)
            {
                WriteStructureMap(fixture, pair, "types", [(quantitySource, quantityTarget)]);
            }
        }

        fixture.Load().ShouldBeTrue();
        DbStructureMapping[] structures = [.. DbStructureMapping.SelectList(fixture.Connection)];
        DbElementMapping[] elements = [.. DbElementMapping.SelectList(fixture.Connection)];
        structures.ShouldAllBe(mapping => !passiveKeys.Contains(mapping.SourceStructureKey) &&
            (mapping.TargetStructureKey == null || !passiveKeys.Contains(mapping.TargetStructureKey.Value)));
        elements.ShouldAllBe(mapping => !passiveKeys.Contains(mapping.SourceStructureKey) &&
            (mapping.TargetStructureKey == null || !passiveKeys.Contains(mapping.TargetStructureKey.Value)));
        foreach ((FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition target, string name) in controls)
        {
            structures.Where(mapping => mapping.SourceStructureKey == source.Key && mapping.TargetStructureKey == target.Key)
                .ShouldHaveSingleItem();
            DbElementMapping mapping = elements.Where(mapping => mapping.SourceFhirPackageKey == pair.SourcePackageKey &&
                mapping.TargetFhirPackageKey == pair.TargetPackageKey && mapping.SourceStructureKey == source.Key &&
                mapping.TargetStructureKey == target.Key).ShouldHaveSingleItem();
            DbElement sourceRoot = DbElement.SelectSingle(fixture.Connection, StructureKey: source.Key, ResourceFieldOrder: 0)!;
            DbElement targetRoot = DbElement.SelectSingle(fixture.Connection, StructureKey: target.Key, ResourceFieldOrder: 0)!;
            mapping.SourceElementKey.ShouldBe(Element(fixture, source, sourceRoot.Id + ".code").Key);
            mapping.TargetElementKey.ShouldBe(Element(fixture, target, targetRoot.Id + ".code").Key);
            if (fml)
            {
                mapping.FmlIsSimpleCopy.ShouldBe(true);
                mapping.ConceptMapSourceKey.ShouldBeNull();
                DbMappingSourceFile.SelectSingle(fixture.Connection, Key: mapping.FmlSourceKey)!.Url
                    .ShouldBe("http://example.org/StructureMap/" + name);
            }
            else
            {
                DbMappingSourceFile.SelectSingle(fixture.Connection, Key: mapping.ConceptMapSourceKey)!.Url
                    .ShouldBe("http://example.org/ConceptMap/" + name);
            }
        }
        if (!fml)
        {
            DbProfileMapOwnerFacts[] retained = [.. DbProfileMapOwnerFacts.SelectList(fixture.Connection)];
            retained.Length.ShouldBe(16);
            retained.ShouldAllBe(owner => owner.ElementMappingKey == null &&
                (passiveKeys.Contains(owner.SourceStructureKey!.Value) || passiveKeys.Contains(owner.TargetStructureKey!.Value)));
            foreach (DbProfileMapOwnerFacts owner in retained)
            {
                DbStructureDefinition source = DbStructureDefinition.SelectSingle(fixture.Connection, Key: owner.SourceStructureKey)!;
                DbStructureDefinition target = DbStructureDefinition.SelectSingle(fixture.Connection, Key: owner.TargetStructureKey)!;
                owner.GroupSourceCanonical.ShouldBe(source.VersionedUrl);
                owner.GroupSourceVersion.ShouldBe(source.Version);
                owner.GroupTargetCanonical.ShouldBe(target.VersionedUrl);
                owner.GroupTargetVersion.ShouldBe(target.Version);
            }
            DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).Count.ShouldBe(retained.Length);
        }
    }

    [Theory]
    [InlineData("Age")]
    [InlineData("Coding")]
    [InlineData("ValueSet")]
    [InlineData("Money")]
    public void BothEndpointsAndTransitiveOwnersHonorProcessingEligibility(string nativeName)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProcessingEligibilityInputs();
        HashSet<int> passiveKeys = [.. DbStructureDefinition.SelectList(fixture.Connection, ArtifactClass: FhirArtifactClassEnum.Profile)
            .Where(owner => owner.Id != "SimpleQuantity").Select(owner => owner.Key)];
        FhirPackageComparisonPair first = new(fixture.Packages[2], fixture.Packages[1]);
        FhirPackageComparisonPair second = new(fixture.Packages[1], fixture.Packages[0]);
        FhirPackageComparisonPair full = new(fixture.Packages[2], fixture.Packages[0]);
        string profileName = nativeName == "Money" ? "MoneyQuantity" : "Passive" + nativeName;
        DbStructureDefinition passiveMiddle = fixture.Structure(first.TargetPackage, profileName);
        DbStructureDefinition direct = fixture.AddStructure(first.SourcePackage, "Direct" + nativeName);
        DbStructureDefinition inverse = fixture.AddStructure(first.TargetPackage, "Inverse" + nativeName);
        fixture.AddElement(direct, direct.Id);
        fixture.AddElement(inverse, inverse.Id);
        AddStructureMapping(fixture, first, direct, passiveMiddle);
        AddStructureMapping(fixture, first, fixture.Structure(first.SourcePackage, profileName), inverse);
        FhirPackageComparisonPair[] neighbors = [first, second];
        foreach (FhirPackageComparisonPair pair in neighbors)
        {
            AddStructureMapping(fixture, pair, fixture.Structure(pair.SourcePackage, nativeName), fixture.Structure(pair.TargetPackage, nativeName));
        }
        fixture.PublishAuthoredDefinitions();
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(fixture.Connection, DbFhirPackage.SelectList(fixture.Connection));
        fixture.CompareProfilePairs(first, second, new(first.TargetPackage, first.SourcePackage));
        DbStructureComparison.SelectList(fixture.Connection).ShouldAllBe(comparison => !passiveKeys.Contains(comparison.SourceStructureKey) &&
            (comparison.TargetStructureKey == null || !passiveKeys.Contains(comparison.TargetStructureKey.Value)));
        DbElementComparison.SelectList(fixture.Connection).ShouldAllBe(comparison => !passiveKeys.Contains(comparison.SourceStructureKey) &&
            (comparison.TargetStructureKey == null || !passiveKeys.Contains(comparison.TargetStructureKey.Value)));
        DbStructureComparison rejectedDirect = DbStructureComparison.SelectList(fixture.Connection, SourceStructureKey: direct.Key).ShouldHaveSingleItem();
        rejectedDirect.NotMapped.ShouldBeTrue();
        rejectedDirect.TargetStructureKey.ShouldBeNull();
        DbStructureComparison.SelectList(fixture.Connection, SourceStructureKey: inverse.Key,
            TargetFhirPackageKey: first.SourcePackageKey).ShouldHaveSingleItem().TargetStructureKey.ShouldBeNull();

        // Normalized authorities can predate the consumer: neither direct nor transitive traversal may promote their passive owners.
        for (int passivePosition = 0; passivePosition < 3; passivePosition++)
        {
            DbStructureDefinition[] chain = [.. fixture.Packages.AsEnumerable().Reverse().Select((package, position) =>
            {
                if (position == passivePosition)
                {
                    return fixture.Structure(package, profileName);
                }
                DbStructureDefinition owner = fixture.AddStructure(package, $"Chain{passivePosition}At{position}{nativeName}");
                fixture.AddElement(owner, owner.Id);
                return owner;
            })];
            AddAuthoredComparison(fixture, first, AddStructureMapping(fixture, first, chain[0], chain[1]));
            AddAuthoredComparison(fixture, second, AddStructureMapping(fixture, second, chain[1], chain[2]));
        }
        fixture.CompleteAuthoredArrangement(first);
        fixture.StampComparison(second);
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: [full.SequencePair]);

        DbStructureComparison[] transitive = [.. DbStructureComparison.SelectList(fixture.Connection,
            SourceFhirPackageKey: full.SourcePackageKey, TargetFhirPackageKey: full.TargetPackageKey)];
        transitive.ShouldNotBeEmpty();
        transitive.ShouldAllBe(comparison => comparison.ContentKeys.All(key => key == null || !passiveKeys.Contains(key.Value)));
        DbStructureComparison control = transitive.Single(comparison =>
            comparison.SourceStructureKey == fixture.Structure(full.SourcePackage, nativeName).Key);
        control.TargetStructureKey.ShouldBe(fixture.Structure(full.TargetPackage, nativeName).Key);
        control.ContentKeyR4B.ShouldBe(fixture.Structure(first.TargetPackage, nativeName).Key);
        control.Steps.ShouldBe(2);

        new OutcomeGenerator(fixture.Connection, NullLoggerFactory.Instance).GenerateOutcomes(processValueSets: false,
            specificPairs: [first.SequencePair, second.SequencePair, full.SequencePair]);
        DbStructureOutcome[] outcomes = [.. DbStructureOutcome.SelectList(fixture.Connection)];
        outcomes.ShouldNotBeEmpty();
        outcomes.ShouldAllBe(outcome => !passiveKeys.Contains(outcome.SourceStructureKey) &&
            (outcome.TargetStructureKey == null || !passiveKeys.Contains(outcome.TargetStructureKey.Value)));
        DbElementOutcome.SelectList(fixture.Connection).ShouldAllBe(outcome => !passiveKeys.Contains(outcome.SourceStructureKey));
        outcomes.Single(outcome => outcome.SourceStructureKey == direct.Key &&
            outcome.TargetFhirPackageKey == first.TargetPackageKey).TargetStructureKey.ShouldBeNull();
        outcomes.ShouldContain(outcome => outcome.StructureComparisonKey == control.Key &&
            outcome.TargetStructureKey == control.TargetStructureKey);
    }

    [Fact]
    public void AmbiguousCapturedFilePositionsFailBeforeAssociatingAnyElementEdge()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        using MappingLoader mappingLoader = new(fixture.Connection, NullLoggerFactory.Instance);
        mappingLoader.TryLoadCrossVersionSourceMaps(fixture.SourceRoot, false).ShouldBeTrue();
        DbMappingClasses.DropTables(fixture.Connection);
        DbMappingClasses.CreateTables(fixture.Connection);
        UnsafeCodeInputStore.BeginInputs(fixture.Connection, "source", fixture.SourceRoot);
        string inputBefore = JsonSerializer.Serialize(fixture.InputState);
        string inventoryBefore = JsonSerializer.Serialize(DefinitionInventoryStore.ReadState(fixture.Connection));
        const string filename = "ConceptMap-elements-5to4B.json";
        string relativeFilename = Path.Combine("input", "elements", filename);
        string fileBefore = File.ReadAllText(Path.Combine(fixture.SourceRoot, relativeFilename));
        ConceptMap map = fixture.ReadMap("elements", filename);
        using PackageLoader packageLoader = new(new()
        {
            OfflineMode = true, AutoLoadExpansions = false, ResolvePackageDependencies = false,
            LogFactory = NullLoggerFactory.Instance,
        }, new()
        {
            JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
        });
        ConceptMapLoader conceptMapLoader = new(mappingLoader, fixture.Connection, packageLoader,
            DbFhirPackage.SelectList(fixture.Connection), NullLoggerFactory.Instance);
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER DuplicateCapturedAnnotationOwner AFTER INSERT ON ProfileMapOwnerFacts
            BEGIN
                INSERT INTO ProfileMapOwnerFacts
                    (AnnotationKey, InputSnapshotId, SourceFhirPackageKey, TargetFhirPackageKey,
                     GroupSourceCanonical, GroupTargetCanonical, ResolutionState, SourceElementId, TargetElementId,
                     MappingSourceFileKey, SourceRelativeFilename, ConceptMapId, ConceptMapUrl, GroupIndex, SourceIndex, TargetIndex)
                VALUES
                    (NEW.AnnotationKey, NEW.InputSnapshotId, NEW.SourceFhirPackageKey, NEW.TargetFhirPackageKey,
                     NEW.GroupSourceCanonical, NEW.GroupTargetCanonical, NEW.ResolutionState, NEW.SourceElementId, NEW.TargetElementId,
                     NEW.MappingSourceFileKey, NEW.SourceRelativeFilename, NEW.ConceptMapId, NEW.ConceptMapUrl,
                     NEW.GroupIndex, NEW.SourceIndex, NEW.TargetIndex);
            END
            """;
        command.ExecuteNonQuery();

        InvalidDataException error = Should.Throw<InvalidDataException>(() =>
            conceptMapLoader.LoadSourceMaps(fixture.SourceRoot, "elements", filename));
        error.Message.ShouldContain("Ambiguous annotation association");
        DbUnsafeCodeElementAnnotation annotation = DbUnsafeCodeElementAnnotation.SelectList(fixture.Connection).ShouldHaveSingleItem();
        annotation.SourceRelativeFilename.ShouldBe(relativeFilename);
        annotation.ConceptMapId.ShouldBe(map.Id);
        annotation.ConceptMapUrl.ShouldBe(map.Url);
        annotation.GroupIndex.ShouldBe(0);
        annotation.SourceIndex.ShouldBe(0);
        annotation.TargetIndex.ShouldBe(0);
        annotation.SourceElementId.ShouldBe(map.Group[0].Element[0].Code);
        annotation.TargetElementId.ShouldBe(map.Group[0].Element[0].Target[0].Code);
        error.Message.ShouldContain(annotation.SourceRelativeFilename);
        error.Message.ShouldContain(fixture.InputState.SnapshotId);
        DbMappingSourceFile file = DbMappingSourceFile.SelectList(fixture.Connection).ShouldHaveSingleItem();
        file.SourceRelativeFilename.ShouldBe(relativeFilename);
        DbProfileMapOwnerFacts[] owners = [.. DbProfileMapOwnerFacts.SelectList(fixture.Connection)];
        owners.Length.ShouldBe(2);
        owners.ShouldAllBe(owner => owner.AnnotationKey == annotation.Key && owner.ElementMappingKey == null &&
            owner.SourceRelativeFilename == annotation.SourceRelativeFilename && owner.InputSnapshotId == fixture.InputState.SnapshotId &&
            owner.MappingSourceFileKey == file.Key && owner.ConceptMapId == annotation.ConceptMapId &&
            owner.ConceptMapUrl == annotation.ConceptMapUrl && owner.GroupIndex == annotation.GroupIndex &&
            owner.SourceIndex == annotation.SourceIndex && owner.TargetIndex == annotation.TargetIndex);
        DbElementMapping.SelectList(fixture.Connection).ShouldBeEmpty();
        fixture.InputState.CompletedInputRevision.ShouldBeNull();
        JsonSerializer.Serialize(fixture.InputState).ShouldBe(inputBefore);
        JsonSerializer.Serialize(DefinitionInventoryStore.ReadState(fixture.Connection)).ShouldBe(inventoryBefore);
        File.ReadAllText(Path.Combine(fixture.SourceRoot, relativeFilename)).ShouldBe(fileBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameConceptMapIdentityKeepsSelectedAnnotationsOnTheirOriginalFileAndPosition(bool reverseAnnotations)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs(persistDatabase: true);
        string[] filenames = ["ConceptMap-elements-5to4B.json", "ConceptMap-elements-authored-5to4B.json"];
        for (int fileIndex = 0; fileIndex < filenames.Length; fileIndex++)
        {
            ConceptMap map = fixture.ReadMap("elements", filenames[0]);
            map.Version = "authored-" + fileIndex;
            ConceptMap.TargetElementComponent target = map.Group[0].Element[0].Target[0];
            map.Group[0].Element[0].Target = [];
            for (int position = 0; position < 2; position++)
            {
                ConceptMap.TargetElementComponent copy = (ConceptMap.TargetElementComponent)target.DeepCopy();
                copy.Property[0].Value = new FhirBoolean(position == (reverseAnnotations ? 1 - fileIndex : fileIndex));
                map.Group[0].Element[0].Target.Add(copy);
            }
            File.WriteAllText(Path.Combine(fixture.SourceRoot, "input", "elements", filenames[fileIndex]), map.ToJson());
        }

        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        fixture.LoadProfileMaps();
        DbElementMapping[] mappings = [.. DbElementMapping.SelectList(fixture.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)];
        mappings.Length.ShouldBe(4);
        DbProfileMapOwnerFacts[] owners = [.. DbProfileMapOwnerFacts.SelectList(fixture.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)];
        owners.Length.ShouldBe(2);
        owners.Select(owner => owner.ConceptMapId).Distinct().ShouldHaveSingleItem();
        owners.Select(owner => owner.ConceptMapUrl).Distinct().ShouldHaveSingleItem();
        string originalOwners = JsonSerializer.Serialize(owners);
        DbElement source = DbElement.SelectSingle(fixture.Connection, Key: mappings[0].SourceElementKey)!;
        DbElement targetElement = DbElement.SelectSingle(fixture.Connection, Key: mappings[0].TargetElementKey)!;
        DbStructureOutcome outcome = fixture.AddOutcome(pair, fixture.Structure(pair.SourcePackage), fixture.Structure(pair.TargetPackage));
        fixture.Connect(pair, outcome, source, targetElement);
        DbElementComparison comparison = DbElementComparison.SelectList(fixture.Connection,
            SourceElementKey: source.Key, TargetFhirPackageKey: pair.TargetPackageKey).ShouldHaveSingleItem();

        foreach (string filename in filenames)
        {
            string relativeFilename = Path.Combine("input", "elements", filename);
            DbElementMapping[] fileMappings = [.. mappings.Where(mapping => mapping.ConceptMapFilename == filename).OrderBy(mapping => mapping.Key)];
            fileMappings.Length.ShouldBe(2);
            for (int position = 0; position < fileMappings.Length; position++)
            {
                DbElementMapping mapping = fileMappings[position];
                DbProfileMapOwnerFacts[] selected = [.. owners.Where(owner =>
                    owner.SourceRelativeFilename == relativeFilename && owner.TargetIndex == position)];
                int[] expectedKeys = [.. selected.Select(owner => owner.AnnotationKey)];
                foreach (DbProfileMapOwnerFacts owner in selected)
                {
                    owner.ElementMappingKey.ShouldBe(mapping.Key);
                    owner.MappingSourceFileKey.ShouldBe(mapping.ConceptMapSourceKey);
                    DbMappingSourceFile.SelectSingle(fixture.Connection, Key: owner.MappingSourceFileKey)!
                        .SourceRelativeFilename.ShouldBe(relativeFilename);
                    DbUnsafeCodeElementAnnotation.SelectSingle(fixture.Connection, Key: owner.AnnotationKey)!
                        .SourceRelativeFilename.ShouldBe(relativeFilename);
                    owner.InputSnapshotId.ShouldBe(fixture.InputState.SnapshotId);
                }

                comparison.ElementMappingKey = mapping.Key;
                DbElementComparison.Update(fixture.Connection, comparison);
                fixture.StampComparison(pair);
                fixture.GenerateSafety(pair);
                DbUnsafeCodeTrigger[] triggers = [.. DbUnsafeCodeTrigger.SelectList(fixture.Connection,
                    SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)];
                if (expectedKeys.Length == 0)
                {
                    triggers.ShouldBeEmpty();
                }
                else
                {
                    triggers.ShouldNotBeEmpty();
                    triggers.ShouldAllBe(trigger => JsonSerializer.Deserialize<int[]>(trigger.AnnotationKeysJson)!.SequenceEqual(expectedKeys));
                }
                fixture.ReopenPersistedDatabase();
                JsonSerializer.Serialize(DbProfileMapOwnerFacts.SelectList(fixture.Connection,
                    SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)).ShouldBe(originalOwners);
            }
        }
    }

    [Fact]
    public void EffectiveOccurrenceConstraintsOverrideBaseEvenWithIdenticalTypeNames()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateRepresentationInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair();
        Dictionary<int, DbElement> uses = [];
        Dictionary<int, int> localTypes = [];
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition resource = fixture.Structure(package);
            DbStructureDefinition basis = fixture.AddStructure(package, "ResourceBase", FhirArtifactClassEnum.Resource);
            fixture.AddElement(basis, "ResourceBase");
            DbElement baseUse = fixture.AddTypedElement(basis, "ResourceBase.measure", "Quantity");
            fixture.DeclareBaseDefinition(resource, basis);
            DbElement use = fixture.AddTypedElement(resource, "ValueSet.measure", "Quantity");
            use.BaseElementKey = baseUse.Key;
            use.BaseStructureKey = basis.Key;
            use.BasePath = baseUse.Path;
            DbElement.Update(fixture.Connection, use);
            fixture.AddTypedElement(resource, "ValueSet.measure.code", "code");
            fixture.ConfigureDefinition(resource, definition =>
                definition.Snapshot.Element.Single(element => element.Path == "ValueSet.measure.code").Fixed =
                    new Code(package.Key == pair.SourcePackageKey ? "a" : "b"));
            uses.Add(package.Key, use);
            localTypes.Add(package.Key, DbElementType.SelectList(fixture.Connection, ElementKey: use.Key).ShouldHaveSingleItem().Key);
        }
        AddStructureMapping(fixture, pair, fixture.Structure(pair.SourcePackage, "Quantity"), fixture.Structure(pair.TargetPackage, "Quantity"));
        fixture.CompleteAuthoredArrangement(pair);
        DbComparisonCache<DbElementTypeComparison> cache = new();
        ElementTypeComparer comparer = new(fixture.Connection, NullLoggerFactory.Instance, cache);
        comparer.DoTypeComparisons(pair, uses[pair.SourcePackageKey], uses[pair.TargetPackageKey], 42);
        DbElementTypeComparison comparison = cache.ToAdd.ShouldHaveSingleItem();
        comparison.SourceElementKey.ShouldBe(uses[pair.SourcePackageKey].Key);
        comparison.SourceElementTypeKey.ShouldBe(localTypes[pair.SourcePackageKey]);
        comparison.TargetElementTypeKey.ShouldBe(localTypes[pair.TargetPackageKey]);
        comparison.NotMapped.ShouldBeTrue();
        comparison.Relationship.ShouldNotBe(CMR.Equivalent);
        PersistedProfileDecision decision = ProfileUseFacts.ReadDecisions(comparison.TechnicalMessage!).ShouldHaveSingleItem();
        decision.RequiredFacts[".code"].Restrictions.ShouldContain(restriction =>
            restriction.Kind == ProfileRestrictionKind.Fixed && restriction.Value.GetString() == "a");
        decision.NativeFacts[".code"].Restrictions.ShouldContain(restriction =>
            restriction.Kind == ProfileRestrictionKind.Fixed && restriction.Value.GetString() == "b");
    }

    [Theory]
    [InlineData("http://hl7.org/fhir/5.0/ValueSet", true)]
    [InlineData("http://hl7.org/fhir/5.0/StructureDefinition/ValueSet|5.0.0", true)]
    [InlineData("http://hl7.org/fhir/4.0/ValueSet", false)]
    [InlineData("http://hl7.org/fhir/5.0/ValueSet|4.0.1", false)]
    [InlineData("http://example.org/fhir/5.0/ValueSet", false)]
    [InlineData("http://hl7.org/fhir/5.0/other/ValueSet", false)]
    public void FmlReleaseDeclarationsRequireExactNamespaceReleaseAndDefinitionVersion(string sourceUrl, bool accepted)
    {
        using UnsafeCodeTestFixture fixture = new();
        fixture.InsertFilterElements();
        string directory = Path.Combine(fixture.SourceRoot, "input", "R5toR4");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ValueSet5to4.fml"), $$"""
            /// url = "http://example.org/StructureMap/ValueSet5to4"
            /// name = "ValueSet5to4"
            uses "{{sourceUrl}}" alias ValueSetR5 as source
            uses "http://hl7.org/fhir/4.0/ValueSet" alias ValueSetR4 as target
            group ValueSet(source src : ValueSetR5, target tgt : ValueSetR4) {
              src.compose.include.filter.op -> tgt.compose.include.filter.op;
            }
            """);

        fixture.Load().ShouldBeTrue();
        DbElementMapping.SelectList(fixture.Connection).Any(mapping =>
            mapping.SourceElementId == UnsafeCodeTestFixture.FilterElement &&
            mapping.TargetFhirPackageKey == fixture.Packages[0].Key &&
            mapping.FmlSourceKey is not null && mapping.FmlIsSimpleCopy == true).ShouldBe(accepted);
    }

    [Fact]
    public void MultipleAuthoredTargetsPersistTheirOwnElementMappingKeys()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        DbStructureDefinition sourceStructure = fixture.Structure(pair.SourcePackage);
        DbStructureDefinition targetStructure = fixture.Structure(pair.TargetPackage);
        DbElement source = Element(fixture, sourceStructure, UnsafeCodeTestFixture.FilterElement);
        DbElement target = Element(fixture, targetStructure, UnsafeCodeTestFixture.FilterElement);
        DbElement renamed = fixture.AddElement(targetStructure, "ValueSet.compose.include.filter.renamed", "code", UnsafeCodeTestFixture.FilterValueSet);
        fixture.AddType(renamed, "code", fixture.Structure(pair.TargetPackage, "code"));
        renamed.MaxCardinality = -1;
        renamed.MaxCardinalityString = "*";

        DbValueSet sourceValueSet = fixture.SetBinding(pair.SourcePackage);
        DbValueSet targetValueSet = fixture.SetBinding(pair.TargetPackage);
        source.BindingValueSetKey = sourceValueSet.Key;
        target.BindingValueSetKey = targetValueSet.Key;
        renamed.BindingValueSetKey = targetValueSet.Key;
        DbElement.Update(fixture.Connection, [source, target, renamed]);
        TestValueSetComparison binding = new(pair, sourceValueSet, targetValueSet);
        binding.Key = DbValueSetComparison.Insert(fixture.Connection, binding);

        // Offset the mapping-family keys so a structure key cannot accidentally look correct.
        foreach (string type in new[] { "code", "string", "Extension" })
        {
            AddStructureMapping(fixture, pair, fixture.Structure(pair.SourcePackage, type), fixture.Structure(pair.TargetPackage, type));
        }

        DbStructureMapping structureMapping = AddStructureMapping(fixture, pair, sourceStructure, targetStructure);
        DbElementMapping[] mappings =
        [
            new TestElementMapping(pair, structureMapping, source, target),
            new TestElementMapping(pair, structureMapping, source, renamed) { Relationship = CMR.SourceIsNarrowerThanTarget },
            new TestElementMapping(pair, structureMapping, source, null),
        ];
        foreach (DbElementMapping mapping in mappings)
        {
            mapping.Key = DbElementMapping.Insert(fixture.Connection, mapping);
        }
        mappings.ShouldAllBe(mapping => mapping.Key != structureMapping.Key);

        fixture.CompleteAuthoredArrangement(pair);
        Compare(fixture, pair);
        List<DbElementComparison> comparisons = DbElementComparison.SelectList(fixture.Connection,
            SourceElementKey: source.Key, TargetFhirPackageKey: pair.TargetPackageKey);
        comparisons.Count.ShouldBe(3);
        comparisons.Select(comparison => comparison.TargetElementKey).OrderBy(key => key)
            .ShouldBe(new int?[] { null, target.Key, renamed.Key }.OrderBy(key => key));

        DbElementComparison originalTarget = comparisons.Single(comparison => comparison.TargetElementKey == target.Key);
        originalTarget.Relationship.ShouldBe(CMR.SourceIsBroaderThanTarget);
        originalTarget.SourceAllowsMoreValues.ShouldBe(true);
        originalTarget.TargetAllowsMoreValues.ShouldBe(false);
        originalTarget.IsIdentical.ShouldBe(true);
        DbElementComparison renamedTarget = comparisons.Single(comparison => comparison.TargetElementKey == renamed.Key);
        renamedTarget.Relationship.ShouldBe(CMR.SourceIsNarrowerThanTarget);
        renamedTarget.SourceAllowsMoreValues.ShouldBe(false);
        renamedTarget.TargetAllowsMoreValues.ShouldBe(false);
        renamedTarget.RelativePathsAreIdentical.ShouldBe(false);

        foreach (DbElementComparison comparison in new[] { originalTarget, renamedTarget })
        {
            comparison.NotMapped.ShouldBeFalse();
            comparison.SourceRequiresMoreValues.ShouldBe(false);
            comparison.TargetRequiresMoreValues.ShouldBe(false);
            comparison.TypeRelationship.ShouldBe(CMR.Equivalent);
            comparison.BoundValueSetComparisonKey.ShouldBe(binding.Key);
            comparison.BoundValueSetRelationship.ShouldBe(CMR.Equivalent);
            comparison.ConceptDomainRelationship.ShouldBe(CMR.Equivalent);
            comparison.ValueDomainRelationship.ShouldBe(CMR.Equivalent);
            comparison.ContentKeys[pair.SourcePackage.PackageArrayIndex].ShouldBe(source.Key);
            comparison.ContentKeys[pair.TargetPackage.PackageArrayIndex].ShouldBe(comparison.TargetElementKey);
        }

        DbElementComparison authoredNoMap = comparisons.Single(comparison => comparison.TargetElementKey is null);
        authoredNoMap.NotMapped.ShouldBeTrue();
        authoredNoMap.Relationship.ShouldBe(CMR.Equivalent);
        authoredNoMap.TypeRelationship.ShouldBeNull();
        authoredNoMap.SourceAllowsMoreValues.ShouldBeNull();

        DbStructureComparison structureComparison = DbStructureComparison.SelectSingle(fixture.Connection,
            SourceStructureKey: sourceStructure.Key, TargetStructureKey: targetStructure.Key)!;
        structureComparison.StructureMappingKey.ShouldBe(structureMapping.Key);
        foreach (DbElementMapping mapping in mappings)
        {
            DbElementComparison comparison = comparisons.Single(comparison => comparison.TargetElementKey == mapping.TargetElementKey);
            comparison.StructureComparisonKey.ShouldBe(structureComparison.Key);
            comparison.ElementMappingKey.ShouldBe(mapping.Key);
            DbElementMapping persistedMapping = DbElementMapping.SelectSingle(fixture.Connection, Key: comparison.ElementMappingKey)!;
            persistedMapping.StructureMappingKey.ShouldBe(structureMapping.Key);
            persistedMapping.SourceElementKey.ShouldBe(comparison.SourceElementKey);
            persistedMapping.TargetElementKey.ShouldBe(comparison.TargetElementKey);
        }
    }

    [Fact]
    public void InferredAndUnmatchedElementsDoNotClaimStructureMappingProvenance()
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProfileInputs();
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        DbStructureDefinition sourceStructure = fixture.Structure(pair.SourcePackage);
        DbStructureDefinition targetStructure = fixture.Structure(pair.TargetPackage);
        DbElement inferredSource = Element(fixture, sourceStructure, "ValueSet.compose.include.filter.value");
        DbElement inferredTarget = Element(fixture, targetStructure, inferredSource.Id);
        DbElement unmatched = fixture.AddElement(sourceStructure, "ValueSet.compose.include.filter.unmatched", "string");
        fixture.AddType(unmatched, "string", fixture.Structure(pair.SourcePackage, "string"));
        DbStructureMapping structureMapping = AddStructureMapping(fixture, pair, sourceStructure, targetStructure);
        AddStructureMapping(fixture, pair, fixture.Structure(pair.SourcePackage, "string"), fixture.Structure(pair.TargetPackage, "string"));
        DbElementMapping.SelectList(fixture.Connection).ShouldBeEmpty();

        fixture.CompleteAuthoredArrangement(pair);
        Compare(fixture, pair);
        DbElementComparison inferred = DbElementComparison.SelectSingle(fixture.Connection,
            SourceElementKey: inferredSource.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        inferred.TargetElementKey.ShouldBe(inferredTarget.Key);
        inferred.Relationship.ShouldBe(CMR.Equivalent);
        inferred.TypeRelationship.ShouldBe(CMR.Equivalent);
        inferred.NotMapped.ShouldBeFalse();
        inferred.IsIdentical.ShouldBe(true);
        DbElementComparison noMap = DbElementComparison.SelectSingle(fixture.Connection,
            SourceElementKey: unmatched.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        noMap.TargetElementKey.ShouldBeNull();
        noMap.Relationship.ShouldBeNull();
        noMap.NotMapped.ShouldBeTrue();
        noMap.TypeRelationship.ShouldBeNull();

        DbStructureComparison structureComparison = DbStructureComparison.SelectSingle(fixture.Connection,
            SourceStructureKey: sourceStructure.Key, TargetStructureKey: targetStructure.Key)!;
        structureComparison.StructureMappingKey.ShouldBe(structureMapping.Key);
        inferred.StructureComparisonKey.ShouldBe(structureComparison.Key);
        noMap.StructureComparisonKey.ShouldBe(structureComparison.Key);
        inferred.ElementMappingKey.ShouldBeNull();
        noMap.ElementMappingKey.ShouldBeNull();
    }

    [Fact]
    public void AbsentTargetStructureDoesNotSupplyElementMappingProvenance()
    {
        using UnsafeCodeTestFixture fixture = new(initializeIndices: true);
        FhirPackageComparisonPair pair = fixture.PreparePair(2, 1);
        DbStructureDefinition sourceStructure = fixture.AddStructure(pair.SourcePackage, "RemovedResource", FhirArtifactClassEnum.Resource);
        fixture.AddElement(sourceStructure, "RemovedResource");
        fixture.AddElement(sourceStructure, "RemovedResource.value");
        DbStructureMapping structureMapping = AddStructureMapping(fixture, pair, sourceStructure, null);

        fixture.CompleteAuthoredArrangement(pair);
        Compare(fixture, pair);
        DbStructureComparison structureComparison = DbStructureComparison.SelectSingle(fixture.Connection,
            SourceStructureKey: sourceStructure.Key, TargetFhirPackageKey: pair.TargetPackageKey)!;
        structureComparison.TargetStructureKey.ShouldBeNull();
        structureComparison.StructureMappingKey.ShouldBe(structureMapping.Key);
        List<DbElementComparison> comparisons = DbElementComparison.SelectList(fixture.Connection,
            StructureComparisonKey: structureComparison.Key);
        comparisons.Count.ShouldBe(2);
        foreach (DbElementComparison comparison in comparisons)
        {
            comparison.TargetElementKey.ShouldBeNull();
            comparison.NotMapped.ShouldBeTrue();
            comparison.Relationship.ShouldBeNull();
            comparison.SourceAllowsMoreValues.ShouldBeNull();
            comparison.ElementMappingKey.ShouldBeNull();
        }
    }

    private static DbElement Element(UnsafeCodeTestFixture fixture, DbStructureDefinition structure, string id) =>
        DbElement.SelectSingle(fixture.Connection, StructureKey: structure.Key, Id: id)!;

    private static void Compare(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair) =>
        new StructureComparer(fixture.Connection, NullLoggerFactory.Instance).CompareStructures(specificPairs: [pair.SequencePair]);

    private static void WriteStructureMap(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair, string category,
        (DbStructureDefinition Source, DbStructureDefinition? Target)[] edges)
    {
        string group = category == "resources" ? "resource-types" : "data-types";
        string direction = pair.SourcePackageShortName[1..] + "to" + pair.TargetPackageShortName[1..];
        string name = category + "-" + edges[0].Source.Id + "-" + direction;
        fixture.WriteInputMap(new ConceptMap
        {
            Id = name, Url = "http://example.org/ConceptMap/" + name, Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = "http://hl7.org/fhir/" + pair.SourceFhirVersionShort + "/" + group,
                Target = "http://hl7.org/fhir/" + pair.TargetFhirVersionShort + "/" + group,
                Element = [.. edges.Select(edge => new ConceptMap.SourceElementComponent
                {
                    Code = edge.Source.Id, NoMap = edge.Target == null ? true : null,
                    Target = edge.Target == null ? [] : [new() { Code = edge.Target.Id, Relationship = CMR.Equivalent }],
                })],
            }],
        }, category, "ConceptMap-" + name + ".json");
    }

    private static void AddAuthoredComparison(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair, DbStructureMapping mapping)
    {
        DbStructureDefinition source = DbStructureDefinition.SelectSingle(fixture.Connection, Key: mapping.SourceStructureKey)!;
        DbStructureDefinition target = DbStructureDefinition.SelectSingle(fixture.Connection, Key: mapping.TargetStructureKey)!;
        int?[] contentKeys = new int?[6];
        contentKeys[pair.SourcePackage.PackageArrayIndex] = source.Key;
        contentKeys[pair.TargetPackage.PackageArrayIndex] = target.Key;
        DbStructureComparison.Insert(fixture.Connection, new DbStructureComparison
        {
            Key = DbStructureComparison.GetIndex(),
            SourceFhirPackageKey = pair.SourcePackageKey, SourceFhirSequence = pair.SourceFhirSequence,
            TargetFhirPackageKey = pair.TargetPackageKey, TargetFhirSequence = pair.TargetFhirSequence,
            SourceStructureKey = source.Key, TargetStructureKey = target.Key, StructureMappingKey = mapping.Key,
            SourceCanonicalVersioned = source.VersionedUrl, SourceCanonicalUnversioned = source.UnversionedUrl,
            SourceId = source.Id, SourceName = source.Name, SourceVersion = source.Version,
            TargetCanonicalVersioned = target.VersionedUrl, TargetCanonicalUnversioned = target.UnversionedUrl,
            TargetId = target.Id, TargetName = target.Name, TargetVersion = target.Version,
            ContentKeys = contentKeys, Steps = 1, Relationship = CMR.Equivalent, NotMapped = false,
            ConceptDomainRelationship = CMR.Equivalent, ValueDomainRelationship = CMR.Equivalent,
            IsIdentical = true, ElementRelativePathsAreIdentical = true, IsBasedOnFallbackMapping = false,
            TechnicalMessage = "Authored normalized neighbor edge", UserMessage = null,
        }, insertPrimaryKey: true);
    }

    private static DbStructureMapping AddStructureMapping(UnsafeCodeTestFixture fixture, FhirPackageComparisonPair pair,
        DbStructureDefinition source, DbStructureDefinition? target)
    {
        TestStructureMapping mapping = new(pair, source, target);
        mapping.Key = DbStructureMapping.Insert(fixture.Connection, mapping);
        return mapping;
    }

    private sealed class TestStructureMapping : DbStructureMapping
    {
        [SetsRequiredMembers]
        internal TestStructureMapping(FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition? target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirPackageKey = pair.TargetPackageKey;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceStructureKey = source.Key;
            SourceStructureId = source.Id;
            SourceStructureUrl = source.VersionedUrl;
            TargetStructureKey = target?.Key;
            TargetStructureId = target?.Id;
            TargetStructureUrl = target?.VersionedUrl;
            ExplicitNoMap = target is null;
            Relationship = target is null ? null : CMR.Equivalent;
        }
    }

    private sealed class TestElementMapping : DbElementMapping
    {
        [SetsRequiredMembers]
        internal TestElementMapping(FhirPackageComparisonPair pair, DbStructureMapping structureMapping, DbElement source, DbElement? target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirPackageKey = pair.TargetPackageKey;
            TargetFhirSequence = pair.TargetFhirSequence;
            StructureMappingKey = structureMapping.Key;
            SourceStructureKey = source.StructureKey;
            TargetStructureKey = structureMapping.TargetStructureKey;
            SourceElementKey = source.Key;
            SourceElementId = source.Id;
            TargetElementKey = target?.Key;
            TargetElementId = target?.Id;
            ExplicitNoMap = target is null;
            Relationship = target is null ? null : CMR.Equivalent;
        }
    }

    private sealed class TestValueSetComparison : DbValueSetComparison
    {
        [SetsRequiredMembers]
        internal TestValueSetComparison(FhirPackageComparisonPair pair, DbValueSet source, DbValueSet target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirPackageKey = pair.TargetPackageKey;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceValueSetKey = source.Key;
            TargetValueSetKey = target.Key;
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
            Relationship = CMR.Equivalent;
        }
    }
}
