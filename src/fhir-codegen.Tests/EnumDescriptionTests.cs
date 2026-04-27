// <copyright file="EnumDescriptionTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.CommandLine;
using fhir_codegen_shared;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace fhir_codegen.Tests;

public class EnumDescriptionTests
{
    private static IConfiguration BuildEnvConfig() => new ConfigurationBuilder().Build();

    private static Option FindLoadStructures()
    {
        RootCommand root = LaunchUtils.BuildCommand(BuildEnvConfig());
        return root.Options.First(o => o.Name == "--load-structures");
    }

    [Fact]
    public void EnumOption_DescriptionContainsAllowedValues()
    {
        Option opt = FindLoadStructures();
        opt.Description.ShouldNotBeNull();
        opt.Description!.ShouldContain("Allowed values:");
        opt.Description.ShouldContain("CapabilityStatement");
        opt.Description.ShouldContain("ValueSet");
    }

    [Fact]
    public void EnumOption_DescriptionAugmentationIsIdempotent()
    {
        // First build performed by FindLoadStructures.
        Option opt = FindLoadStructures();
        string? first = opt.Description;

        // Build the command tree again; same static Option<T> instance must
        // not get a second 'Allowed values:' clause appended.
        _ = LaunchUtils.BuildCommand(BuildEnvConfig());

        opt.Description.ShouldBe(first);

        int occurrences = (opt.Description!.Length - opt.Description.Replace("Allowed values:", "", StringComparison.Ordinal).Length) / "Allowed values:".Length;
        occurrences.ShouldBe(1);
    }
}
