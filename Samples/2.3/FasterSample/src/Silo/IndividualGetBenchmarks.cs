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
    [ShortRunJob, MarkdownExporter, AllStatisticsColumn]
    [GcServer(true), GcConcurrent(true)]
    public class IndividualGetBenchmarks
    {
        private IHost host;
        private IDictionaryLookupGrain dictionaryGrain;
        private IConcurrentDictionaryLookupGrain concurrentDictionaryGrain;
        private int[] data;
        private const int Items = 1 << 14;

        [GlobalSetup]
        public void GlobalSetup()
        {
            host = Program.BuildHost();
            host.StartAsync().Wait();

            var values = Enumerable.Range(0, Items).Select(x => new LookupItem(x, x, DateTime.UtcNow)).ToImmutableList();

            dictionaryGrain = host.Services.GetService<IGrainFactory>().GetGrain<IDictionaryLookupGrain>(Guid.Empty);
            dictionaryGrain.SetAsync(values).Wait();

            concurrentDictionaryGrain = host.Services.GetService<IGrainFactory>().GetGrain<IConcurrentDictionaryLookupGrain>(Guid.Empty);
            concurrentDictionaryGrain.SetAsync(values).Wait();

            data = Enumerable.Range(0, Items).ToArray();
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(1, 2, 4)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = Items)]
        public void DictionaryGrainGet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(x => dictionaryGrain.TryGetAsync(x)));
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = Items)]
        public void ConcurrentDictionaryGrainGet()
        {
            var pipeline = new AsyncPipeline(Concurrency);
            pipeline.AddRange(data.Select(x => concurrentDictionaryGrain.TryGetAsync(x)));
            pipeline.Wait();
        }
    }
}