using System;
using System.Collections.Immutable;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Grains;
using Grains.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Runtime;

namespace Silo
{
    [ShortRunJob, EvaluateOverhead(false), AllStatisticsColumn, MarkdownExporter, RunOncePerIteration]
    [GcServer(true), GcConcurrent(true)]
    public class ConcurrentDictionaryGetBenchmarks
    {
        private IHost host;
        private IConcurrentDictionaryGrain grain;
        private int[] data;
        private const int Items = 1 << 13;

        [GlobalSetup]
        public void GlobalSetup()
        {
            // prepare the lookup workload
            data = Enumerable.Range(0, Items).ToArray();

            // prepare preloaded data
            var values = Enumerable.Range(0, Items)
                .Select(x => new LookupItem(x, x, DateTime.UtcNow))
                .ToImmutableList();

            // startup orleans
            host = Program.StartNewHost();

            // grab a proxy to the concurrent dictionary grain
            grain = host.Services.GetService<IGrainFactory>().GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);

            // preload the dictionary grain
            grain.SetRangeAsync(values).Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = Items)]
        public void Benchmarks()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(x => grain.TryGetAsync(x)));
            pipeline.Wait();
        }
    }
}