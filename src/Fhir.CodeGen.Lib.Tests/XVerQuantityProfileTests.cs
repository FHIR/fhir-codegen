// <copyright file="XVerQuantityProfileTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
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
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Hl7.FhirPath.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;
using ConceptMap = Hl7.Fhir.Model.ConceptMap;
using ElementDefinition = Hl7.Fhir.Model.ElementDefinition;
using StructureDefinition = Hl7.Fhir.Model.StructureDefinition;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class XVerQuantityProfileTests(ITestOutputHelper output)
{
    private static readonly string[] _cores =
    [
        "hl7.fhir.r2.core#1.0.2", "hl7.fhir.r3.core#3.0.2", "hl7.fhir.r4.core#4.0.1",
        "hl7.fhir.r4b.core#4.3.0", "hl7.fhir.r5.core#5.0.0",
    ];

    [Theory]
    [InlineData("hl7.fhir.r2.core#1.0.2")]
    [InlineData("hl7.fhir.r3.core#3.0.2")]
    [InlineData("hl7.fhir.r4.core#4.0.1")]
    [InlineData("hl7.fhir.r4b.core#4.3.0")]
    [InlineData("hl7.fhir.r5.core#5.0.0")]
    public async Task NormalizedQuantityInventoryFollowsCollectionClassification(string moniker)
    {
        DefinitionCollection definitions = await LoadCore(moniker);
        using CoreCatalog imported = new([definitions]);
        DbFhirPackage package = DbFhirPackage.SelectList(imported.Connection).ShouldHaveSingleItem();
        XVerStructureCatalog catalog = imported.Catalog;
        StructureDefinition[] profiles = [.. definitions.ProfilesByUrl.Values.Where(profile => profile.Type == "Quantity")];
        catalog.GetDefinitions(package.Key).Where(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
            .Select(owner => (owner.UnversionedUrl, owner.Version)).Order()
            .ShouldBe(definitions.ProfilesByUrl.Values.Select(profile => (profile.Url, profile.Version)).Order());
        DbStructureDefinition[] retained = [.. catalog.GetDefinitions(package.Key).Where(owner =>
            owner.ArtifactClass == FhirArtifactClassEnum.Profile && catalog.GetDeclaredType(owner) == "Quantity")];
        retained.Select(owner => owner.UnversionedUrl).Order().ShouldBe(profiles.Select(profile => profile.Url).Order());
        catalog.GetProcessedStructures(package.Key).Where(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
            .Select(owner => owner.UnversionedUrl).Order().ShouldBe(profiles
                .Where(profile => !XVerProcessor._exclusionSet.Contains(profile.Url)).Select(profile => profile.Url).Order());
        foreach (StructureDefinition profile in profiles)
        {
            DbStructureDefinition owner = catalog.ResolveDefinition(package.Key, profile.Url + "|" + profile.Version)!;
            owner.ArtifactClass.ShouldBe(FhirArtifactClassEnum.Profile);
            catalog.IsProcessed(owner).ShouldBeTrue();
            catalog.GetOwnedElements(owner).Count.ShouldBe(profile.Snapshot.Element.Count);
            catalog.ReadPayload(owner).ToJson().ShouldBe(profile.ToJson());
            catalog.ResolveRoot(owner).Id.ShouldBe(profile.Snapshot.Element[0].ElementId);
        }
        List<DbElementType> types = DbElementType.SelectList(imported.Connection, FhirPackageKey: package.Key);
        HashSet<int> profileKeys = [.. retained.Select(owner => owner.Key)];
        foreach (DbElementType use in types.Where(type => type.TypeProfile is not null &&
            profiles.Any(profile => UnsafeCodeCanonical.Parse(type.TypeProfile).Url == profile.Url)))
        {
            use.TypeProfileStructureKey.ShouldNotBeNull();
            profileKeys.ShouldContain(use.TypeProfileStructureKey!.Value);
            catalog.ResolveTypeUse(use).Key.ShouldBe(use.TypeProfileStructureKey.Value);
            use.TypeStructureKey.ShouldBe(catalog.ResolveNativeType(package.Key, use.TypeName)!.Key);
        }
        if (definitions.FhirSequence == FhirReleases.FhirSequenceCodes.DSTU2)
        {
            foreach (string id in new[] { "Age", "Count", "Distance", "Duration", "Money", "SimpleQuantity" })
            {
                definitions.ComplexTypesByName.ContainsKey(id).ShouldBeFalse();
                profiles.ShouldContain(profile => profile.Id == id);
            }
        }
        DbStructureDefinition[] native = UsedNativeFamily(catalog, package.Key, types);
        output.WriteLine($"{moniker}: normalized Quantity profiles ({profiles.Length}) = {string.Join(", ", profiles.Select(profile => profile.Id).Order())}");
        output.WriteLine($"  actual used native Quantity-family definitions ({native.Length}) = {string.Join(", ", native.Select(owner => owner.Id).Order())}");
        DbStructureDefinition? money = catalog.ResolveNativeType(package.Key, "Money", required: false);
        if (money is not null && catalog.ReadPayload(money).BaseDefinition != QuantityUrl)
        {
            native.ShouldNotContain(owner => owner.Key == money.Key);
        }
    }

    [Theory]
    [InlineData("hl7.fhir.r2.core#1.0.2")]
    [InlineData("hl7.fhir.r3.core#3.0.2")]
    [InlineData("hl7.fhir.r4.core#4.0.1")]
    [InlineData("hl7.fhir.r4b.core#4.3.0")]
    [InlineData("hl7.fhir.r5.core#5.0.0")]
    public async Task FreshCoreImportResolvesBothRangeBoundsToSimpleQuantity(string moniker)
    {
        DefinitionCollection definitions = await LoadCore(moniker);
        using CoreCatalog imported = new([definitions]);
        DbFhirPackage package = DbFhirPackage.SelectList(imported.Connection).ShouldHaveSingleItem();
        XVerStructureCatalog catalog = imported.Catalog;
        StructureDefinition normalized = definitions.ProfilesByUrl["http://hl7.org/fhir/StructureDefinition/SimpleQuantity"];
        DbStructureDefinition profile = catalog.ResolveDefinition(package.Key, normalized.Url + "|" + normalized.Version)!;
        DbStructureDefinition quantity = catalog.ResolveNativeType(package.Key, "Quantity")!;
        DbStructureDefinition range = catalog.ResolveNativeType(package.Key, "Range")!;
        profile.ArtifactClass.ShouldBe(FhirArtifactClassEnum.Profile);
        profile.FhirPackageKey.ShouldBe(package.Key);
        profile.Version.ShouldBe(normalized.Version);
        catalog.GetDeclaredType(profile).ShouldBe("Quantity");
        catalog.IsProcessed(profile).ShouldBeTrue();
        catalog.GetAncestors(profile).ShouldContain(owner => owner.Key == quantity.Key);
        DbElement root = catalog.ResolveRoot(profile);
        root.Id.ShouldBe(normalized.Snapshot.Element[0].ElementId);
        root.Path.ShouldBe("Quantity");
        root.ParentElementKey.ShouldBeNull();
        root.ResourceFieldOrder.ShouldBe(0);
        root.StructureKey.ShouldBe(profile.Key);
        root.FhirPackageKey.ShouldBe(package.Key);
        if (definitions.FhirSequence == FhirReleases.FhirSequenceCodes.STU3)
        {
            root.Id.ShouldBe("Quantity:simplequantity");
        }
        foreach (string bound in new[] { "low", "high" })
        {
            DbElement element = catalog.ResolveOwnedElement(range, "Range." + bound)!;
            DbElementType type = DbElementType.SelectList(imported.Connection, FhirPackageKey: package.Key,
                StructureKey: range.Key, ElementKey: element.Key).ShouldHaveSingleItem();
            type.TypeName.ShouldBe("Quantity");
            type.TypeProfile.ShouldBe(normalized.Url);
            type.TypeStructureKey.ShouldBe(quantity.Key);
            type.TypeProfileStructureKey.ShouldBe(profile.Key);
            type.TargetProfileStructureKey.ShouldBeNull();
            catalog.ResolveTypeUse(type).Key.ShouldBe(profile.Key);
        }
        DbDefinitionInventoryState state = DefinitionInventoryStore.ReadState(imported.Connection)!;
        state.CompletedRevision.ShouldNotBeNullOrEmpty();
        state.CompletedRevision.ShouldBe(state.StartedRevision);
    }

    [Fact]
    public async Task CoreQuantityUsesHaveExplicitRepresentationDecisions()
    {
        List<DefinitionCollection> definitions = [];
        foreach (string core in _cores)
        {
            definitions.Add(await LoadCore(core));
        }
        using CoreCatalog imported = new(definitions);
        XVerStructureCatalog catalog = imported.Catalog;
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(imported.Connection);
        List<DbElementType> allTypes = DbElementType.SelectList(imported.Connection);
        List<string> failures = [];
        Dictionary<ProfileReasonCategory, int> negativeReasons = [];
        HashSet<(int, int)> pairs = [];
        int proofCases = 0;
        int nativeProofs = 0;
        int ownerProofs = 0;
        int negativeDecisions = 0;
        int rangeProofs = 0;
        foreach (DbFhirPackage sourcePackage in packages)
        {
            HashSet<int> operational = [.. catalog.GetProcessedStructures(sourcePackage.Key).Select(owner => owner.Key)];
            DbStructureDefinition[] profiles = [.. catalog.GetDefinitions(sourcePackage.Key).Where(owner =>
                owner.ArtifactClass == FhirArtifactClassEnum.Profile && catalog.GetDeclaredType(owner) == "Quantity")];
            DbStructureDefinition[] natives = UsedNativeFamily(catalog, sourcePackage.Key, allTypes);
            HashSet<int> requiredOwners = [.. profiles.Concat(natives).Select(owner => owner.Key)];
            DbElementType[] uses = [.. allTypes.Where(type => type.FhirPackageKey == sourcePackage.Key &&
                operational.Contains(type.StructureKey) &&
                requiredOwners.Contains(type.TypeProfileStructureKey ?? type.TypeStructureKey ?? -1))];
            output.WriteLine($"{sourcePackage.PackageId}@{sourcePackage.PackageVersion}: required profiles={profiles.Length}, used native family={natives.Length}, relational uses={uses.Length}");
            foreach (DbFhirPackage targetPackage in packages.Where(package => package.Key != sourcePackage.Key))
            {
                pairs.Add((sourcePackage.Key, targetPackage.Key));
                using ProfileUseFacts facts = new(catalog, sourcePackage.Key, targetPackage.Key);
                int firstFailure = failures.Count;
                int pairCases = 0;
                foreach (DbStructureDefinition owner in profiles.Concat(natives))
                {
                    Check(facts.ForDefinition(owner), owner.UnversionedUrl + "|" + owner.Version + " definition", null);
                }
                // Tokens come from normalized declarations. Cross-product relational rows only identify the actual uses.
                foreach (IGrouping<(int, int), DbElementType> group in uses.GroupBy(type => (type.StructureKey, type.ElementKey)))
                {
                    DbStructureDefinition useOwner = catalog.GetDefinitions(sourcePackage.Key).Single(owner => owner.Key == group.Key.Item1);
                    DbElement element = catalog.GetOwnedElements(useOwner).Single(row => row.Key == group.Key.Item2);
                    for (int token = 0; token < catalog.ReadElement(element).Type.Count; token++)
                    {
                        EffectiveProfileUse use = facts.AtUse(useOwner, element, token);
                        failures.AddRange(use.Diagnostics);
                        foreach (EffectiveProfileBranch branch in use.Branches.Where(branch => requiredOwners.Contains(branch.Definition.Key)))
                        {
                            Check(branch, useOwner.VersionedUrl + "#" + element.Id + $" type-use {token}", element);
                        }
                    }
                }
                output.WriteLine($"  pair {sourcePackage.Key}->{targetPackage.Key} ({sourcePackage.Name}->{targetPackage.Name}): cases={pairCases}, failures={failures.Count - firstFailure}");
                foreach (string failure in failures.Skip(firstFailure).Take(1))
                {
                    output.WriteLine("    " + failure);
                }

                void Check(EffectiveProfileBranch branch, string context, DbElement? occurrence)
                {
                    proofCases++;
                    pairCases++;
                    DbStructureDefinition target = catalog.ResolveDefinition(targetPackage.Key, branch.Definition.UnversionedUrl,
                        required: false, context: context) ?? catalog.ResolveNativeType(targetPackage.Key, "Quantity")!;
                    ProfileRepresentationProof proof = facts.Prove(branch, target);
                    if (!proof.PreservesRestrictions)
                    {
                        // Try a real corresponding owner occurrence, never a fabricated datatype profile or URL.
                        DbStructureDefinition? targetOwner = occurrence is null ? null :
                            catalog.ResolveDefinition(targetPackage.Key, catalog.GetOwner(occurrence).UnversionedUrl, required: false);
                        DbElement? targetUse = targetOwner is null ? null :
                            catalog.ResolveOwnedElement(targetOwner, occurrence!.Id, required: false);
                        if (targetOwner is not null && targetUse is not null && targetUse.ResourceFieldOrder != 0)
                        {
                            EffectiveProfileUse targetFacts = facts.AtUse(targetOwner, targetUse, 0);
                            failures.AddRange(targetFacts.Diagnostics);
                            EffectiveProfileBranch[] matching = [.. targetFacts.Branches.Where(candidate => candidate.Definition.Key == target.Key)];
                            if (matching.Length == 1)
                            {
                                proof = facts.Prove(branch, target, nativeContext: matching[0]);
                            }
                        }
                    }
                    string fullContext = $"{sourcePackage.Name}->{targetPackage.Name} pair {sourcePackage.Key}->{targetPackage.Key}, {context}, alternative {branch.Key.ProfileAlternative}/{branch.Key.TargetProfileAlternative}, canonical {branch.Definition.UnversionedUrl}|{branch.Definition.Version}, source root {branch.Root.Id}, result {proof.Kind}: {string.Join("; ", proof.Diagnostics)}";
                    proof.Input.ShouldBe(branch.Key, fullContext);
                    proof.SourceRoot.Canonical.ShouldBe(branch.Definition.UnversionedUrl, fullContext);
                    proof.SourceRoot.Version.ShouldBe(branch.Definition.Version, fullContext);
                    proof.SourceRoot.ElementId.ShouldBe(branch.Root.Id, fullContext);
                    proof.TargetPackageKey.ShouldBe(targetPackage.Key, fullContext);
                    proof.RequiredFacts.ShouldBeSameAs(branch.Elements, fullContext);
                    proof.NativeFacts.ShouldNotBeEmpty(fullContext);
                    proof.TargetRoot.ShouldNotBeNull(fullContext);
                    proof.TargetRoot.OwnerKey.ShouldBe(target.Key, fullContext);
                    proof.TargetRoot.Canonical.ShouldBe(target.UnversionedUrl, fullContext);
                    proof.TargetRoot.Version.ShouldBe(target.Version, fullContext);
                    foreach (EffectiveProfileElement required in proof.RequiredFacts.Values)
                    {
                        required.MinimumKnown.ShouldBeTrue(fullContext);
                        required.MaximumKnown.ShouldBeTrue(fullContext);
                        required.Origins.ShouldNotBeEmpty(fullContext);
                    }
                    foreach (IReadOnlyDictionary<string, EffectiveProfileElement> graph in new[] { proof.RequiredFacts, proof.NativeFacts })
                    {
                        foreach ((string id, EffectiveProfileElement element) in graph)
                        {
                            if (id != "" && !graph.Any(pair => pair.Value.Prohibited &&
                                (id == pair.Key || id.StartsWith(pair.Key + ".", StringComparison.Ordinal))))
                            {
                                element.Types.ShouldNotBeEmpty(fullContext + " missing effective child datatype at " + id);
                            }
                        }
                    }
                    if (proof.Kind == ProfileProofKind.NativeReuse)
                    {
                        nativeProofs++;
                        proof.PreservesRestrictions.ShouldBeTrue(fullContext);
                        proof.TargetCanonical.ShouldBe(target.UnversionedUrl + "|" + target.Version, fullContext);
                        if (sourcePackage.PackageVersion == "5.0.0" && targetPackage.PackageVersion == "4.3.0" &&
                            occurrence?.Id is "Range.low" or "Range.high")
                        {
                            AssertRangeProof(proof, branch, target, catalog);
                            rangeProofs++;
                        }
                    }
                    else if (proof.Kind == ProfileProofKind.OwnerProjection)
                    {
                        ownerProofs++;
                    }
                    else if (proof.Kind == ProfileProofKind.Unrepresentable)
                    {
                        negativeDecisions++;
                        proof.IsAcceptedDecision.ShouldBeTrue(fullContext);
                        proof.PreservesRestrictions.ShouldBeFalse(fullContext);
                        proof.TargetCanonical.ShouldBeNull(fullContext);
                        proof.Projection.ShouldBeEmpty(fullContext);
                        proof.ProjectionOwner.ShouldBeNull(fullContext);
                        proof.Reasons.ShouldNotBeEmpty(fullContext);
                        foreach (ProfileDecisionReason reason in proof.Reasons)
                        {
                            reason.Category.ShouldNotBe(ProfileReasonCategory.UnclassifiedRestriction, fullContext);
                            reason.Category.ShouldNotBe(ProfileReasonCategory.InvalidFacts, fullContext);
                            reason.Origin.Canonical.ShouldNotBeNullOrEmpty(fullContext);
                            reason.Origin.Version.ShouldNotBeNullOrEmpty(fullContext);
                            reason.Detail.ShouldNotBeNullOrEmpty(fullContext);
                            if (reason.Category == ProfileReasonCategory.MissingTargetField)
                            {
                                proof.RequiredFacts.ContainsKey(reason.RelativeId).ShouldBeTrue(fullContext);
                                proof.NativeFacts.ContainsKey(reason.RelativeId).ShouldBeFalse(fullContext);
                            }
                            negativeReasons[reason.Category] = negativeReasons.GetValueOrDefault(reason.Category) + 1;
                        }
                    }
                    else
                    {
                        proof.IsAcceptedDecision.ShouldBeFalse(fullContext);
                        failures.Add(fullContext);
                    }
                }
            }
        }
        output.WriteLine($"Core representation census: cases={proofCases}, native proofs={nativeProofs}, owner projections={ownerProofs}, explicit negatives={negativeDecisions}, unresolved={failures.Count}, Range R5->R4B native proofs={rangeProofs}.");
        foreach ((ProfileReasonCategory kind, int count) in negativeReasons.OrderBy(pair => pair.Key))
        {
            output.WriteLine($"  negative reason ({count} facts): {kind}");
        }
        foreach (string failure in failures.Take(20))
        {
            output.WriteLine(failure);
        }
        pairs.Count.ShouldBe(20);
        proofCases.ShouldBe(3604);
        failures.Count.ShouldBe(0, $"Required core representation decision unresolved. First failure: {failures.FirstOrDefault()}");
        rangeProofs.ShouldBe(2);
        nativeProofs.ShouldBe(284);
        ownerProofs.ShouldBe(0);
        negativeDecisions.ShouldBe(3320);
        (nativeProofs + ownerProofs + negativeDecisions).ShouldBe(proofCases);
    }

    private static void AssertRangeProof(ProfileRepresentationProof proof, EffectiveProfileBranch branch,
        DbStructureDefinition target, XVerStructureCatalog catalog)
    {
        branch.Definition.UnversionedUrl.ShouldBe("http://hl7.org/fhir/StructureDefinition/SimpleQuantity");
        target.UnversionedUrl.ShouldBe(branch.Definition.UnversionedUrl);
        target.Version.ShouldBe("4.3.0");
        proof.TargetCanonical.ShouldBe(target.UnversionedUrl + "|4.3.0");
        proof.RequiredFacts[".comparator"].Maximum.ShouldBe(0);
        proof.NativeFacts[".comparator"].Maximum.ShouldBe(0);
        proof.RequiredFacts[""].Invariants.ShouldContain(invariant => invariant.Expression == "comparator.empty()");
        proof.NativeFacts[""].Invariants.ShouldContain(invariant => invariant.Expression == "comparator.empty()");
        catalog.ReadPayload(target).Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max.ShouldBe("0");
        proof.Projection.ShouldBeEmpty();
        proof.Reasons.ShouldBeEmpty();
    }

    private const string QuantityUrl = "http://hl7.org/fhir/StructureDefinition/Quantity";

    [Fact]
    public async Task R5RangeBoundsFaithfullyReuseExactR4BSimpleQuantity()
    {
        using CoreCatalog imported = new([await LoadCore(_cores[4]), await LoadCore(_cores[3])]);
        XVerStructureCatalog catalog = imported.Catalog;
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(imported.Connection);
        DbFhirPackage sourcePackage = packages.Single(package => package.PackageVersion == "5.0.0");
        DbFhirPackage targetPackage = packages.Single(package => package.PackageVersion == "4.3.0");
        using ProfileUseFacts facts = new(catalog, sourcePackage.Key, targetPackage.Key);
        DbStructureDefinition sourceRange = catalog.ResolveNativeType(sourcePackage.Key, "Range")!;
        DbStructureDefinition targetRange = catalog.ResolveNativeType(targetPackage.Key, "Range")!;
        DbStructureDefinition target = catalog.ResolveDefinition(targetPackage.Key,
            "http://hl7.org/fhir/StructureDefinition/SimpleQuantity|4.3.0")!;
        foreach (string bound in new[] { "low", "high" })
        {
            foreach (DbStructureDefinition range in new[] { sourceRange, targetRange })
            {
                DbElement element = catalog.ResolveOwnedElement(range, "Range." + bound)!;
                DbElementType type = catalog.GetTypeUses(element).ShouldHaveSingleItem();
                DbStructureDefinition linked = catalog.ResolveTypeUse(type);
                type.TypeProfileStructureKey.ShouldBe(linked.Key);
                linked.FhirPackageKey.ShouldBe(range.FhirPackageKey);
                linked.UnversionedUrl.ShouldBe(target.UnversionedUrl);
                linked.Version.ShouldBe(range.Key == sourceRange.Key ? "5.0.0" : "4.3.0");
            }
            EffectiveProfileBranch source = facts.AtUse(sourceRange,
                catalog.ResolveOwnedElement(sourceRange, "Range." + bound)!, 0).Branches.ShouldHaveSingleItem();
            EffectiveProfileBranch native = facts.AtUse(targetRange,
                catalog.ResolveOwnedElement(targetRange, "Range." + bound)!, 0).Branches.ShouldHaveSingleItem();
            ProfileRepresentationProof proof = facts.Prove(source, target, nativeContext: native);
            proof.Kind.ShouldBe(ProfileProofKind.NativeReuse, string.Join("; ", proof.Diagnostics));
            proof.Input.OwnerKey.ShouldBe(sourceRange.Key);
            proof.Input.ProfileAlternative.ShouldBe(target.UnversionedUrl);
            proof.NativeFacts.ShouldBeSameAs(native.Elements);
            proof.RequiredFacts.ShouldBeSameAs(source.Elements);
            proof.PreservesRestrictions.ShouldBeTrue();
            AssertRangeProof(proof, source, target, catalog);
            foreach (string id in new[] { ".id", ".extension", ".value", ".unit", ".system", ".code" })
            {
                proof.RequiredFacts[id].Minimum.ShouldBe(proof.NativeFacts[id].Minimum);
                proof.RequiredFacts[id].Maximum.ShouldBe(proof.NativeFacts[id].Maximum);
                proof.RequiredFacts[id].Types.Select(type => type.Code).ShouldBe(proof.NativeFacts[id].Types.Select(type => type.Code));
            }
            ProfileInvariant rule = proof.NativeFacts[""].Invariants.First(invariant => invariant.Expression == "comparator.empty()");
            FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
            Hl7.Fhir.Model.Quantity valid = new() { Value = 1 };
            compiler.Compile(rule.Expression!)(valid.ToTypedElement(), new EvaluationContext()).ShouldHaveSingleItem().Value.ShouldBe(true);
            valid.Comparator = Hl7.Fhir.Model.Quantity.QuantityComparator.LessThan;
            compiler.Compile(rule.Expression!)(valid.ToTypedElement(), new EvaluationContext()).ShouldHaveSingleItem().Value.ShouldBe(false);
            output.WriteLine($"R5 Range.{bound} -> {proof.TargetCanonical}: NativeReuse; comparator max=0 and native sqty-1 retained.");
        }
    }

    [Fact]
    public async Task Dstu2AgeZeroWitnessDoesNotSatisfyR4NativeAge()
    {
        DefinitionCollection sourceDefinitions = await LoadCore(_cores[0]);
        DefinitionCollection targetDefinitions = await LoadCore(_cores[2]);
        using CoreCatalog imported = new([sourceDefinitions, targetDefinitions]);
        XVerStructureCatalog catalog = imported.Catalog;
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(imported.Connection);
        DbFhirPackage sourcePackage = packages.Single(package => package.PackageVersion == "1.0.2");
        DbFhirPackage targetPackage = packages.Single(package => package.PackageVersion == "4.0.1");
        using ProfileUseFacts facts = new(catalog, sourcePackage.Key, targetPackage.Key);
        DbStructureDefinition source = catalog.ResolveDefinition(sourcePackage.Key, "http://hl7.org/fhir/StructureDefinition/Age|1.0.2")!;
        DbStructureDefinition target = catalog.ResolveDefinition(targetPackage.Key, "http://hl7.org/fhir/StructureDefinition/Age|4.0.1")!;
        EffectiveProfileBranch sourceFacts = facts.ForDefinition(source);
        EffectiveProfileBranch targetFacts = facts.ForDefinition(target);
        ProfileInvariant sourceInvariant = sourceFacts.Elements[""].Invariants.Single(invariant => invariant.Key == "age-1");
        ProfileInvariant targetInvariant = targetFacts.Elements[""].Invariants.First(invariant => invariant.Key == "age-1");
        AssertAgeZeroWitness(sourceInvariant, targetInvariant);
        ProfileRepresentationProof native = facts.Prove(sourceFacts, target);
        native.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        native.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.InvariantTransformation);
        native.RequiredFacts.ShouldBeSameAs(sourceFacts.Elements);
        native.NativeFacts[""].Invariants.ShouldContain(invariant => invariant.Key == "age-1");
        native.TargetCanonical.ShouldBeNull();
        native.Projection.ShouldBeEmpty();
        native.PreservesRestrictions.ShouldBeFalse();
        native.Diagnostics.ShouldContain(message => message.Contains("target restriction not entailed") && message.Contains("age-1"));
        ProfileRepresentationProof quantity = facts.Prove(sourceFacts, catalog.ResolveNativeType(targetPackage.Key, "Quantity")!);
        quantity.PreservesRestrictions.ShouldBeFalse();
        output.WriteLine($"Required unrepresentable native reuse: {source.VersionedUrl}#{sourceFacts.Root.Id} -> {target.VersionedUrl}#{targetFacts.Root.Id}; source permits value=0/code=a, target age-1 rejects it. A Quantity fallback also needs retained source restrictions and cannot produce a standalone datatype profile.");
    }

    [Fact]
    public async Task R5ComparatorAdditionCannotWidenR4RequiredBinding()
    {
        DefinitionCollection sourceDefinitions = await LoadCore(_cores[4]);
        DefinitionCollection targetDefinitions = await LoadCore(_cores[2]);
        using CoreCatalog imported = new([sourceDefinitions, targetDefinitions]);
        XVerStructureCatalog catalog = imported.Catalog;
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(imported.Connection);
        DbFhirPackage sourcePackage = packages.Single(package => package.PackageVersion == "5.0.0");
        DbFhirPackage targetPackage = packages.Single(package => package.PackageVersion == "4.0.1");
        const string valueSetUrl = "http://hl7.org/fhir/ValueSet/quantity-comparator";
        DbValueSet targetDomain = AssertComparatorDomains(imported.Connection, sourcePackage.Key, targetPackage.Key);
        using ProfileUseFacts facts = new(catalog, sourcePackage.Key, targetPackage.Key);
        DbStructureDefinition source = catalog.ResolveNativeType(sourcePackage.Key, "Quantity")!;
        DbStructureDefinition target = catalog.ResolveNativeType(targetPackage.Key, "Quantity")!;
        EffectiveProfileBranch sourceFacts = facts.ForDefinition(source);
        sourceFacts.Elements[".comparator"].Restrictions.ShouldContain(restriction =>
            restriction.Kind == ProfileRestrictionKind.Binding && ProfileUseFacts.Text(restriction.Value, "strength") == "required" &&
            ProfileUseFacts.Text(restriction.Value, "valueSet") == valueSetUrl + "|5.0.0");
        foreach (DbStructureDefinition candidate in catalog.GetDefinitions(targetPackage.Key).Where(owner =>
            owner.ArtifactClass == FhirArtifactClassEnum.ComplexType ||
            owner.ArtifactClass == FhirArtifactClassEnum.Profile && catalog.GetDeclaredType(owner) == "Quantity")
            .Where(owner => owner.Key == target.Key || catalog.GetAncestors(owner).Any(ancestor => ancestor.Key == target.Key)))
        {
            EffectiveProfileElement comparator = facts.ForDefinition(candidate).Elements[".comparator"];
            (comparator.Prohibited || comparator.Restrictions.Any(restriction =>
                restriction.Kind == ProfileRestrictionKind.Binding && ProfileUseFacts.Text(restriction.Value, "strength") == "required" &&
                ProfileUseFacts.Text(restriction.Value, "valueSet") == valueSetUrl + "|4.0.1")).ShouldBeTrue(candidate.VersionedUrl);
        }
        ProfileRepresentationProof proof = facts.Prove(sourceFacts, target);
        proof.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.Binding);
        proof.RequiredFacts.ShouldBeSameAs(sourceFacts.Elements);
        proof.NativeFacts[".comparator"].Restrictions.ShouldContain(restriction =>
            restriction.Kind == ProfileRestrictionKind.Binding && ProfileUseFacts.Text(restriction.Value, "valueSet") == valueSetUrl + "|4.0.1");
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.Diagnostics.ShouldContain(message => message.Contains("target restriction not entailed") && message.Contains(valueSetUrl + "|4.0.1"));
        output.WriteLine($"Required R5->R4 obstruction: {source.VersionedUrl}#Quantity.comparator allows http://hl7.org/fhir/quantity-comparator|5.0.0#ad; {target.VersionedUrl}#Quantity.comparator requires {targetDomain.VersionedUrl}, which excludes ad. Every target Quantity descendant either prohibits comparator or retains that required domain. Narrowing/projection cannot widen the target requirement.");
    }

    [Fact]
    public async Task PersistedR5ComparatorAdditionRefusesScheduledQuantityConsumerBeforeAnyWrites()
    {
        await AssertPersistedConsumerNegative(_cores[4], "Quantity", ProfileReasonCategory.Binding);
    }

    [Fact]
    public async Task PersistedDstu2AgeZeroRefusesScheduledAgeConsumerBeforeAnyWrites()
    {
        await AssertPersistedConsumerNegative(_cores[0], "Age", ProfileReasonCategory.InvariantTransformation);
    }

    private async Task AssertPersistedConsumerNegative(string sourceCore, string datatype, ProfileReasonCategory reason)
    {
        DefinitionCollection sourceDefinitions = await LoadCore(sourceCore);
        DefinitionCollection targetDefinitions = await LoadCore(_cores[2]);
        AddConsumerCarrier(sourceDefinitions, datatype);
        AddConsumerCarrier(targetDefinitions, datatype);
        using CoreCatalog imported = new([sourceDefinitions, targetDefinitions]);
        imported.Reopen();
        FhirPackageComparisonPair pair = imported.ArrangeConsumerPipeline(sourceDefinitions.FhirSequence, targetDefinitions.FhirSequence);
        imported.Reopen();
        UnsafeCodeInputStore.RequireExportReady(imported.Connection, [pair]);
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition sourceOwner = catalog.ResolveDefinition(pair.SourcePackageKey, ConsumerUrl)!;
        DbStructureDefinition targetOwner = catalog.ResolveDefinition(pair.TargetPackageKey, ConsumerUrl)!;
        DbElement sourceUse = catalog.ResolveOwnedElement(sourceOwner, ConsumerName + ".measure")!;
        DbElement targetUse = catalog.ResolveOwnedElement(targetOwner, ConsumerName + ".measure")!;
        DbStructureDefinition source = catalog.ResolveDefinition(pair.SourcePackageKey, QuantityUrl.Replace("Quantity", datatype))!;
        DbStructureDefinition target = catalog.ResolveDefinition(pair.TargetPackageKey, source.UnversionedUrl)!;
        foreach ((DefinitionCollection definitions, DbFhirPackage package) in new[]
        {
            (sourceDefinitions, pair.SourcePackage), (targetDefinitions, pair.TargetPackage),
        })
        {
            catalog.GetDefinitions(package.Key).Where(owner => owner.ArtifactClass == FhirArtifactClassEnum.Profile)
                .Select(owner => owner.VersionedUrl).Order().ShouldBe(definitions.ProfilesByUrl.Values
                    .Select(profile => profile.Url + "|" + profile.Version).Order());
            foreach (string name in new[] { datatype, "Quantity" }.Distinct())
            {
                StructureDefinition normalized = definitions.ProfilesByUrl.GetValueOrDefault(QuantityUrl.Replace("Quantity", name))
                    ?? definitions.ComplexTypesByName[name];
                DbStructureDefinition retained = catalog.ResolveDefinition(package.Key, normalized.Url + "|" + normalized.Version)!;
                catalog.ReadPayload(retained).ToJson().ShouldBe(normalized.ToJson());
                catalog.GetOwnedElements(retained).Count.ShouldBe(normalized.Snapshot.Element.Count);
            }
        }
        DbElementComparison comparison = DbElementComparison.SelectSingle(imported.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey,
            SourceElementKey: sourceUse.Key)!;
        comparison.TargetElementKey.ShouldBe(targetUse.Key);
        comparison.ElementMappingKey.ShouldNotBeNull();
        DbElementMapping mapping = DbElementMapping.SelectSingle(imported.Connection, Key: comparison.ElementMappingKey!.Value)!;
        mapping.SourceStructureKey.ShouldBe(sourceOwner.Key);
        mapping.TargetStructureKey.ShouldBe(targetOwner.Key);
        mapping.ConceptMapSourceKey.ShouldNotBeNull();
        DbElementOutcome outcome = DbElementOutcome.SelectSingle(imported.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey,
            SourceElementKey: sourceUse.Key)!;
        outcome.ShouldNotBeNull();
        outcome.ExtensionDefinitionIsProhibited.ShouldBeTrue();
        outcome.IsEquivalent.ShouldBeFalse();
        outcome.IsIdentical.ShouldBeFalse();
        outcome.FullyMapsAcrossAllTargets.ShouldBeFalse();
        outcome.FullyMapsToThisTarget.ShouldBeFalse();
        outcome.GenUrl.ShouldBeNull();
        outcome.ExtensionSubstitutionUrl.ShouldBeNull();
        outcome.ContentReferenceExtensionUrl.ShouldBeNull();
        string persisted = outcome.ExtensionProhibitionReason!;
        PersistedProfileDecision decision = ProfileUseFacts.ReadDecisions(persisted).ShouldHaveSingleItem();
        decision.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        decision.Input.PackageKey.ShouldBe(pair.SourcePackageKey);
        decision.Input.OwnerKey.ShouldBe(sourceOwner.Key);
        decision.Input.ElementKey.ShouldBe(sourceUse.Key);
        decision.Input.TypeUseToken.ShouldBe(0);
        decision.Input.SliceContext.ShouldBe(".measure");
        decision.InputOwnerRoot!.Canonical.ShouldBe(ConsumerUrl);
        decision.InputOwnerRoot.Version.ShouldBe(sourceOwner.Version);
        decision.NativeInput!.PackageKey.ShouldBe(pair.TargetPackageKey);
        decision.NativeInput.OwnerKey.ShouldBe(targetOwner.Key);
        decision.NativeInput.ElementKey.ShouldBe(targetUse.Key);
        decision.NativeInput.TypeUseToken.ShouldBe(0);
        decision.NativeOwnerRoot!.Canonical.ShouldBe(ConsumerUrl);
        decision.NativeOwnerRoot.Version.ShouldBe(targetOwner.Version);
        decision.SourceRoot.Canonical.ShouldBe(source.UnversionedUrl);
        decision.SourceRoot.Version.ShouldBe(source.Version);
        decision.SourceRoot.ElementId.ShouldBe(catalog.ResolveRoot(source).Id);
        decision.TargetPackageKey.ShouldBe(pair.TargetPackageKey);
        decision.TargetRoot!.Canonical.ShouldBe(target.UnversionedUrl);
        decision.TargetRoot.Version.ShouldBe(target.Version);
        decision.TargetCanonical.ShouldBeNull();
        decision.Reasons.ShouldContain(fact => fact.Category == reason);
        using ProfileUseFacts facts = new(catalog, pair.SourcePackageKey, pair.TargetPackageKey,
            DbElementComparison.SelectList(imported.Connection));
        ProfileRepresentationProof actual = ProfileUseFacts.ClassifyPair(imported.Connection, pair)
            .Single(proof => proof.Input == decision.Input);
        actual.PreservesRestrictions.ShouldBeFalse();
        actual.TargetCanonical.ShouldBeNull();
        actual.Projection.ShouldBeEmpty();
        actual.ProjectionOwner.ShouldBeNull();
        ProfileRepresentationProof projected = facts.ProveInOwner(actual, targetOwner, new HashSet<int> { targetOwner.Key });
        projected.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        projected.PreservesRestrictions.ShouldBeFalse();
        projected.TargetCanonical.ShouldBeNull();
        projected.Projection.ShouldBeEmpty();
        projected.ProjectionOwner.ShouldBeNull();
        string witness;
        if (datatype == "Quantity")
        {
            DbValueSet targetDomain = AssertComparatorDomains(imported.Connection, pair.SourcePackageKey, pair.TargetPackageKey);
            AssertRequiredBinding(decision.RequiredFacts[".comparator"], targetDomain.UnversionedUrl + "|5.0.0");
            AssertRequiredBinding(decision.NativeFacts[".comparator"], targetDomain.VersionedUrl);
            decision.Reasons.ShouldContain(fact => fact.Category == reason && fact.RelativeId == ".comparator" &&
                fact.Origin.Canonical == QuantityUrl && fact.Origin.Version == target.Version &&
                fact.Detail.Contains(targetDomain.VersionedUrl, StringComparison.Ordinal));
            projected.Reasons.ShouldContain(fact => fact.Category == ProfileReasonCategory.Binding);
            witness = targetDomain.VersionedUrl;
        }
        else
        {
            source.ArtifactClass.ShouldBe(FhirArtifactClassEnum.Profile);
            target.ArtifactClass.ShouldBe(FhirArtifactClassEnum.ComplexType);
            catalog.GetDeclaredType(source).ShouldBe("Quantity");
            decision.Input.ProfileAlternative.ShouldBe(source.VersionedUrl);
            AssertAgeZeroWitness(decision.RequiredFacts[""].Invariants.Single(invariant => invariant.Key == "age-1"),
                decision.NativeFacts[""].Invariants.First(invariant => invariant.Key == "age-1"));
            decision.Reasons.ShouldContain(fact => fact.Category == reason && fact.Origin.Canonical == target.UnversionedUrl &&
                fact.Origin.Version == target.Version && fact.Detail.Contains("age-1", StringComparison.Ordinal));
            projected.Reasons.ShouldContain(fact => fact.Category == ProfileReasonCategory.InvariantTransformation);
            witness = "age-1";
        }
        DbStructureOutcome ownerOutcome = DbStructureOutcome.SelectSingle(imported.Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)!;
        ownerOutcome.SourceStructureKey.ShouldBe(sourceOwner.Key);
        ownerOutcome.IsEquivalent.ShouldBeFalse();
        ownerOutcome.TargetCanonicalVersioned.ShouldBeNull();
        ownerOutcome.TargetCanonicalUnversioned.ShouldBeNull();
        DbUnsafeCodeRule.SelectList(imported.Connection).ShouldBeEmpty();
        DbUnsafeCodeTrigger.SelectList(imported.Connection).ShouldBeEmpty();

        ConfigXVer config = imported.ConsumerExportConfig();
        XVerExporter exporter = new(imported.Connection, config);
        IgExporter ig = new(imported.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.ExportSchedule schedule = ig.BuildExportSchedule(specificPairs: [pair.SequencePair]);
        schedule.PackagePairs.ShouldHaveSingleItem().SequencePair.ShouldBe(pair.SequencePair);
        IgExporter.XVerExportTrackingRecord prepared = ig.PrepareXVerIgs(schedule);
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        InvalidOperationException buildFailure = Should.Throw<InvalidOperationException>(() =>
            new StructureFhirExporter(exporter, imported.Connection, NullLoggerFactory.Instance)
                .BuildDefinitions(prepared.XVerIgs.ShouldHaveSingleItem()));
        buildFailure.Message.ShouldContain("Unrepresentable scheduled");
        buildFailure.Message.ShouldContain(sourceOwner.VersionedUrl);
        buildFailure.Message.ShouldContain(witness);
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        List<string> sentinels = [];
        foreach (string directory in prepared.XVerIgs.Select(tracking => tracking.IgRootDir!)
            .Concat(prepared.ValidationIgs.Select(tracking => tracking.IgRootDir!)))
        {
            Directory.CreateDirectory(directory);
            string sentinel = Path.Combine(directory, "sentinel.txt");
            File.WriteAllText(sentinel, "preserve pre-existing IG and vocabulary output");
            sentinels.Add(sentinel);
        }
        InvalidOperationException exportFailure = Should.Throw<InvalidOperationException>(() =>
            exporter.Export(specificPairs: [pair.SequencePair]));
        exportFailure.Message.ShouldContain("Unrepresentable scheduled");
        exportFailure.Message.ShouldContain(sourceOwner.VersionedUrl);
        exportFailure.Message.ShouldContain(witness);
        Directory.GetFiles(config.OutputDirectory, "*", SearchOption.AllDirectories).Order().ShouldBe(sentinels.Order());
        sentinels.ShouldAllBe(path => File.ReadAllText(path) == "preserve pre-existing IG and vocabulary output");
        imported.Reopen();
        UnsafeCodeInputStore.RequireExportReady(imported.Connection, [pair]);
        DbElementOutcome.SelectSingle(imported.Connection, Key: outcome.Key)!.ExtensionProhibitionReason.ShouldBe(persisted);
        DbUnsafeCodeRule.SelectList(imported.Connection).ShouldBeEmpty();
        DbUnsafeCodeTrigger.SelectList(imported.Connection).ShouldBeEmpty();
        imported.ConnectionReopens.ShouldBe(5);
        output.WriteLine($"{pair.SourcePackage.Name}->{pair.TargetPackage.Name}: full unchanged core inventory, local {ConsumerName}.measure ({source.VersionedUrl}); authored carrier maps/comparisons/outcome, production negative persistence, physical reopen, RequireExportReady, actual BuildDefinitions and combined vocabulary/structure Export refuse {witness} before writes.");
    }

    [Fact]
    public async Task PersistedRangeConsumerStillBuildsWithUnscheduledCoreQuantityNegatives()
    {
        DefinitionCollection sourceDefinitions = await LoadCore(_cores[4]);
        DefinitionCollection targetDefinitions = await LoadCore(_cores[3]);
        AddConsumerCarrier(sourceDefinitions, "Range");
        AddConsumerCarrier(targetDefinitions, "Range");
        using CoreCatalog imported = new([sourceDefinitions, targetDefinitions]);
        imported.Reopen();
        FhirPackageComparisonPair pair = imported.ArrangeConsumerPipeline(sourceDefinitions.FhirSequence, targetDefinitions.FhirSequence);
        imported.Reopen();
        UnsafeCodeInputStore.RequireExportReady(imported.Connection, [pair]);
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition range = catalog.ResolveNativeType(pair.SourcePackageKey, "Range")!;
        DbStructureDefinition target = catalog.ResolveDefinition(pair.TargetPackageKey,
            "http://hl7.org/fhir/StructureDefinition/SimpleQuantity|4.3.0")!;
        IReadOnlyList<ProfileRepresentationProof> decisions = ProfileUseFacts.ClassifyPair(imported.Connection, pair);
        foreach (string bound in new[] { "low", "high" })
        {
            DbElement element = catalog.ResolveOwnedElement(range, "Range." + bound)!;
            ProfileRepresentationProof proof = decisions.Single(decision => decision.Input.ElementKey == element.Key);
            using ProfileUseFacts facts = new(catalog, pair.SourcePackageKey, pair.TargetPackageKey);
            EffectiveProfileBranch branch = facts.AtUse(range, element, 0).Branches.ShouldHaveSingleItem();
            proof.Kind.ShouldBe(ProfileProofKind.NativeReuse);
            AssertRangeProof(proof, branch, target, catalog);
        }
        DbElementOutcome.SelectList(imported.Connection).ShouldContain(outcome => outcome.ExtensionDefinitionIsProhibited);
        DbStructureOutcome owner = DbStructureOutcome.SelectList(imported.Connection).ShouldHaveSingleItem();
        owner.SourceId.ShouldBe(ConsumerName);
        DbElementOutcome.SelectList(imported.Connection, SourceStructureKey: owner.SourceStructureKey).ShouldBeEmpty();
        ConfigXVer config = imported.ConsumerExportConfig();
        XVerExporter exporter = new(imported.Connection, config);
        IgExporter ig = new(imported.Connection, NullLoggerFactory.Instance, config.OutputDirectory, null, exporter, config);
        IgExporter.XVerExportTrackingRecord prepared = ig.PrepareXVerIgs(ig.BuildExportSchedule(specificPairs: [pair.SequencePair]));
        StructureFhirExporter.BuiltDefinitions built = new StructureFhirExporter(exporter, imported.Connection, NullLoggerFactory.Instance)
            .BuildDefinitions(prepared.XVerIgs.ShouldHaveSingleItem());
        built.Profiles.ShouldHaveSingleItem().Definition.BaseDefinition.ShouldBe(ConsumerUrl);
        built.Profiles.ShouldHaveSingleItem().Projections.ShouldBeEmpty();
        built.Extensions.ShouldBeEmpty();
        Directory.Exists(config.OutputDirectory).ShouldBeFalse();
        UnsafeCodeInputStore.RequireExportReady(imported.Connection, [pair]);
        output.WriteLine("R5->R4B: the actual scheduled Range carrier builds without projecting either bound; exact SimpleQuantity|4.3.0/sqty-1 reuse survives unrelated persisted core negatives.");
    }

    private static void AssertRequiredBinding(EffectiveProfileElement comparator, string canonical)
    {
        comparator.Restrictions.ShouldContain(restriction => restriction.Kind == ProfileRestrictionKind.Binding &&
            ProfileUseFacts.Text(restriction.Value, "strength") == "required" &&
            ProfileUseFacts.Text(restriction.Value, "valueSet") == canonical);
    }

    private static DbValueSet AssertComparatorDomains(IDbConnection connection, int sourcePackageKey, int targetPackageKey)
    {
        const string valueSetUrl = "http://hl7.org/fhir/ValueSet/quantity-comparator";
        const string systemUrl = "http://hl7.org/fhir/quantity-comparator";
        DbValueSet sourceDomain = DbValueSet.SelectList(connection, FhirPackageKey: sourcePackageKey)
            .Single(valueSet => valueSet.UnversionedUrl == valueSetUrl && valueSet.Version == "5.0.0");
        DbValueSet targetDomain = DbValueSet.SelectList(connection, FhirPackageKey: targetPackageKey)
            .Single(valueSet => valueSet.UnversionedUrl == valueSetUrl && valueSet.Version == "4.0.1");
        sourceDomain.CanExpand.ShouldBeTrue();
        targetDomain.CanExpand.ShouldBeTrue();
        UnsafeCodeIdentity[] sourceMembers = [.. DbValueSetConcept.SelectList(connection, ValueSetKey: sourceDomain.Key)
            .Select(member => new UnsafeCodeIdentity(member.System, member.SystemVersion, member.Code))];
        UnsafeCodeIdentity[] targetMembers = [.. DbValueSetConcept.SelectList(connection, ValueSetKey: targetDomain.Key)
            .Select(member => new UnsafeCodeIdentity(member.System, member.SystemVersion, member.Code))];
        sourceMembers.Length.ShouldBe(5);
        targetMembers.Length.ShouldBe(4);
        sourceMembers.ShouldAllBe(member => member.System == systemUrl && member.Version == "5.0.0");
        targetMembers.ShouldAllBe(member => member.System == systemUrl && member.Version == "4.0.1");
        foreach (string comparator in new[] { "<", "<=", ">=", ">", "ad" })
        {
            ISourceNode witness = FhirJsonNode.Parse(
                $$$"""{"resourceType":"Parameters","parameter":[{"name":"witness","valueQuantity":{"value":1,"comparator":"{{{comparator}}}"}}]}""")
                .Children("parameter").Single().Children("valueQuantity").Single();
            string actual = witness.Children("comparator").Single().Text!;
            // Focused required-binding membership, not a claim of full instance validation.
            sourceMembers.ShouldContain(new UnsafeCodeIdentity(systemUrl, "5.0.0", actual));
            targetMembers.Contains(new UnsafeCodeIdentity(systemUrl, "4.0.1", actual)).ShouldBe(actual != "ad");
        }
        return targetDomain;
    }

    private static void AssertAgeZeroWitness(ProfileInvariant sourceInvariant, ProfileInvariant targetInvariant)
    {
        sourceInvariant.XPath.ShouldNotBeNull();
        targetInvariant.Expression.ShouldNotBeNull();
        sourceInvariant.XPath.ShouldContain("not(contains(f:value/@value, '-'))");
        targetInvariant.Expression.ShouldContain("value > 0");
        XmlNamespaceManager namespaces = new(new NameTable());
        namespaces.AddNamespace("f", "http://hl7.org/fhir");
        // XPath 1.0's boolean(node-set) executes only this exact XPath 2.0 exists(node-set).
        string executableXPath = sourceInvariant.XPath.Replace("exists(", "boolean(", StringComparison.Ordinal);
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        CompiledExpression compiled = compiler.Compile(targetInvariant.Expression);
        foreach (int value in new[] { -1, 0, 1 })
        {
            XDocument sourceInstance = XDocument.Parse(
                $"""<Quantity xmlns="http://hl7.org/fhir"><value value="{value}"/><system value="http://unitsofmeasure.org"/><code value="a"/></Quantity>""");
            ((bool)sourceInstance.Root!.XPathEvaluate(executableXPath, namespaces)).ShouldBe(value >= 0);
            Hl7.Fhir.Model.Quantity targetInstance = new() { Value = value, System = "http://unitsofmeasure.org", Code = "a" };
            compiled(targetInstance.ToTypedElement(), new EvaluationContext()).ShouldHaveSingleItem().Value.ShouldBe(value > 0);
        }
    }

    private const string ConsumerName = "QuantityConsumerWitness";
    private const string ConsumerUrl = "http://example.org/StructureDefinition/" + ConsumerName;

    private static void AddConsumerCarrier(DefinitionCollection definitions, string datatype)
    {
        // A local resource specialization isolates scheduling, not datatype semantics. Keep the
        // entire actual core inventory and DomainResource snapshot, adding just one typed field.
        StructureDefinition basis = definitions.ResourcesByName["DomainResource"];
        StructureDefinition carrier = (StructureDefinition)basis.DeepCopy();
        carrier.Id = ConsumerName;
        carrier.Name = ConsumerName;
        carrier.Url = ConsumerUrl;
        carrier.Version = definitions.MainPackageVersion;
        carrier.Type = ConsumerName;
        carrier.Abstract = false;
        carrier.BaseDefinition = basis.Url + "|" + basis.Version;
        carrier.Derivation = StructureDefinition.TypeDerivationRule.Specialization;
        foreach (ElementDefinition element in carrier.Snapshot.Element)
        {
            element.ElementId = ConsumerName + element.ElementId[basis.Snapshot.Element[0].ElementId.Length..];
            element.Path = ConsumerName + element.Path["DomainResource".Length..];
        }
        StructureDefinition actual = definitions.ProfilesByUrl.GetValueOrDefault(QuantityUrl.Replace("Quantity", datatype))
            ?? definitions.ComplexTypesByName[datatype];
        ElementDefinition use = new()
        {
            ElementId = ConsumerName + ".measure", Path = ConsumerName + ".measure",
            Min = 0, Max = "1", Short = "Local carrier of the unchanged actual core datatype",
            Base = new() { Path = ConsumerName + ".measure", Min = 0, Max = "1" },
            Type = [new()
            {
                Code = actual.Type,
                Profile = actual.Derivation == StructureDefinition.TypeDerivationRule.Constraint
                    ? [actual.Url + "|" + actual.Version] : [],
            }],
        };
        carrier.Snapshot.Element.Add(use);
        carrier.Differential = new()
        {
            Element = [new() { ElementId = ConsumerName, Path = ConsumerName }, (ElementDefinition)use.DeepCopy()],
        };
        definitions.AddResourceDefinition(carrier, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
    }

    private static DbStructureDefinition[] UsedNativeFamily(XVerStructureCatalog catalog, int packageKey, IEnumerable<DbElementType> types)
    {
        HashSet<int> operational = [.. catalog.GetProcessedStructures(packageKey).Select(owner => owner.Key)];
        HashSet<int> used = [.. types.Where(type => type.FhirPackageKey == packageKey && operational.Contains(type.StructureKey))
            .Select(type => type.TypeProfileStructureKey ?? type.TypeStructureKey).OfType<int>()];
        DbStructureDefinition quantity = catalog.ResolveNativeType(packageKey, "Quantity")!;
        return [.. catalog.GetDefinitions(packageKey).Where(owner =>
            owner.ArtifactClass == FhirArtifactClassEnum.ComplexType && used.Contains(owner.Key) &&
            (owner.Key == quantity.Key || catalog.GetAncestors(owner).Any(ancestor => ancestor.Key == quantity.Key)))];
    }

    private static async Task<DefinitionCollection> LoadCore(string moniker)
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fhir", "packages");
        List<string> prerequisites = [moniker];
        if (!moniker.StartsWith("hl7.fhir.r2.core#", StringComparison.Ordinal))
        {
            prerequisites.Add(moniker.Replace(".core#", ".expansions#"));
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

    private sealed class CoreCatalog : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests", "quantity-" + Guid.NewGuid().ToString("N"));

        internal CoreCatalog(IReadOnlyList<DefinitionCollection> definitions)
        {
            Database = new([.. definitions], _directory, "quantity.sqlite", NullLoggerFactory.Instance);
            Database.TryLoadFromDefinitionCollections(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes).ShouldBeTrue();
        }

        internal ComparisonDatabase Database { get; private set; }
        internal IDbConnection Connection => Database.DbConnection;
        internal XVerStructureCatalog Catalog => new(Connection, XVerProcessor._exclusionSet);
        internal int ConnectionReopens { get; private set; }

        internal void Reopen()
        {
            Close();
            Database = new(_directory, "quantity.sqlite", NullLoggerFactory.Instance);
            DbMappingClasses.LoadIndices(Connection);
            DbComparisonClasses.LoadIndices(Connection);
            DbOutcomeClasses.LoadIndices(Connection);
            ConnectionReopens++;
        }

        internal ConfigXVer ConsumerExportConfig() => new()
        {
            OutputDirectory = Path.Combine(_directory, "consumer-output"),
            ExportR2 = false, ExportR3 = false, ExportR4 = true, ExportR4B = true,
            ExportR5 = false, ExportR6 = false, LogFactory = NullLoggerFactory.Instance,
        };

        internal FhirPackageComparisonPair ArrangeConsumerPipeline(
            FhirReleases.FhirSequenceCodes sourceSequence, FhirReleases.FhirSequenceCodes targetSequence)
        {
            List<DbFhirPackage> packages = DbFhirPackage.SelectList(Connection);
            FhirPackageComparisonPair pair = new(packages.Single(package => package.DefinitionFhirSequence == sourceSequence),
                packages.Single(package => package.DefinitionFhirSequence == targetSequence));
            DbStructureDefinition source = Catalog.ResolveDefinition(pair.SourcePackageKey, ConsumerUrl)!;
            DbStructureDefinition target = Catalog.ResolveDefinition(pair.TargetPackageKey, ConsumerUrl)!;
            string sourceRoot = Path.Combine(_directory, "local-maps");
            string suffix = pair.SourcePackage.FhirVersionShort + "to" + pair.TargetPackage.FhirVersionShort;
            WriteMap("resources", new()
            {
                Source = $"http://hl7.org/fhir/{pair.SourcePackage.FhirVersionShort}/resource-types",
                Target = $"http://hl7.org/fhir/{pair.TargetPackage.FhirVersionShort}/resource-types",
                Element = [new()
                {
                    Code = ConsumerName,
                    Target = [new() { Code = ConsumerName, Relationship = ConceptMap.ConceptMapRelationship.RelatedTo }],
                }],
            });
            WriteMap("elements", new()
            {
                Source = source.VersionedUrl, Target = target.VersionedUrl,
                Element = [.. Catalog.GetOwnedElements(source).Select(element => new ConceptMap.SourceElementComponent
                {
                    Code = element.Id,
                    Target = [new() { Code = element.Id, Relationship = ConceptMap.ConceptMapRelationship.RelatedTo }],
                })],
            });
            // Only the local carrier has a scheduled outcome. The complete actual core catalog,
            // including every retained profile and every negative decision, remains present.
            new MappingLoader(Connection, NullLoggerFactory.Instance)
                .TryLoadCrossVersionSourceMaps(sourceRoot, useInternalTypeMaps: false).ShouldBeTrue();
            Reopen();
            UnsafeCodeInputStore.RequireInputs(Connection);
            DbStructureMapping map = DbStructureMapping.SelectList(Connection,
                SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey).ShouldHaveSingleItem();
            map.SourceStructureKey.ShouldBe(source.Key);
            map.TargetStructureKey.ShouldBe(target.Key);
            DbComparisonClasses.CreateTables(Connection);
            string? revision = UnsafeCodeInputStore.BeginComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
            ConsumerStructureComparison comparison = new(pair, source, target, map.Key);
            comparison.Key = DbStructureComparison.Insert(Connection, comparison);
            foreach (DbElementMapping elementMap in DbElementMapping.SelectList(Connection,
                SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey))
            {
                ConsumerElementComparison element = new(pair, comparison.Key, elementMap);
                element.Key = DbElementComparison.Insert(Connection, element);
            }
            DbElementComparison.SelectList(Connection).Count.ShouldBe(Catalog.GetOwnedElements(source).Count);
            UnsafeCodeInputStore.CompleteComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey, revision);
            Reopen();
            DbOutcomeClasses.CreateTables(Connection);
            DbUnsafeCodePairState dependency = UnsafeCodeInputStore.BeginOutcomes(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
            ConsumerStructureOutcome outcome = new(pair, source, target, comparison.Key);
            outcome.Key = DbStructureOutcome.Insert(Connection, outcome);
            new UnsafeCodeOutcomeGenerator(Connection).GenerateForPair(pair, dependency);
            return pair;

            void WriteMap(string category, ConceptMap.GroupComponent group)
            {
                string directory = Path.Combine(sourceRoot, "input", category);
                Directory.CreateDirectory(directory);
                ConceptMap authored = new()
                {
                    Id = category + "-" + suffix,
                    Url = "http://example.org/ConceptMap/consumer-" + category + "-" + suffix,
                    Status = Hl7.Fhir.Model.PublicationStatus.Active,
                    Group = [group],
                };
                File.WriteAllText(Path.Combine(directory, "ConceptMap-" + category + "-" + suffix + ".json"), authored.ToJson());
            }
        }

        private void Close()
        {
            SqliteConnection connection = (SqliteConnection)Connection;
            ((IDisposable)Database).Dispose();
            SqliteConnection.ClearPool(connection);
        }

        public void Dispose()
        {
            Close();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(100);
                }
            }
        }

        // Explicit, conservative ordinary-stage facts for the one local carrier, not a
        // whole-core comparer run. Production classification, persistence and export are unmodified.
        private sealed class ConsumerStructureComparison : DbStructureComparison
        {
            [SetsRequiredMembers]
            internal ConsumerStructureComparison(FhirPackageComparisonPair pair, DbStructureDefinition source,
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

        private sealed class ConsumerElementComparison : DbElementComparison
        {
            [SetsRequiredMembers]
            internal ConsumerElementComparison(FhirPackageComparisonPair pair, int comparisonKey, DbElementMapping map)
            {
                SourceFhirPackageKey = pair.SourcePackageKey;
                TargetFhirPackageKey = pair.TargetPackageKey;
                SourceFhirSequence = pair.SourceFhirSequence;
                TargetFhirSequence = pair.TargetFhirSequence;
                StructureComparisonKey = comparisonKey;
                SourceStructureKey = map.SourceStructureKey;
                TargetStructureKey = map.TargetStructureKey;
                SourceElementKey = map.SourceElementKey!.Value;
                TargetElementKey = map.TargetElementKey;
                SourceElementId = map.SourceElementId;
                TargetElementId = map.TargetElementId;
                ElementMappingKey = map.Key;
                Steps = Math.Abs(pair.SourcePackage.PackageArrayIndex - pair.TargetPackage.PackageArrayIndex);
                Relationship = ConceptMap.ConceptMapRelationship.RelatedTo;
                RelativePathsAreIdentical = true;
                int?[] keys = new int?[6];
                keys[pair.SourcePackage.PackageArrayIndex] = map.SourceElementKey;
                keys[pair.TargetPackage.PackageArrayIndex] = map.TargetElementKey;
                ContentKeys = keys;
            }
        }

        private sealed class ConsumerStructureOutcome : DbStructureOutcome
        {
            [SetsRequiredMembers]
            internal ConsumerStructureOutcome(FhirPackageComparisonPair pair, DbStructureDefinition source,
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
    }
}
