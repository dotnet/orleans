using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Grains;
using Grains.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Silo
{
    [RunOncePerIteration, AllStatisticsColumn, MarkdownExporter]
    [GcServer(true), GcConcurrent(true)]
    public class GetBenchmarks
    {
        private const int ItemCount = 1 << 20;

        private readonly IHost host = Program.BuildHost();
        private IConcurrentDictionaryGrain dictionaryGrain;
        private IFasterThreadPoolGrain fasterThreadPoolGrain;
        private IFasterDedicatedGrain fasterDedicatedGrain;
        private IEnumerable<int> generator;

        [GlobalSetup]
        public void GlobalSetup()
        {
            host.StartAsync().Wait();

            dictionaryGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.NewGuid());

            fasterThreadPoolGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterThreadPoolGrain>(Guid.NewGuid());

            fasterDedicatedGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterDedicatedGrain>(Guid.NewGuid());

            // start the grains
            fasterThreadPoolGrain.StartAsync(HashBuckets, MemorySizeBits).Wait();
            fasterDedicatedGrain.StartAsync(HashBuckets, MemorySizeBits).Wait();
            dictionaryGrain.StartAsync().Wait();

            // pre-load grains
            var data = Enumerable.Range(0, Items).Select(i => new LookupItem(i, i, new DateTime(i))).ToImmutableList();
            dictionaryGrain.SetRangeAsync(data).Wait();
            fasterThreadPoolGrain.SetRangeAsync(data).Wait();
            fasterDedicatedGrain.SetRangeAsync(data).Wait();

            generator = Enumerable.Range(0, Items);
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1 << 20)]
        public int HashBuckets { get; set; }

        [Params(29)]
        public int MemorySizeBits { get; set; }

        [Params(4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount, Baseline = true)]
        public async Task ConcurrentDictionary()
        {
            var pipeline = new MyAsyncPipeline(Concurrency);
            foreach (var key in generator)
            {
                await pipeline.WaitOneAsync();
                await pipeline.Add(dictionaryGrain.TryGetAsync(key));
            }
            await pipeline.WaitAllAsync();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public async Task FasterOnThreadPool()
        {
            var pipeline = new MyAsyncPipeline(Concurrency);
            foreach (var key in generator)
            {
                await pipeline.WaitOneAsync();
                await pipeline.Add(fasterThreadPoolGrain.TryGetAsync(key));
            }
            await pipeline.WaitAllAsync();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public async Task FasterOnDedicatedThreads()
        {
            var pipeline = new MyAsyncPipeline(Concurrency);
            foreach (var key in generator)
            {
                await pipeline.WaitOneAsync();
                await pipeline.Add(fasterDedicatedGrain.TryGetAsync(key));
            }
            await pipeline.WaitAllAsync();
        }
    }
}