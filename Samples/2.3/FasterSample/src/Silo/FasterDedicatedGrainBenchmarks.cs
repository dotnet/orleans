using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Grains;
using Grains.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Silo
{
    public class FasterDedicatedGrainBenchmarks
    {
        private const int ItemCount = 1 << 20;

        private readonly IHost host = Program.BuildHost();
        private Queue<Task> tasks;
        private IFasterDedicatedGrain grain;

        [GlobalSetup]
        public void GlobalSetup()
        {
            host.StartAsync().Wait();
            grain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterDedicatedGrain>(Guid.NewGuid());
            tasks = new Queue<Task>(Concurrency);
        }

        [IterationSetup]
        public void IterationSetup() => grain.StartAsync(HashBuckets, MemorySizeBits).Wait();

        [IterationCleanup]
        public void IterationCleanup() => grain.StopAsync().Wait();

        [GlobalCleanup]
        public void GlobalCleanup() => host.StopAsync().Wait();

        [Params(ItemCount)]
        public int Items { get; set; }

        [Params(1 << 20)]
        public int HashBuckets { get; set; }

        [Params(29)]
        public int MemorySizeBits { get; set; }

        [Params(1 << 10)]
        public int BatchSize { get; set; }

        [Params(8)]
        public int Concurrency { get; set; }

        [Benchmark(OperationsPerInvoke = ItemCount)]
        public async Task Set()
        {
            // add up to concurrency
            for (var i = 0; i < Concurrency; ++i)
            {
                tasks.Enqueue(grain.SetAsync(new LookupItem(i, i, DateTime.UtcNow)));
            }

            // keep up the pipeline
            for (var i = Concurrency; i < Items; ++i)
            {
                await tasks.Dequeue();
                tasks.Enqueue(grain.SetAsync(new LookupItem(i, i, DateTime.UtcNow)));
            }

            // flush the pipeline
            while (tasks.Count > 0)
            {
                await tasks.Dequeue();
            }
        }
    }
}