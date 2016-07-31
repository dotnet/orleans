using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace SerializationBenchmarks
{
    public class SerializerBenchmarkConfig : ManualConfig
    {
        public SerializerBenchmarkConfig()
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
