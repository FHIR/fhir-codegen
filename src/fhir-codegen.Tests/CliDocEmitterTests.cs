// <copyright file="CliDocEmitterTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text;
using Fhir.CodeGen.Lib.Language;
using fhir_codegen_shared;
using Shouldly;
using Xunit;

namespace fhir_codegen.Tests;

public class CliDocEmitterTests
{
    [Fact]
    public void EmitMarkdown_ContainsAllRegisteredLanguages()
    {
        string markdown = CliDocEmitter.EmitMarkdown();

        foreach (ILanguage language in LanguageManager.GetLanguages())
        {
            string expectedHeading = $"### generate {language.Name}";
            markdown.ShouldContain(expectedHeading);
        }
    }

    [Fact]
    public void EmitMarkdown_ContainsAllEnabledCommands()
    {
        string markdown = CliDocEmitter.EmitMarkdown();

        foreach (LaunchUtils.LaunchCommandRecord rec in LaunchUtils.EnabledCommands)
        {
            markdown.ShouldContain($"## {rec.Literal} ");
        }

        // sql is Disabled = true and must not appear.
        markdown.ShouldNotContain("## sql ");
    }

    [Fact]
    public void EmitMarkdown_RendersOptionsForGenerateTypeScript()
    {
        string markdown = CliDocEmitter.EmitMarkdown();

        // Pin down well-known TypeScript options to catch drift between the
        // emitter and the live BuildCliOptions output.
        markdown.ShouldContain("--namespace");
        markdown.ShouldContain("--min-ts-version");
        markdown.ShouldContain("--inline-enums");
    }

    [Fact]
    public async Task WriteToFileAsync_WritesUtf8NoBomWithTrailingNewline()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"fhir-codegen-cli-doc-{Guid.NewGuid():N}.md");

        try
        {
            int code = await CliDocEmitter.WriteToFileAsync(tempPath);
            code.ShouldBe(0);

            File.Exists(tempPath).ShouldBeTrue();

            byte[] bytes = await File.ReadAllBytesAsync(tempPath);
            bytes.Length.ShouldBeGreaterThan(0);

            // No UTF-8 BOM (EF BB BF).
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            hasBom.ShouldBeFalse();

            // Trailing newline.
            bytes[^1].ShouldBe((byte)'\n');

            // Content round-trips as UTF-8.
            string roundTripped = Encoding.UTF8.GetString(bytes);
            roundTripped.ShouldContain("# Command Line Usage");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
