using System;
using System.Threading.Tasks;
using System.Linq;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using Orleans.Runtime;
using System.Collections.Generic;
using Orleans.Internal;

namespace Tester.EventSourcingTests
{
    public partial class CountersGrainTests : IClassFixture<EventSourcingClusterFixture>
    {
        private readonly EventSourcingClusterFixture fixture;

        public CountersGrainTests(EventSourcingClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task Record()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ICountersGrain>(GrainId.Create("simple-counters-grain", "0"));

            var currentstate = await grain.GetTentativeState();
            Assert.NotNull(currentstate);
            Assert.Empty(currentstate);

            await grain.Add("Alice", 1, false);
            await grain.Add("Alice", 1, false);
            await grain.Add("Alice", 1, false);

            // all three updates should be visible in the tentative count (even if not confirmed yet)
            Assert.Equal(3, await grain.GetTentativeCount("Alice"));

            // reset all counters to zero, and wait for confirmation
            await grain.Reset(true);

            Assert.Empty((await grain.GetTentativeState()));
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task ConcurrentIncrements()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ICountersGrain>(GrainId.Create("simple-counters-grain", "0"));
            await ConcurrentIncrementsRunner(grain, 50, false);
        }

        private static string[] keys = { "a", "b", "c", "d", "e", "f", "g", "h" };
        private string RandomKey() { return keys[Random.Shared.Next(keys.Length)]; }


        private async Task ConcurrentIncrementsRunner(ICountersGrain grain, int count, bool wait_for_confirmation_on_each)
        {
            // increment (count) times, on random keys, concurrently
            var tasks = new List<Task>();
            for (int i = 0; i < count; i++)
                tasks.Add(grain.Add(RandomKey(), 1, wait_for_confirmation_on_each));
            await Task.WhenAll(tasks);

            // check that the tentative state shows all increments
            Assert.Equal(count, (await grain.GetTentativeState()).Aggregate(0, (c, kvp) => c + kvp.Value));

            // if we did not wait for confirmation on each event, wait now
            if (!wait_for_confirmation_on_each)
                await grain.ConfirmAllPreviouslyRaisedEvents();

            // check that the confirmed state shows all the increments
            Assert.Equal(count, (await grain.GetConfirmedState()).Aggregate(0, (c, kvp) => c + kvp.Value));

            // reset all counters
            await grain.Reset(true);
        }
    }
}