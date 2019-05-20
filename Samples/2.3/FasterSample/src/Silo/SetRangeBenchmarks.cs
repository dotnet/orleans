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
using Orleans.Runtime;

namespace Silo
{
    [RunOncePerIteration, MemoryDiagnoser, MarkdownExporter]
    [GcServer(true), GcConcurrent(true)]
    public class SetRangeBenchmarks
    {
        private const int ItemCount = 1 << 20;

        private readonly IHost host = Program.BuildHost();
        private IConcurrentDictionaryGrain dictionaryGrain;
        private IFasterThreadPoolGrain fasterThreadPoolGrain;
        private IFasterDedicatedGrain fasterDedicatedGrain;
        private ImmutableList<LookupItem>[] batches;

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

            // create test data in advance
            // this assumes even batch sizes
            batches = new ImmutableList<LookupItem>[Items / BatchSize];
            for (var b = 0; b < batches.Length; ++b)
            {
                var batch = ImmutableList.CreateBuilder<LookupItem>();
                for (var i = 0; i < BatchSize; ++i)
                {
                    var x = BatchSize * b + i;
                    batch.Add(new LookupItem(x, x, new DateTime(x)));
                }
                batches[b] = batch.ToImmutable();
            }
        }

        [IterationSetup]
        public void IterationSetup()
        {
            fasterThreadPoolGrain.StartAsync(HashBuckets, MemorySizeBits).Wait();
            fasterDedicatedGrain.StartAsync(HashBuckets, MemorySizeBits).Wait();
            dictionaryGrain.StartAsync().Wait();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            fasterThreadPoolGrain.StopAsync().Wait();
            fasterDedicatedGrain.StopAsync().Wait();
            dictionaryGrain.StopAsync().Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1 << 20)]
        public int HashBuckets { get; set; }

        [Params(29)]
        public int MemorySizeBits { get; set; }

        [Params(1 << 10)]
        public int BatchSize { get; set; }

        [Params(4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount, Baseline = true)]
        public async Task ConcurrentDictionary()
        {
            var pipeline = new MyAsyncPipeline(Concurrency);
            foreach (var batch in batches)
            {
                await pipeline.WaitOneAsync();
                await pipeline.Add(dictionaryGrain.SetRangeAsync(batch));
            }
            await pipeline.WaitAllAsync();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public async Task FasterOnThreadPool()
        {
            var pipeline = new MyAsyncPipeline(Concurrency);
            foreach (var batch in batches)
            {
                await pipeline.WaitOneAsync();
                await pipeline.Add(fasterThreadPoolGrain.SetRangeAsync(batch));
            }
            await pipeline.WaitAllAsync();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public async Task FasterOnDedicatedThreads()
        {
            var pipeline = new MyAsyncPipeline(Concurrency);
            foreach (var batch in batches)
            {
                await pipeline.WaitOneAsync();
                await pipeline.Add(fasterDedicatedGrain.SetRangeAsync(batch));
            }
            await pipeline.WaitAllAsync();
        }
    }
}