using Orleans.Dashboard.Model;
using System;
using System.Collections.Generic;

namespace Benchmarks.Dashboard
{
    internal class Helper
    {
        public static IEnumerable<TestTraces> CreateTraces(DateTime time, int siloCount, int grainCount, int methodCount)
        {
            for (var siloIndex = 0; siloIndex < siloCount; siloIndex++)
            {
                var trace = new List<SiloGrainTraceEntry>();

                for (var grainIndex = 0; grainIndex < grainCount; grainIndex++)
                {
                    for (var grainMethodIndex = 0; grainMethodIndex < methodCount; grainMethodIndex++)
                    {
                        trace.Add(new SiloGrainTraceEntry
                        {
                            ElapsedTime = 10,
                            Count = 100,
                            Method = $"METHOD_{grainMethodIndex}",
                            Grain = $"GRAIN_{grainIndex}",
                            ExceptionCount = 0
                        });
                    }
                }

                yield return new TestTraces(time, $"SILO_{siloIndex}", trace.ToArray());
            }
        }
    }
}
