using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace OrleansBenchmarks.MapReduce
{
    public class MapReduceBenchmarkConfig : ManualConfig
    {
        public MapReduceBenchmarkConfig()
        {
            Add(new Job
            {
                LaunchCount = 1,
                TargetCount = 2,
                WarmupCount = 0
            });
        }
    }
}
