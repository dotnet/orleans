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
    [ShortRunJob, MarkdownExporter, AllStatisticsColumn]
    [GcServer(true), GcConcurrent(true)]
    public class SetBenchmarks
    {
        private IHost host;
        private IDictionaryLookupGrain dictionaryGrain;
        private IConcurrentDictionaryLookupGrain concurrentDictionaryGrain;
        private LookupItem[] data;
        private const int Items = 1 << 14;

        [GlobalSetup]
        public void GlobalSetup()
        {
            host = Program.BuildHost();
            host.StartAsync().Wait();

            data = Enumerable.Range(0, Items).Select(x => new LookupItem(x, x, DateTime.UtcNow)).ToArray();
            dictionaryGrain = host.Services.GetService<IGrainFactory>().GetGrain<IDictionaryLookupGrain>(Guid.Empty);
            concurrentDictionaryGrain = host.Services.GetService<IGrainFactory>().GetGrain<IConcurrentDictionaryLookupGrain>(Guid.Empty);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            dictionaryGrain.StartAsync().Wait();
            concurrentDictionaryGrain.StartAsync().Wait();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            dictionaryGrain.StopAsync().Wait();
            concurrentDictionaryGrain.StopAsync().Wait();
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
    }
}