namespace OrleansBenchmarks.Serialization
{
    using BenchmarkDotNet.Configs;
    using BenchmarkDotNet.Diagnostics.Windows;

    public class SerializationBenchmarkConfig : ManualConfig
    {
        public SerializationBenchmarkConfig()
        {
            Add(new MemoryDiagnoser());
        }
    }
}
