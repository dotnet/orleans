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
    [ShortRunJob, EvaluateOverhead(false), AllStatisticsColumn, MarkdownExporter]
    [GcServer(true), GcConcurrent(true)]
    public class DictionaryRangeGetBenchmarks
    {
        private IHost host;
        private IDictionaryGrain grain;
        private ImmutableList<int>[] input;
        private ImmutableList<LookupItem> output;
        private const int Items = 1 << 20;

        [GlobalSetup]
        public void GlobalSetup()
        {
            // prepare the lookup workload
            input = Enumerable.Range(0, Items)
                .BatchIEnumerable(BatchSize)
                .Select(x => x.ToImmutableList())
                .ToArray();

            // prepare preloaded data
            output = Enumerable.Range(0, Items)
                .Select(x => new LookupItem(x, x, DateTime.UtcNow))
                .ToImmutableList();

            // startup orleans
            host = Program.StartNewHost();

            // grab a proxy to the dictionary grain
            grain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IDictionaryGrain>(Guid.Empty);

            // preload the dictionary grain
            grain.SetRangeAsync(output).Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Params(1 << 17, 1 << 16, 1 << 15)]
        public int BatchSize { get; set; }

        [Benchmark(OperationsPerInvoke = Items)]
        public void Benchmark()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(input.Select(x => grain.TryGetRangeAsync(x)));
            pipeline.Wait();
        }
    }
}