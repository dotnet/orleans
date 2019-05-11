using System;
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
    public class ConcurrentDictionarySingleSetBenchmarks
    {
        private readonly IHost host = Program.BuildHost();
        private IConcurrentDictionaryGrain grain;
        private LookupItem[] data;
        private const int Items = 1 << 13;

        [GlobalSetup]
        public void GlobalSetup()
        {
            // prepare workload
            data = Enumerable.Range(0, Items)
                .Select(x => new LookupItem(x, x, DateTime.UtcNow))
                .ToArray();

            // start orleans
            host.StartAsync().Wait();

            // grab a proxy to the concurrent dictionary grain
            grain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);
        }

        [IterationSetup]
        public void IterationSetup() => grain.StartAsync().Wait();

        [IterationCleanup]
        public void IterationCleanup() => grain.StopAsync().Wait();

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = Items)]
        public void Benchmark()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => grain.SetAsync(_)));
            pipeline.Wait();
        }
    }
}