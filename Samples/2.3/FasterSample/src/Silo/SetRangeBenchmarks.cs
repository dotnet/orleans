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
    [ShortRunJob, RunOncePerIteration, AllStatisticsColumn, MarkdownExporter]
    [GcServer(true), GcConcurrent(true)]
    public class SetRangeBenchmarks
    {
        private const int ItemCount = 1 << 20;

        private readonly IHost host = Program.BuildHost();
        private IConcurrentDictionaryGrain dictionaryGrain;
        private IFasterThreadPoolGrain fasterThreadPoolGrain;
        private IFasterDedicatedGrain fasterDedicatedGrain;
        private IEnumerable<ImmutableList<LookupItem>> generator;

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

            generator = Enumerable.Range(0, Items)
                .Select(i => new LookupItem(i, i, new DateTime(i)))
                .BatchIEnumerable(BatchSize)
                .Select(b => b.ToImmutableList());
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

        [Params(8)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount, Baseline = true)]
        public void ConcurrentDictionary()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            foreach (var batch in generator)
            {
                pipeline.Add(dictionaryGrain.SetRangeAsync(batch));
            }
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void FasterOnThreadPool()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            foreach (var batch in generator)
            {
                pipeline.Add(fasterThreadPoolGrain.SetRangeAsync(batch));
            }
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void FasterOnDedicatedThreads()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            foreach (var batch in generator)
            {
                pipeline.Add(fasterDedicatedGrain.SetRangeAsync(batch));
            }
            pipeline.Wait();
        }
    }
}