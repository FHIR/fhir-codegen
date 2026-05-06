// <copyright file="FileSystemUtilsTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using Shouldly;
using Fhir.CodeGen.Common.Utils;

namespace Fhir.CodeGen.Lib.Tests;

public class FileSystemUtilsTests
{
    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void FindRelativeDir_TildePath_ResolvesUnderUserProfile()
    {
        string leaf = "fhir-codegen-tests-tilde-" + Path.GetRandomFileName();
        string created = Path.Combine(UserProfile, leaf);
        Directory.CreateDirectory(created);

        try
        {
            string result = FileSystemUtils.FindRelativeDir(string.Empty, "~/" + leaf);

            result.ShouldBe(Path.GetFullPath(Path.Combine(UserProfile, leaf)));
        }
        finally
        {
            Directory.Delete(created, recursive: true);
        }
    }

    [Fact]
    public void FindRelativeDir_MissingTildePath_ReturnsEmpty_WhenThrowIfNotFoundFalse()
    {
        string missing = "~/fhir-codegen-tests-missing-" + Path.GetRandomFileName();

        string result = FileSystemUtils.FindRelativeDir(string.Empty, missing, throwIfNotFound: false);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FindRelativeDir_MissingTildePath_Throws_WhenThrowIfNotFoundTrue()
    {
        string missing = "~/fhir-codegen-tests-missing-" + Path.GetRandomFileName();

        DirectoryNotFoundException ex = Should.Throw<DirectoryNotFoundException>(
            () => FileSystemUtils.FindRelativeDir(string.Empty, missing));

        // The error message should retain the ~/ prefix instead of dropping it
        // (the previous implementation produced "Could not find directory <leaf>!").
        ex.Message.ShouldContain("~/");
        ex.Message.ShouldContain(missing);
    }
}
