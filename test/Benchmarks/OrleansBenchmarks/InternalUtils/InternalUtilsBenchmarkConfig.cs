using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace SerializationBenchmarks
{
    public class InternalUtilsBenchmarkConfig : ManualConfig
    {
        public InternalUtilsBenchmarkConfig()
        {
            Add(new Job
            {
                LaunchCount = 1,
                TargetCount = 3,
                WarmupCount = 1
            });
        }
    }
}
