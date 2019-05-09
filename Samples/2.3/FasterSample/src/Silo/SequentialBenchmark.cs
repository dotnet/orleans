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

namespace Silo
{
    [MarkdownExporter]
    public class SequentialBenchmarks
    {
        private IHost host;
        private IDictionaryLookupGrain dictionary;
        private ImmutableList<LookupItem> items;

        [GlobalSetup]
        public void GlobalSetup()
        {
            host = Program.BuildHost();
            host.StartAsync().Wait();

            dictionary = host.Services.GetService<IGrainFactory>().GetGrain<IDictionaryLookupGrain>(Guid.NewGuid());
        }

        [IterationSetup]
        public void IterationSetup()
        {
            dictionary.StartAsync().Wait();
            items = Enumerable.Range(0, ItemCount).Select(x => new LookupItem(x, x, DateTime.UtcNow)).ToImmutableList();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            dictionary.StopAsync().Wait();
            items = null;
        }

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(10000)]
        public int ItemCount { get; set; }

        [Benchmark(OperationsPerInvoke = 10000)]
        public async Task SequentialDictionaryUpsert()
        {
            foreach (var item in items)
            {
                await dictionary.SetAsync(item);
            }
        }
    }
}