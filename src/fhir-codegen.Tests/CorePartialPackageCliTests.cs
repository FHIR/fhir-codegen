// <copyright file="CorePartialPackageCliTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;
using Xunit;

namespace fhir_codegen.Tests;

/// <summary>
/// Regression coverage for FHIR core-partial directives (e.g., <c>hl7.fhir.r4</c>), which used to
/// send the package loader into unbounded self-recursion. The crash is an uncatchable stack
/// overflow, so these run the CLI in a child process rather than in the test host.
/// </summary>
public class CorePartialPackageCliTests : IDisposable
{
    /// <summary>(Immutable) Windows STATUS_STACK_OVERFLOW (0xC00000FD), the exit code the runtime
    /// fast-fails with when the loader recursion is unbounded.</summary>
    private const int _statusStackOverflow = -1073741571;

    private const int _cliTimeoutMs = 120_000;

    private readonly string _tempDir;

    public CorePartialPackageCliTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "fhir-codegen-tests",
            "core-partial-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; do not mask the test outcome.
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("hl7.fhir.r2")]
    [InlineData("hl7.fhir.r3")]
    [InlineData("hl7.fhir.r4")]
    [InlineData("hl7.fhir.r4b")]
    [InlineData("hl7.fhir.r5")]
    [InlineData("hl7.fhir.r6")]
    public void CorePartialDirectiveTerminates(string directive)
    {
        (bool exited, int exitCode, string output) = RunCli(
            "generate",
            "Info",
            "-p",
            directive,
            "--use-official-registries",
            "false",
            "--fhir-cache",
            _tempDir,
            "--output-path",
            _tempDir);

        exited.ShouldBeTrue($"the CLI did not terminate for '{directive}'");
        exitCode.ShouldNotBe(_statusStackOverflow, $"the CLI stack-overflowed for '{directive}'");
        output.ShouldContain($"Failed to install package {directive}");
        output.ShouldNotContain(".expansions");
    }

    [Fact]
    public void CoreFullDirectiveStillAutoLoadsExpansions()
    {
        (bool exited, int exitCode, string output) = RunCli(
            "generate",
            "Info",
            "-p",
            "hl7.fhir.r4.core",
            "--use-official-registries",
            "false",
            "--fhir-cache",
            _tempDir,
            "--output-path",
            _tempDir);

        exited.ShouldBeTrue("the CLI did not terminate for 'hl7.fhir.r4.core'");
        exitCode.ShouldNotBe(_statusStackOverflow, "the CLI stack-overflowed for 'hl7.fhir.r4.core'");
        output.ShouldContain("hl7.fhir.r4.expansions@latest");

        int autoLoadCount = CountLinesContaining(output, "Auto-loading core expansion");
        autoLoadCount.ShouldBe(1, $"expected exactly one auto-expansion request, saw {autoLoadCount}");
    }

    /// <summary>Counts emissions, not substring hits: the logger's message template already carries
    /// the "Auto-loading core expansion" prefix that the call site repeats, so a single emitted line
    /// contains the marker twice.</summary>
    private static int CountLinesContaining(string haystack, string needle)
    {
        int count = 0;

        foreach (string line in haystack.Split('\n'))
        {
            if (line.Contains(needle, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static string ResolveCliExecutable()
    {
        string? cliDirectory = typeof(CorePartialPackageCliTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "FhirCodeGenCliDirectory")
            ?.Value;

        cliDirectory.ShouldNotBeNullOrEmpty("the FhirCodeGenCliDirectory assembly metadata was not injected by the build");

        string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "fhir-codegen.exe"
            : "fhir-codegen";

        string executablePath = Path.Combine(cliDirectory!, executableName);

        File.Exists(executablePath).ShouldBeTrue($"the fhir-codegen CLI was not found at '{executablePath}'");

        return executablePath;
    }

    private static (bool Exited, int ExitCode, string Output) RunCli(params string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ResolveCliExecutable(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        StringBuilder sink = new();

        using Process process = new() { StartInfo = startInfo };

        // both streams must drain asynchronously: on the crashing case the runtime's stack-overflow
        // dump fills stderr while a synchronous stdout read is still blocked, and the pair deadlocks
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (sink)
                {
                    sink.AppendLine(e.Data);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (sink)
                {
                    sink.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(_cliTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // the process may have exited between the wait and the kill
            }

            lock (sink)
            {
                return (false, 0, sink.ToString());
            }
        }

        // the timed overload does not wait for the asynchronous stream callbacks; this one does
        process.WaitForExit();

        lock (sink)
        {
            return (true, process.ExitCode, sink.ToString());
        }
    }
}
