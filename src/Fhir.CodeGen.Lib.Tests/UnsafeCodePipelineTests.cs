// <copyright file="UnsafeCodePipelineTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Comparison.XVer;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

public class UnsafeCodePipelineTests
{
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
