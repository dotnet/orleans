using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using System;

namespace Benchmarks.Utilities
{
    internal class BenchmarkConfig : ManualConfig
    {
        public BenchmarkConfig()
        {
            ArtifactsPath = ".\\BenchmarkDotNet.Aritfacts." + DateTime.Now.ToString("u").Replace(' ', '_').Replace(':', '-');
            AddExporter(MarkdownExporter.GitHub);
            AddDiagnoser(MemoryDiagnoser.Default);
            Options |= ConfigOptions.KeepBenchmarkFiles;
        }
    }
}