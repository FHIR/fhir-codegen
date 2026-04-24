// <copyright file="ICodeGenConfig.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Fhir.CodeGen.Lib.Configuration;

public interface ICodeGenConfig
{
    /// <summary>Gets options for controlling the operation.</summary>
    ConfigurationOption[] GetOptions();

    /// <summary>Parses the given parse result.</summary>
    /// <param name="parseResult">The parse result.</param>
    void Parse(System.CommandLine.ParseResult parseResult);

    /// <summary>Gets the initial command passed at launch.</summary>
    string? LaunchCommand { get; }

    /// <summary>Gets the logger factory instance.</summary>
    /// <value>The logger factory instance used for logging.</value>
    ILoggerFactory LogFactory { get; set; }
}
