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
    public class SetBenchmarks
    {
        private readonly IHost host = Program.BuildHost();
        private IDictionaryGrain dictionaryGrain;
        private IConcurrentDictionaryGrain concurrentDictionaryGrain;
        private IFasterSimpleGrain fasterSimpleGrain;
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

            // grab a proxy to the dictionary grain
            dictionaryGrain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IDictionaryGrain>(Guid.Empty);

            // grab a proxy to the concurrent dictionary
            concurrentDictionaryGrain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IConcurrentDictionaryGrain>(Guid.Empty);

            // grab a proxy to the faster simple grain
            fasterSimpleGrain = host.Services.GetService<IGrainFactory>()
                .GetGrain<IFasterSimpleGrain>(Guid.Empty);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            // warm up the dictionary grain
            dictionaryGrain.StartAsync().Wait();

            // warm up the concurrent dictionary grain
            concurrentDictionaryGrain.StartAsync().Wait();

            // warm up the faster simple grain
            fasterSimpleGrain.StartAsync().Wait();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            // deactivate the dictionary grain
            dictionaryGrain.StopAsync().Wait();

            // deactivate the concurrent dictionary grain
            concurrentDictionaryGrain.StopAsync().Wait();

            // deactivate the faster simple grain
            fasterSimpleGrain.StopAsync().Wait();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = Items)]
        public void DictionaryGrainSet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => dictionaryGrain.SetAsync(_)));
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = Items)]
        public void ConcurrentDictionarySet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => concurrentDictionaryGrain.SetAsync(_)));
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = Items)]
        public void FasterSimpleSet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(_ => fasterSimpleGrain.SetAsync(_)));
            pipeline.Wait();
        }
    }
}