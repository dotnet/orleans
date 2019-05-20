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
    [ShortRunJob, EvaluateOverhead(false), RunOncePerIteration, AllStatisticsColumn, MarkdownExporter]
    [GcServer(true), GcConcurrent(true)]
    public class BigDataBenchmarks
    {
        private const int ItemCount = 1 << 27;

        private readonly IHost host = Program.BuildHost();
        private IConcurrentDictionaryGrain dictionaryGrain;
        private IFasterThreadPoolGrain fasterThreadPoolGrain;
        private IFasterDedicatedGrain fasterDedicatedGrain;
        private IEnumerable<ImmutableList<LookupItem>> generator;
        private MyAsyncPipeline pipeline;

        [GlobalSetup]
        public void GlobalSetup()
        {
            host.StartAsync().Wait();

            dictionaryGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);

            fasterThreadPoolGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterThreadPoolGrain>(Guid.Empty);

            fasterDedicatedGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterDedicatedGrain>(Guid.Empty);

            generator = Enumerable.Range(0, Items)
                .Select(i => new LookupItem(i, i, new DateTime(i)))
                .BatchIEnumerable(BatchSize)
                .Select(b => b.ToImmutableList());

            pipeline = new MyAsyncPipeline(Concurrency);
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

        [Params(1 << 20)]
        public int BatchSize { get; set; }

        [Params(4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void FasterOnDedicatedThreads()
        {
            foreach (var batch in generator)
            {
                pipeline.Add(fasterDedicatedGrain.SetRangeAsync(batch));
            }
            pipeline.WaitAll();
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void FasterOnThreadPool()
        {
            foreach (var batch in generator)
            {
                pipeline.Add(fasterThreadPoolGrain.SetRangeAsync(batch));
            }
            pipeline.WaitAll();
        }

        [Benchmark(OperationsPerInvoke = ItemCount, Baseline = true)]
        public void ConcurrentDictionary()
        {
            foreach (var batch in generator)
            {
                pipeline.Add(dictionaryGrain.SetRangeAsync(batch));
            }
            pipeline.WaitAll();
        }
    }
}