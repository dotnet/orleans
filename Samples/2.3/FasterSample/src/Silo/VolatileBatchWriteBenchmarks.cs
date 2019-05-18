using System;
using System.Collections.Generic;
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
    [ShortRunJob, MarkdownExporter, RunOncePerIteration]
    [GcServer(true), GcConcurrent(true)]
    public class VolatileBatchWriteBenchmarks
    {
        private const int ItemCount = 1 << 21;

        private readonly IHost host = Program.BuildHost();
        private IConcurrentDictionaryGrain concurrentGrain;
        private IFasterGrain fasterGrain;
        private IEnumerable<ImmutableList<LookupItem>> generator;

        [GlobalSetup]
        public void GlobalSetup()
        {
            generator = Enumerable.Range(0, Items)
                .Select(i => new LookupItem(i, i, DateTime.UtcNow))
                .BatchIEnumerable(BatchSize)
                .Select(_ => _.ToImmutableList());

            host.StartAsync().Wait();

            concurrentGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);

            fasterGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterGrain>(Guid.Empty);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            concurrentGrain.StartAsync().Wait();
            fasterGrain.TryGetAsync(0).Wait();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            concurrentGrain.StopAsync().Wait();
            fasterGrain.StopAsync().Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1 << 10, 1 << 11, 1 << 12, 1 << 13, 1 << 14)]
        public int BatchSize { get; set; }

        [Params(2, 4, 8, 16, 32, 64, 128)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount, Baseline = true)]
        public void ConcurrentDictionary()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(generator.Select(_ => concurrentGrain.SetRangeAsync(_)));
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void Faster()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(generator.Select(_ => fasterGrain.SetRangeAsync(_)));
            pipeline.Wait();
        }
    }
}