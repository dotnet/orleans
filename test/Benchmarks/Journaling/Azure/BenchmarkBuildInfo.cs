using System.Reflection;
using System.Text.Json;
using Orleans.Journaling;

namespace Benchmarks.Journaling.Azure;

internal sealed record BenchmarkAssemblyBuild(string Name, string? SourceRevision, Guid ModuleVersionId)
{
    public static BenchmarkAssemblyBuild FromAssembly(Assembly assembly)
        => new(
            assembly.GetName().Name!,
            ParseSourceRevision(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion),
            assembly.ManifestModule.ModuleVersionId);

    internal static string? ParseSourceRevision(string? informationalVersion)
    {
        if (informationalVersion is null)
        {
            return null;
        }

        var separator = informationalVersion.LastIndexOf('+');
        if (separator < 0)
        {
            return null;
        }

        var revision = informationalVersion[(separator + 1)..];
        return revision.Length is 40 or 64 && revision.All(Uri.IsHexDigit) ? revision.ToLowerInvariant() : null;
    }
}

internal sealed record BenchmarkBuildInfo(
    BenchmarkAssemblyBuild Benchmark,
    BenchmarkAssemblyBuild Journaling,
    BenchmarkAssemblyBuild AzureStorage)
{
    public static BenchmarkBuildInfo Current { get; } = new(
        BenchmarkAssemblyBuild.FromAssembly(typeof(BenchmarkBuildInfo).Assembly),
        BenchmarkAssemblyBuild.FromAssembly(typeof(IJournalStorage).Assembly),
        BenchmarkAssemblyBuild.FromAssembly(typeof(AzureBlobJournalStorageOptions).Assembly));

    public static void WriteTo(TextWriter writer)
        => writer.WriteLine($"Azure benchmark build: {JsonSerializer.Serialize(Current)}");
}
