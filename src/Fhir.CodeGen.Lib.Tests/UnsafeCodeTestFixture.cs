// <copyright file="UnsafeCodeTestFixture.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fhir.CodeGen.Lib.Tests;

internal sealed class UnsafeCodeTestFixture : IDisposable
{
    internal const string FilterSystem = "http://hl7.org/fhir/filter-operator";
    internal const string FilterValueSet = "http://hl7.org/fhir/ValueSet/filter-operator";
    internal const string FilterElement = "ValueSet.compose.include.filter.op";

    internal SqliteConnection Connection { get; }
    internal string SourceRoot { get; }
    internal string ConfigurationFilename => Path.Combine(SourceRoot, "input", "ig-support", "unsafe-code-mappings.json");
    internal List<DbFhirPackage> Packages { get; } = [];
    internal DbUnsafeCodeInputState InputState => DbUnsafeCodeInputState.SelectSingle(Connection, Key: 1)!;

    internal UnsafeCodeTestFixture()
    {
        Connection = new("Data Source=:memory:");
        Connection.Open();
        SourceRoot = Path.Combine(Path.GetTempPath(), "fhir-codegen-unsafe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(SourceRoot);
            string input = Path.Combine(AppContext.BaseDirectory, "TestData", "XverUnsafeCodes");
            foreach (string filename in Directory.GetFiles(input, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(SourceRoot, Path.GetRelativePath(input, filename));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(filename, destination);
            }

            DbContentClasses.CreateTables(Connection);
            DbMappingClasses.CreateTables(Connection);
            FhirReleases.FhirSequenceCodes[] sequences = [FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R4B, FhirReleases.FhirSequenceCodes.R5];
            foreach (FhirReleases.FhirSequenceCodes sequence in sequences)
            {
                DbFhirPackage package = new()
                {
                    Name = sequence.ToCorePackageId(),
                    PackageId = sequence.ToCorePackageId(),
                    PackageVersion = sequence.ToLongVersion(),
                    FhirVersionShort = sequence.ToShortVersion(),
                    CanonicalUrl = "http://hl7.org/fhir",
                    ShortName = sequence.ToRLiteral(),
                    Dependencies = null,
                    DefinitionFhirSequence = sequence,
                };
                package.Key = DbFhirPackage.Insert(Connection, package);
                Packages.Add(package);
                InsertStructure(package);
                InsertValueSet(package);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool Load() => new MappingLoader(Connection, NullLoggerFactory.Instance).TryLoadCrossVersionSourceMaps(SourceRoot, false);

    internal void WriteConfiguration(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFilename)!);
        File.WriteAllText(ConfigurationFilename, json);
    }

    internal ConceptMap ReadMap(string category, string filename) =>
        new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(Path.Combine(SourceRoot, "input", category, filename)));

    internal void Capture(ConceptMap map, string category = "elements")
    {
        UnsafeCodeSourceReader.Capture(Connection, map, category, Path.Combine(SourceRoot, "test.json"), "input/test.json", Packages[2].Key, Packages[0].Key);
    }

    internal void SetExpandable(bool value)
    {
        List<DbValueSet> rows = DbValueSet.SelectList(Connection);
        foreach (DbValueSet row in rows)
        {
            row.CanExpand = value;
        }

        rows.Update(Connection);
    }

    internal void InsertFilterElements()
    {
        foreach (DbFhirPackage package in Packages)
        {
            TestElement row = new(package, DbStructureDefinition.SelectSingle(Connection, FhirPackageKey: package.Key, Id: "ValueSet")!.Key, FilterElement);
            DbElement.Insert(Connection, row);
        }
    }

    private void InsertStructure(DbFhirPackage package)
    {
        TestStructure row = new(package);
        row.Key = DbStructureDefinition.Insert(Connection, row);
    }

    private void InsertValueSet(DbFhirPackage package)
    {
        TestValueSet row = new(package);
        row.Key = DbValueSet.Insert(Connection, row);
    }

    // Test-only subclasses avoid repeating the nullable metadata fields on these minimal definitions.
    private sealed class TestStructure : DbStructureDefinition
    {
        [SetsRequiredMembers]
        internal TestStructure(DbFhirPackage package)
        {
            FhirPackageKey = package.Key;
            Id = "ValueSet";
            Name = "ValueSet";
            Version = package.PackageVersion;
            UnversionedUrl = "http://hl7.org/fhir/StructureDefinition/ValueSet";
            VersionedUrl = UnversionedUrl + "|" + package.PackageVersion;
            Status = PublicationStatus.Active;
            ArtifactClass = FhirArtifactClassEnum.Resource;
        }
    }

    private sealed class TestValueSet : DbValueSet
    {
        [SetsRequiredMembers]
        internal TestValueSet(DbFhirPackage package)
        {
            FhirPackageKey = package.Key;
            Id = "filter-operator";
            Name = "FilterOperator";
            Version = package.PackageVersion;
            UnversionedUrl = FilterValueSet;
            VersionedUrl = UnversionedUrl + "|" + package.PackageVersion;
            Status = PublicationStatus.Active;
        }
    }

    private sealed class TestElement : DbElement
    {
        [SetsRequiredMembers]
        internal TestElement(DbFhirPackage package, int structureKey, string id)
        {
            FhirPackageKey = package.Key;
            StructureKey = structureKey;
            Id = id;
            Path = id;
            Name = id.Split('.')[^1];
            MinCardinality = 1;
            MaxCardinality = 1;
            MaxCardinalityString = "1";
            FullCollatedTypeLiteral = "code";
            DistinctTypeLiterals = "code";
            DistinctTypeCount = 1;
            BindingValueSet = FilterValueSet;
            ValueSetBindingStrength = BindingStrength.Required;
            IsSimpleType = true;
        }
    }

    public void Dispose()
    {
        Connection.Dispose();
        if (Directory.Exists(SourceRoot))
        {
            Directory.Delete(SourceRoot, true);
        }
    }
}
