using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows;

namespace Benchmarks.Serialization
{
    public class SerializationBenchmarkConfig : ManualConfig
    {
        public SerializationBenchmarkConfig()
        {
            Add(new MemoryDiagnoser());
        }
    }
}
