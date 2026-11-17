// <copyright file="XVerQuantityProfileTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
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
        DbStructureDefinition[] retained = [.. catalog.GetDefinitions(package.Key).Where(owner =>
            owner.ArtifactClass == FhirArtifactClassEnum.Profile && catalog.GetDeclaredType(owner) == "Quantity")];
        retained.Select(owner => owner.UnversionedUrl).Order().ShouldBe(profiles.Select(profile => profile.Url).Order());
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
        sourceInvariant.XPath.ShouldNotBeNull();
        targetInvariant.Expression.ShouldNotBeNull();
        sourceInvariant.XPath.ShouldContain("not(contains(f:value/@value, '-'))");
        targetInvariant.Expression.ShouldContain("value > 0");
        XmlNamespaceManager namespaces = new(new NameTable());
        namespaces.AddNamespace("f", "http://hl7.org/fhir");
        XDocument sourceInstance = XDocument.Parse("""
            <Quantity xmlns="http://hl7.org/fhir"><value value="0"/><system value="http://unitsofmeasure.org"/><code value="a"/></Quantity>
            """);
        // XPath 1.0's boolean(node-set) is used only to execute this exact XPath 2.0 exists(node-set).
        string executableXPath = sourceInvariant.XPath.Replace("exists(", "boolean(", StringComparison.Ordinal);
        ((bool)sourceInstance.Root!.XPathEvaluate(executableXPath, namespaces)).ShouldBeTrue();
        Hl7.Fhir.Model.Quantity targetInstance = new() { Value = 0, System = "http://unitsofmeasure.org", Code = "a" };
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        CompiledExpression compiled = compiler.Compile(targetInvariant.Expression);
        compiled(targetInstance.ToTypedElement(), new EvaluationContext()).ShouldHaveSingleItem().Value.ShouldBe(false);
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
        const string systemUrl = "http://hl7.org/fhir/quantity-comparator";
        DbValueSet sourceDomain = DbValueSet.SelectList(imported.Connection, FhirPackageKey: sourcePackage.Key)
            .Single(valueSet => valueSet.UnversionedUrl == valueSetUrl && valueSet.Version == "5.0.0");
        DbValueSet targetDomain = DbValueSet.SelectList(imported.Connection, FhirPackageKey: targetPackage.Key)
            .Single(valueSet => valueSet.UnversionedUrl == valueSetUrl && valueSet.Version == "4.0.1");
        sourceDomain.CanExpand.ShouldBeTrue();
        targetDomain.CanExpand.ShouldBeTrue();
        UnsafeCodeIdentity[] sourceMembers = [.. DbValueSetConcept.SelectList(imported.Connection, ValueSetKey: sourceDomain.Key)
            .Select(member => new UnsafeCodeIdentity(member.System, member.SystemVersion, member.Code))];
        UnsafeCodeIdentity[] targetMembers = [.. DbValueSetConcept.SelectList(imported.Connection, ValueSetKey: targetDomain.Key)
            .Select(member => new UnsafeCodeIdentity(member.System, member.SystemVersion, member.Code))];
        sourceMembers.Length.ShouldBe(5);
        targetMembers.Length.ShouldBe(4);
        sourceMembers.ShouldContain(new UnsafeCodeIdentity(systemUrl, "5.0.0", "ad"));
        targetMembers.ShouldAllBe(member => member.System == systemUrl && member.Version == "4.0.1");
        targetMembers.ShouldNotContain(member => member.Code == "ad");
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
        output.WriteLine($"Required R5->R4 obstruction: {source.VersionedUrl}#Quantity.comparator allows {systemUrl}|5.0.0#ad; {target.VersionedUrl}#Quantity.comparator requires {targetDomain.VersionedUrl}, which excludes ad. Every target Quantity descendant either prohibits comparator or retains that required domain. Narrowing/projection cannot widen the target requirement.");
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
            Database.TryLoadFullCatalogCandidate(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes).ShouldBeTrue();
        }

        internal ComparisonDatabase Database { get; }
        internal IDbConnection Connection => Database.DbConnection;
        internal XVerStructureCatalog Catalog => new(Connection, XVerProcessor._exclusionSet);

        public void Dispose()
        {
            SqliteConnection connection = (SqliteConnection)Connection;
            ((IDisposable)Database).Dispose();
            SqliteConnection.ClearPool(connection);
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
    }
}
