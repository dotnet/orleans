using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace SerializationBenchmarks.MapReduce
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
