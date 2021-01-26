using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Benchmarks.MapReduce
{
    public class MapReduceBenchmarkConfig : ManualConfig
    {
        public MapReduceBenchmarkConfig()
        {
            AddJob(new Job
            {
                Run = {
                    LaunchCount = 1,
                    IterationCount = 2,
                    WarmupCount = 0
                }
            });
        }
    }
}
