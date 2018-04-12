using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Benchmarks.MapReduce
{
    public class MapReduceBenchmarkConfig : ManualConfig
    {
        public MapReduceBenchmarkConfig()
        {
            Add(new Job
            {
                Run = {
                    LaunchCount = 1,
                    TargetCount = 2,
                    WarmupCount = 0
                }
            });
        }
    }
}
