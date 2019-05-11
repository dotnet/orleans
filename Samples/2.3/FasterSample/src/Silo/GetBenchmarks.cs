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
    public class GetBenchmarks
    {
        private readonly IHost host = Program.BuildHost();
        private IDictionaryLookupGrain dictionaryGrain;
        private IConcurrentDictionaryLookupGrain concurrentDictionaryGrain;
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
            host.StartAsync().Wait();

            // grab a proxy to the dictionary grain
            dictionaryGrain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IDictionaryLookupGrain>(Guid.Empty);

            // preload the dictionary grain
            dictionaryGrain.SetRangeAsync(values).Wait();

            // grab a proxy to the concurrent dictionary grain
            concurrentDictionaryGrain = host.Services.GetService<IGrainFactory>().GetGrain<IConcurrentDictionaryLookupGrain>(Guid.Empty);

            // preload the dictionary grain
            concurrentDictionaryGrain.SetRangeAsync(values).Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = Items)]
        public void DictionaryGrainGet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(x => dictionaryGrain.TryGetAsync(x)));
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = Items)]
        public void ConcurrentDictionaryGrainGet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(x => concurrentDictionaryGrain.TryGetAsync(x)));
            pipeline.Wait();
        }
    }
}