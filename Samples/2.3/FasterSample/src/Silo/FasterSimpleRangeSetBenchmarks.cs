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
    public class FasterSimpleRangeSetBenchmarks
    {
        private IHost host;
        private IFasterSimpleGrain grain;
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
            host = Program.StartNewHost();

            // grab a proxy to the grain
            grain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IFasterSimpleGrain>(Guid.Empty);

            // activate the grain
            grain.StartAsync().Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1, 2, 4, 8)]
        public int Concurrency { get; set; }

        [Params(1 << 10, 1 << 11, 1 << 12)]
        public int BatchSize { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public void Benchmark()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => grain.SetRangeAsync(_)));
            pipeline.Wait();
        }
    }
}