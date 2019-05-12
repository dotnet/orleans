using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
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
    [ShortRunJob, EvaluateOverhead(false), AllStatisticsColumn, MarkdownExporter, RunOncePerIteration]
    [GcServer(true), GcConcurrent(true)]
    public class FasterDedicatedRangeSetBenchmarks
    {
        private const int ItemCount = 1 << 20;

        private IHost host;
        private IDictionaryGrain dictionaryGrain;
        private IConcurrentDictionaryGrain concurrentGrain;
        private IFasterSimpleGrain fasterGrain;
        private ImmutableList<LookupItem>[] batches;
        private Task[] tasks;

        [GlobalSetup]
        public void GlobalSetup()
        {
            batches = Enumerable.Range(0, Items)
                .Select(i => new LookupItem(i, i, DateTime.UtcNow))
                .BatchIEnumerable(BatchSize)
                .Select(_ => _.ToImmutableList())
                .ToArray();

            host = Program.StartNewHost();

            dictionaryGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IDictionaryGrain>(Guid.Empty);

            concurrentGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);

            fasterGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterSimpleGrain>(Guid.Empty);

            tasks = new Task[batches.Length];
        }

        [IterationSetup]
        public void IterationSetup()
        {
            dictionaryGrain.StartAsync().Wait();
            concurrentGrain.StartAsync().Wait();
            fasterGrain.StartAsync().Wait();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            dictionaryGrain.StopAsync().Wait();
            concurrentGrain.StopAsync().Wait();
            fasterGrain.StopAsync().Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1 << 10, 1 << 11, 1 << 12)]
        public int BatchSize { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void Faster()
        {
            for (var i = 0; i < batches.Length; ++i)
            {
                tasks[i] = fasterGrain.SetRangeAsync(batches[i]);
            }
            Task.WaitAll(tasks);
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void ConcurrentDictionary()
        {
            for (var i = 0; i < batches.Length; ++i)
            {
                tasks[i] = concurrentGrain.SetRangeAsync(batches[i]);
            }
            Task.WaitAll(tasks);
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void Dictionary()
        {
            for (var i = 0; i < batches.Length; ++i)
            {
                tasks[i] = dictionaryGrain.SetRangeAsync(batches[i]);
            }
            Task.WaitAll(tasks);
        }
    }
}