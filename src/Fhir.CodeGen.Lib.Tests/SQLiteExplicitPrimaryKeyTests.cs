// <copyright file="SQLiteExplicitPrimaryKeyTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json;
using Fhir.CodeGen.Comparison.Models;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

public class SQLiteExplicitPrimaryKeyTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("list")]
    [InlineData("enumerable")]
    public void ExplicitKeysRoundTripThroughDependentRows(string overload)
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        DbUnsafeCodeRule.CreateTable(connection);
        DbUnsafeCodeTrigger.CreateTable(connection);
        List<DbUnsafeCodeRule> rules = [Rule(100), Rule(7), Rule(901)];

        InsertRules(connection, overload, rules, insertPrimaryKey: true);
        List<DbUnsafeCodeTrigger> triggers = rules.Select(rule => new DbUnsafeCodeTrigger
        {
            StructureOutcomeKey = 1,
            SourceFhirPackageKey = 2,
            TargetFhirPackageKey = 3,
            SourceElementId = "ValueSet.compose.include.filter.op",
            TargetElementId = "ValueSet.compose.include.filter.op",
            SourcePathJson = "[]",
            System = rule.System,
            Code = rule.Code,
            IsWildcardVersion = true,
            VersionsJson = "[]",
            EvidenceKeysJson = "[]",
            RuleKeysJson = JsonSerializer.Serialize(new[] { rule.Key }),
        }).ToList();
        DbUnsafeCodeTrigger.Insert(connection, triggers);

        rules.Select(rule => rule.Key).ShouldBe([100, 7, 901]);
        Dictionary<int, DbUnsafeCodeRule> persistedRules = DbUnsafeCodeRule.SelectDict(connection);
        persistedRules.Keys.Order().ShouldBe([7, 100, 901]);
        List<DbUnsafeCodeTrigger> persistedTriggers = DbUnsafeCodeTrigger.SelectList(connection);
        persistedTriggers.Count.ShouldBe(3);
        foreach (DbUnsafeCodeTrigger trigger in persistedTriggers)
        {
            int ruleKey = JsonSerializer.Deserialize<int[]>(trigger.RuleKeysJson)!.ShouldHaveSingleItem();
            persistedRules.ShouldContainKey(ruleKey);
            persistedRules[ruleKey].System.ShouldBe(trigger.System);
            persistedRules[ruleKey].Code.ShouldBe(trigger.Code);
        }
    }

    [Theory]
    [InlineData("single")]
    [InlineData("list")]
    [InlineData("enumerable")]
    public void AutomaticIdentityStillIgnoresSuppliedKeys(string overload)
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        DbUnsafeCodeRule.CreateTable(connection);
        List<DbUnsafeCodeRule> rules = [Rule(100), Rule(7), Rule(901)];
        string[] codes = [.. rules.Select(rule => rule.Code)];

        InsertRules(connection, overload, rules, insertPrimaryKey: false);

        List<DbUnsafeCodeRule> persisted = DbUnsafeCodeRule.SelectList(connection, orderByProperties: ["Key"]);
        persisted.Select(rule => rule.Key).ShouldBe([1, 2, 3]);
        persisted.Select(rule => rule.Code).ShouldBe(codes);
        // The existing automatic enumerable overload does not update input keys.
        rules.Select(rule => rule.Key).ShouldBe(overload == "enumerable" ? [100, 7, 901] : [1, 2, 3]);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("list")]
    [InlineData("enumerable")]
    public void ExplicitKeyDuplicatesHonorIgnoreDuplicates(string overload)
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        DbUnsafeCodeRule.CreateTable(connection);
        InsertRules(connection, overload, [Rule(100)], insertPrimaryKey: true);
        DbUnsafeCodeRule duplicate = Rule(100);
        duplicate.Code = "duplicate";

        SqliteException error = Should.Throw<SqliteException>(() =>
            InsertRules(connection, overload, [duplicate], insertPrimaryKey: true));
        error.SqliteErrorCode.ShouldBe(19);
        DbUnsafeCodeRule.SelectList(connection).ShouldHaveSingleItem().Code.ShouldBe("rule-100");

        InsertRules(connection, overload, [duplicate, Rule(7)], insertPrimaryKey: true, ignoreDuplicates: true);

        List<DbUnsafeCodeRule> persisted = DbUnsafeCodeRule.SelectList(connection, orderByProperties: ["Key"]);
        persisted.Select(rule => rule.Key).ShouldBe([7, 100]);
        persisted.Select(rule => rule.Code).ShouldBe(["rule-7", "rule-100"]);
        duplicate.Key.ShouldBe(100);
    }

    private static DbUnsafeCodeRule Rule(int key) => new()
    {
        Key = key,
        System = "urn:sqlite-explicit-key-test",
        Code = $"rule-{key}",
        NoMapOnly = false,
        ExcludedRelationshipLiteral = "",
        ExcludedVersionLiteral = "",
        SourceRelativeFilename = "input/ig-support/unsafe-code-mappings.json",
        EntryIndex = 0,
    };

    private static void InsertRules(
        SqliteConnection connection,
        string overload,
        List<DbUnsafeCodeRule> rules,
        bool insertPrimaryKey,
        bool ignoreDuplicates = false)
    {
        switch (overload)
        {
            case "single":
                foreach (DbUnsafeCodeRule rule in rules)
                {
                    DbUnsafeCodeRule.Insert(connection, rule, insertPrimaryKey: insertPrimaryKey, ignoreDuplicates: ignoreDuplicates)
                        .ShouldBe(rule.Key);
                }
                break;
            case "list":
                DbUnsafeCodeRule.Insert(connection, rules, insertPrimaryKey: insertPrimaryKey, ignoreDuplicates: ignoreDuplicates);
                break;
            case "enumerable":
                DbUnsafeCodeRule.Insert(connection, (IEnumerable<DbUnsafeCodeRule>)rules,
                    insertPrimaryKey: insertPrimaryKey, ignoreDuplicates: ignoreDuplicates);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(overload));
        }
    }
}
