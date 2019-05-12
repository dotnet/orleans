using System;
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
    [ShortRunJob]
    [EvaluateOverhead(false), AllStatisticsColumn, MarkdownExporter, RunOncePerIteration]
    [GcServer(true), GcConcurrent(true)]
    public class RangeDeltaBenchmarks
    {
        private const int ItemCount = 1 << 21;

        private IHost host;
        private IConcurrentDictionaryGrain concurrentGrain;
        private IFasterGrain fasterGrain;
        private ImmutableList<LookupItem>[] batches;
        private Task[] tasks;

        [GlobalSetup]
        public void GlobalSetup()
        {
            batches = Enumerable.Range(0, Items)
                .Select(i => new LookupItem(i % (1 << 10), i % (1 << 10), DateTime.UtcNow))
                .BatchIEnumerable(BatchSize)
                .Select(_ => _.ToImmutableList())
                .ToArray();

            host = Program.StartNewHost();

            concurrentGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);

            fasterGrain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterGrain>(Guid.Empty);

            tasks = new Task[batches.Length];
        }

        [IterationSetup]
        public void IterationSetup()
        {
            concurrentGrain.StartAsync().Wait();
            fasterGrain.StartAsync().Wait();
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

        [Params(1 << 10, 1 << 9, 1 << 8)]
        public int BatchSize { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void Faster()
        {
            for (var i = 0; i < batches.Length; ++i)
            {
                tasks[i] = fasterGrain.SetRangeDeltaAsync(batches[i]);
            }
            Task.WaitAll(tasks);
        }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void ConcurrentDictionary()
        {
            for (var i = 0; i < batches.Length; ++i)
            {
                tasks[i] = concurrentGrain.SetRangeDeltaAsync(batches[i]);
            }
            Task.WaitAll(tasks);
        }
    }
}