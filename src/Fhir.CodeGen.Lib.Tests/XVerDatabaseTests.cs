// <copyright file="XVerDatabaseTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Reflection;
using System.Text;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fhir.CodeGen.Lib.Tests;

public class XVerDatabaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "fhir-codegen-tests", "xver-db-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> _dbFiles = [];
    private readonly List<XVerProcessor> _processors = [];

    public XVerDatabaseTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (XVerProcessor processor in _processors)
        {
            ComparisonDatabase? database = GetDatabase(processor);
            ((IDisposable?)database)?.Dispose();
        }

        foreach (string dbFile in _dbFiles)
        {
            ClearDatabasePools(dbFile);
        }

        Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DefaultPipelineInitializesMissingNamedDatabaseBeforeLoadingMaps()
    {
        string dbFile = GetDatabaseFile("new-parent");
        XVerProcessor processor = CreateProcessor(dbFile, [CreateDefinitions()]);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => processor.ProcessCommand(null));

        error.Message.ShouldContain("Failed to load cross-version maps");
        error.Message.ShouldContain(Path.Combine(_tempDir, "missing-maps"));
        File.Exists(dbFile).ShouldBeTrue();
        AssertInitialized(dbFile);
        GetDatabase(processor).ShouldNotBeNull();
    }

    [Fact]
    public void ValidNamedDatabaseIsReusedWithoutLoadingDefinitionsOrReplacingMetadata()
    {
        string dbFile = GetDatabaseFile();
        using (ComparisonDatabase seed = new(
            [CreateDefinitions()], Path.GetDirectoryName(dbFile)!, Path.GetFileName(dbFile), NullLoggerFactory.Instance))
        {
            seed.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        }

        using (SqliteConnection connection = OpenConnection(dbFile, SqliteOpenMode.ReadWrite))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE {DbFhirPackage.DefaultTableName} SET Name = 'persisted-name'";
            command.ExecuteNonQuery().ShouldBe(1);
        }

        DbFhirPackage before = ReadPackage(dbFile);
        XVerProcessor processor = CreateProcessor(dbFile);
        processor.LoadDatabase(false);
        GetDatabase(processor).ShouldNotBeNull();

        XVerProcessor defaultProcessor = CreateProcessor(dbFile);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => defaultProcessor.ProcessCommand(null));
        error.Message.ShouldContain("Failed to load cross-version maps");

        DbFhirPackage after = ReadPackage(dbFile);
        after.Key.ShouldBe(before.Key);
        after.Name.ShouldBe("persisted-name");
        after.PackageId.ShouldBe(before.PackageId);
        after.PackageVersion.ShouldBe(before.PackageVersion);
        after.CanonicalUrl.ShouldBe(before.CanonicalUrl);
        after.DefinitionFhirSequence.ShouldBe(before.DefinitionFhirSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingNamedDatabaseIsRejectedWithoutCreatingFileOrParent(bool missingParent)
    {
        string dbFile = GetDatabaseFile(missingParent ? "missing-parent" : null);
        FileNotFoundException directError = Should.Throw<FileNotFoundException>(() =>
            new ComparisonDatabase(Path.GetDirectoryName(dbFile)!, Path.GetFileName(dbFile), NullLoggerFactory.Instance));
        directError.FileName.ShouldBe(dbFile);
        AssertRecoveryGuidance(directError, dbFile);

        XVerProcessor processor = CreateProcessor(dbFile, [CreateDefinitions()]);
        FileNotFoundException loadError = Should.Throw<FileNotFoundException>(() => processor.LoadDatabase(false));
        loadError.FileName.ShouldBe(dbFile);
        AssertRecoveryGuidance(loadError, dbFile);

        FileNotFoundException commandError = Should.Throw<FileNotFoundException>(() => processor.ProcessCommand("compare-vs"));
        AssertRecoveryGuidance(commandError, dbFile);
        GetDatabase(processor).ShouldBeNull();
        File.Exists(dbFile).ShouldBeFalse();
        if (missingParent)
        {
            Directory.Exists(Path.GetDirectoryName(dbFile)).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingDatabaseWithoutPackageTableIsRejectedAndPreserved(bool unrelatedTable)
    {
        string dbFile = GetDatabaseFile();
        if (unrelatedTable)
        {
            using SqliteConnection connection = OpenConnection(dbFile, SqliteOpenMode.ReadWriteCreate);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE UserData (Value TEXT); INSERT INTO UserData VALUES ('keep-me')";
            command.ExecuteNonQuery();
        }
        else
        {
            File.WriteAllBytes(dbFile, []);
        }

        ClearDatabasePools(dbFile);
        byte[] original = File.ReadAllBytes(dbFile);
        InvalidDataException directError = Should.Throw<InvalidDataException>(() =>
            new ComparisonDatabase(Path.GetDirectoryName(dbFile)!, Path.GetFileName(dbFile), NullLoggerFactory.Instance));
        AssertRecoveryGuidance(directError, dbFile);
        directError.Message.ShouldContain(DbFhirPackage.DefaultTableName);

        XVerProcessor processor = CreateProcessor(dbFile, [CreateDefinitions()]);
        InvalidDataException loadError = Should.Throw<InvalidDataException>(() => processor.LoadDatabase(false));
        AssertRecoveryGuidance(loadError, dbFile);
        InvalidDataException commandError = Should.Throw<InvalidDataException>(() => processor.ProcessCommand(null));
        AssertRecoveryGuidance(commandError, dbFile);
        GetDatabase(processor).ShouldBeNull();

        ClearDatabasePools(dbFile);
        using (FileStream exclusive = File.Open(dbFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            exclusive.Length.ShouldBe(original.Length);
        }
        File.ReadAllBytes(dbFile).ShouldBe(original);

        using SqliteConnection preserved = OpenConnection(dbFile, SqliteOpenMode.ReadWrite);
        using SqliteCommand check = preserved.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{DbFhirPackage.DefaultTableName}'";
        Convert.ToInt64(check.ExecuteScalar()).ShouldBe(0);
        if (unrelatedTable)
        {
            check.CommandText = "SELECT Value FROM UserData";
            check.ExecuteScalar().ShouldBe("keep-me");
        }
    }

    [Fact]
    public void UnrelatedSqliteErrorsAreNotReplacedBySchemaGuidance()
    {
        string dbFile = GetDatabaseFile();
        byte[] original = Encoding.UTF8.GetBytes("not a SQLite database");
        File.WriteAllBytes(dbFile, original);

        SqliteException error = Should.Throw<SqliteException>(() =>
            new ComparisonDatabase(Path.GetDirectoryName(dbFile)!, Path.GetFileName(dbFile), NullLoggerFactory.Instance));

        error.SqliteErrorCode.ShouldBe(26);
        ClearDatabasePools(dbFile);
        using (FileStream exclusive = File.Open(dbFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            exclusive.Length.ShouldBe(original.Length);
        }
        File.ReadAllBytes(dbFile).ShouldBe(original);
    }

    [Fact]
    public void ForceCreateInitializesExistingEmptyNamedDatabase()
    {
        string dbFile = GetDatabaseFile();
        File.WriteAllBytes(dbFile, []);
        XVerProcessor processor = CreateProcessor(dbFile, [CreateDefinitions()]);

        processor.LoadDatabase(true);

        AssertInitialized(dbFile);
        GetDatabase(processor).ShouldNotBeNull();
    }

    private string GetDatabaseFile(string? parent = null)
    {
        string dbFile = Path.Combine(_tempDir, parent ?? string.Empty, Guid.NewGuid().ToString("N") + ".sqlite");
        _dbFiles.Add(dbFile);
        return dbFile;
    }

    private XVerProcessor CreateProcessor(string dbFile, DefinitionCollection[]? definitions = null)
    {
        XVerProcessor processor = new(new ConfigXVer
        {
            CrossVersionDbPath = dbFile,
            CrossVersionMapSourcePath = Path.Combine(_tempDir, "missing-maps"),
            // This directive fails validation before package acquisition if reuse ever loads definitions.
            ComparePackages = ["hl7.fhir.r4.core"],
            LogFactory = NullLoggerFactory.Instance,
        });
        if (definitions is not null)
        {
            typeof(XVerProcessor).GetField("_definitions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(processor, definitions);
        }
        _processors.Add(processor);
        return processor;
    }

    private static DefinitionCollection CreateDefinitions() => new()
    {
        Logger = NullLogger.Instance,
        Name = "R4 test definitions",
        FhirVersion = Hl7.Fhir.Model.FHIRVersion.N4_0_1,
        FhirVersionLiteral = "4.0.1",
        FhirSequence = FhirReleases.FhirSequenceCodes.R4,
        MainPackageId = "hl7.fhir.r4.core",
        MainPackageVersion = "4.0.1",
        MainPackageCanonical = "http://hl7.org/fhir",
    };

    private static ComparisonDatabase? GetDatabase(XVerProcessor processor) =>
        (ComparisonDatabase?)typeof(XVerProcessor).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(processor);

    private static SqliteConnection OpenConnection(string dbFile, SqliteOpenMode mode)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = mode,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ClearDatabasePools(string dbFile)
    {
        SqliteOpenMode[] modes = [SqliteOpenMode.ReadWrite, SqliteOpenMode.ReadWriteCreate];
        foreach (SqliteOpenMode mode in modes)
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = dbFile,
                Mode = mode,
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
    }

    private static DbFhirPackage ReadPackage(string dbFile)
    {
        using SqliteConnection connection = OpenConnection(dbFile, SqliteOpenMode.ReadWrite);
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(connection);
        packages.Count.ShouldBe(1);
        return packages[0];
    }

    private static void AssertInitialized(string dbFile)
    {
        using SqliteConnection connection = OpenConnection(dbFile, SqliteOpenMode.ReadWrite);
        string[] tables = [
            DbFhirPackage.DefaultTableName,
            DbCodeSystem.DefaultTableName,
            DbValueSet.DefaultTableName,
            DbStructureDefinition.DefaultTableName,
            DbElement.DefaultTableName,
            DbElementType.DefaultTableName,
        ];
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName";
            command.Parameters.AddWithValue("$tableName", table);
            Convert.ToInt64(command.ExecuteScalar()).ShouldBe(1, $"missing content table {table}");
        }

        DbFhirPackage package = ReadPackage(dbFile);
        package.Name.ShouldBe("R4 test definitions");
        package.PackageId.ShouldBe("hl7.fhir.r4.core");
        package.PackageVersion.ShouldBe("4.0.1");
        package.CanonicalUrl.ShouldBe("http://hl7.org/fhir");
        package.DefinitionFhirSequence.ShouldBe(FhirReleases.FhirSequenceCodes.R4);
    }

    private static void AssertRecoveryGuidance(Exception error, string dbFile)
    {
        error.Message.ShouldContain(dbFile);
        error.Message.ShouldContain("xver load");
        error.Message.ShouldContain("--reload-db true");
        error.Message.ShouldContain("destructive");
    }
}
