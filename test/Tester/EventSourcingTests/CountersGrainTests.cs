using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using Orleans;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime;
using System.Collections.Generic;

namespace Tester.EventSourcingTests
{
    public partial class CountersGrainTests : IClassFixture<EventSourcingClusterFixture>
    {

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task Record()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain");

            var currentstate = await grain.GetAll();
            Assert.NotNull(currentstate);
            Assert.Equal(0, currentstate.Count());

            await grain.Add("Alice", 1, false);
            await grain.Add("Alice", 1, false);
            await grain.Add("Alice", 1, false);

            // all three updates should be visible (even if not persisted to storage yet)
            Assert.Equal(3, await grain.Get("Alice"));

            await grain.Reset(true);

            Assert.Equal(0, (await grain.GetAll()).Count());
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task ConcurrentIncrements()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ICountersGrain>(0, "TestGrains.CountersGrain");
            await ConcurrentIncrements(grain, 50, false);
        }


        private static string[] keys = { "a", "b", "c", "d", "e", "f", "g", "h" };
        private static readonly SafeRandom random = new SafeRandom();
        private string RandomKey() { return keys[random.Next(keys.Length)]; }


        private async Task ConcurrentIncrements(ICountersGrain grain, int count, bool wait_till_persisted_on_each)
        {
            // increment (count) times, on random keys, in parallel
            var tasks = new List<Task>();
            for (int i = 0; i < count; i++)
                tasks.Add(grain.Add(RandomKey(), 1, wait_till_persisted_on_each));
            await Task.WhenAll(tasks);

            // check that the sum of all the counters is correct
            Assert.Equal(count, (await grain.GetAll()).Aggregate(0, (c, kvp) => c + kvp.Value));

            // reset all counters
            await grain.Reset(true);
        }
    }
}