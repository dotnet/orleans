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
    public class RangeSetBenchmarks
    {
        private readonly IHost host = Program.BuildHost();
        private IDictionaryGrain dictionaryGrain;
        private IConcurrentDictionaryGrain concurrentDictionaryGrain;
        private ImmutableList<LookupItem>[] data;
        private const int ItemCount = 1 << 20;

        [GlobalSetup]
        public void GlobalSetup()
        {
            // prepare workload
            data = Enumerable.Range(0, Items)
                .Select(i => new LookupItem(i, i, DateTime.UtcNow))
                .BatchIEnumerable(BatchSize)
                .Select(_ => _.ToImmutableList())
                .ToArray();

            // start orleans
            host.StartAsync().Wait();

            // grab a proxy to the dictionary grain
            dictionaryGrain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IDictionaryGrain>(Guid.Empty);

            // grab a proxy to the concurrent dictionary
            concurrentDictionaryGrain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            // warm up the dictionary grain
            dictionaryGrain.StartAsync().Wait();

            // warm up the concurrent dictionary grain
            concurrentDictionaryGrain.StartAsync().Wait();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            // deactivate the dictionary grain
            dictionaryGrain.StopAsync().Wait();

            // deactivate the concurrent dictionary grain
            concurrentDictionaryGrain.StopAsync().Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Params(1 << 18, 1 << 17, 1 << 16)]
        public int BatchSize { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void DictionaryGrainRangeSet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => dictionaryGrain.SetRangeAsync(_)));
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void ConcurrentDictionaryRangeSet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => concurrentDictionaryGrain.SetRangeAsync(_)));
            pipeline.Wait();
        }
    }
}