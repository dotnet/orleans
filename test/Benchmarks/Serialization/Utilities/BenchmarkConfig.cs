using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace Benchmarks.Serialization.Utilities;

internal class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        ArtifactsPath = ".\\BenchmarkDotNet.Aritfacts." + DateTime.Now.ToString("u").Replace(' ', '_').Replace(':', '-');
        AddExporter(MarkdownExporter.GitHub);
        AddDiagnoser(MemoryDiagnoser.Default);
        Options |= ConfigOptions.KeepBenchmarkFiles;
        AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0));
    }
}
