// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace PackageJsonGenerator;

/// <summary>
/// JSON manifest for batch-processing multiple packages in a single tool invocation.
/// </summary>
internal sealed class BatchManifest
{
    [JsonPropertyName("packages")]
    public List<BatchPackageEntry> Packages { get; set; } = [];
}

/// <summary>
/// A single package entry within a <see cref="BatchManifest"/>.
/// </summary>
internal sealed class BatchPackageEntry
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("references")]
    public string[] References { get; set; } = [];

    [JsonPropertyName("output")]
    public string Output { get; set; } = "";

    [JsonPropertyName("packageVersion")]
    public string? PackageVersion { get; set; }

    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }

    [JsonPropertyName("sourceRepo")]
    public string? SourceRepo { get; set; }

    [JsonPropertyName("sourceCommit")]
    public string? SourceCommit { get; set; }

    [JsonPropertyName("sourceRoot")]
    public string? SourceRoot { get; set; }

    [JsonPropertyName("sourceFileManifest")]
    public string? SourceFileManifest { get; set; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; set; }
}
