// <copyright file="FileSystemUtils.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;


#if NETSTANDARD2_0
using Fhir.CodeGen.Common.Polyfill;
#endif

namespace Fhir.CodeGen.Common.Utils;

public abstract class FileSystemUtils
{
    /// <summary>Searches for the FHIR specification directory.</summary>
    /// <exception cref="DirectoryNotFoundException">Thrown when the requested directory is not
    ///  present.</exception>
    /// <param name="startDir">       The start directory.</param>
    /// <param name="dirName">        The name of the directory we are searching for.</param>
    /// <param name="throwIfNotFound">(Optional) True to throw if not found.</param>
    /// <returns>The found FHIR directory.</returns>
    public static string FindRelativeDir(
        string startDir,
        string dirName,
        bool throwIfNotFound = true)
    {
        if (Path.IsPathRooted(dirName) || Path.IsPathFullyQualified(dirName))
        {
            if (Directory.Exists(dirName))
            {
                return Path.GetFullPath(dirName);
            }

            return dirName;
        }

        if (dirName.StartsWith('~'))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
            {
                profile = Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".";
            }

            string relative = dirName.Length >= 2 && (dirName[1] == '/' || dirName[1] == '\\')
                ? dirName.Substring(2)
                : dirName.Substring(1);

            string expanded = string.IsNullOrEmpty(relative)
                ? profile
                : Path.GetFullPath(Path.Combine(profile, relative));

            if (Directory.Exists(expanded))
            {
                return expanded;
            }

            if (throwIfNotFound)
            {
                throw new DirectoryNotFoundException($"Could not find directory {dirName}!");
            }

            return string.Empty;
        }

        string currentDir = startDir switch
        {
            null => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "" => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "." => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "./" => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "~" => Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty,
            "~/" => Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty,
            _ => startDir,
        };

        if (currentDir.StartsWith('~'))
        {
            currentDir = Path.GetDirectoryName(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".",
                currentDir[2..]))
                ?? ".";
        }

        string testDir = Path.Combine(currentDir, dirName);

        while (!Directory.Exists(testDir))
        {
            currentDir = Path.GetFullPath(Path.Combine(currentDir, ".."));

            if (currentDir == Path.GetPathRoot(currentDir))
            {
                if (throwIfNotFound)
                {
                    throw new DirectoryNotFoundException($"Could not find directory {dirName}!");
                }

                return string.Empty;
            }

            testDir = Path.Combine(currentDir, dirName);
        }

        return Path.GetFullPath(testDir);
    }

    public static string FindRelativeFile(
        string startDir,
        string fileName,
        bool throwIfNotFound = true)
    {
        if (Path.IsPathRooted(fileName) || Path.IsPathFullyQualified(fileName))
        {
            if (File.Exists(fileName))
            {
                return Path.GetFullPath(fileName);
            }

            return fileName;
        }

        string currentDir = startDir switch
        {
            null => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "" => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "." => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "./" => Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty,
            "~" => Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty,
            "~/" => Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty,
            _ => startDir,
        };

        if (currentDir.StartsWith('~'))
        {
            currentDir = Path.GetDirectoryName(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".",
                currentDir[2..]))
                ?? ".";
        }

        string testFile = Path.Combine(currentDir, fileName);

        while (!File.Exists(testFile))
        {
            currentDir = Path.GetFullPath(Path.Combine(currentDir, ".."));
            if (currentDir == Path.GetPathRoot(currentDir))
            {
                if (throwIfNotFound)
                {
                    throw new FileNotFoundException($"Could not find file {fileName}!");
                }
                return string.Empty;
            }
            testFile = Path.Combine(currentDir, fileName);
        }

        return Path.GetFullPath(testFile);
    }

    public static string GenerateSha256(Stream stream)
    {
        if (stream.Position != 0)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        // Compute the SHA256 hash
        using (System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(stream);

            // Convert the hash to a hexadecimal string
            StringBuilder hashString = new StringBuilder();
            foreach (byte b in hash)
            {
                hashString.Append(b.ToString("x2"));
            }

            return hashString.ToString();
        }
    }
}
