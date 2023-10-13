using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams.Filtering;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Sdk;

namespace Tester.StreamingTests.Filtering
{
    public class CustomStreamFilter : IStreamFilter
    {
        private readonly ILogger<CustomStreamFilter> logger;

        public CustomStreamFilter(ILogger<CustomStreamFilter> logger)
        {
            this.logger = logger;
        }

        public bool ShouldDeliver(StreamId streamId, object item, string filterData)
        {
            try
            {
                var result = ShouldDeliverImpl(streamId, item, filterData);
                logger.LogInformation("Filter -> StreamId {StreamId}, Item: {Item}, FilterData: {FilterData} -> Result: {Result}", streamId, item, filterData, result);

                return result;
            }
            catch (Exception)
            {
                logger.LogInformation("Filter -> StreamId {StreamId}, Item: {Item}, FilterData: {FilterData} -> Result: exception", streamId, item, filterData);
                throw;
            }
        }

        private bool ShouldDeliverImpl(StreamId streamId, object item, string filterData)
        {
            if (filterData.Equals("throw"))
                throw new Exception("throw");

            if (string.IsNullOrWhiteSpace(filterData))
                return true;

            if (filterData.Equals("even"))
            {
                var value = (int)item;
                return value % 2 == 0;
            }

            if (filterData.Equals("only3"))
            {
                var value = (int)item;
                return value == 3;
            }

            if (filterData.Equals("only7"))
            {
                var value = (int)item;
                return value == 7;
            }

            return true;
        }
    }

    public abstract class StreamFilteringTestsBase : OrleansTestingBase
    {
        protected readonly BaseTestClusterFixture fixture;
        private IClusterClient clusterClient => this.fixture.Client;
        private CustomStreamFilter streamFilter => this.fixture.HostedCluster.ServiceProvider.GetServiceByName<IStreamFilter>(ProviderName) as CustomStreamFilter;

        protected ILogger logger => fixture.Logger;

        protected abstract string ProviderName { get; }

        protected virtual TimeSpan WaitTime => TimeSpan.Zero;

        protected StreamFilteringTestsBase(BaseTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        public virtual async Task IgnoreBadFilter()
        {
            EnsureStreamFilterIsRegistered();

            const int numberOfEvents = 10;
            var streamId = StreamId.Create("IgnoreBadFilter", "my-stream");
            var grain = this.clusterClient.GetGrain<IStreamingHistoryGrain>("IgnoreBadFilter");

            try
            {
                await grain.BecomeConsumer(streamId, ProviderName, "throw");

                var stream = this.clusterClient.GetStreamProvider(ProviderName).GetStream<int>(streamId);

                for (var i = 0; i < numberOfEvents; i++)
                {
                    await stream.OnNextAsync(i);
                }

                await Task.Delay(WaitTime);

                var history = await grain.GetReceivedItems();

                Assert.Equal(numberOfEvents, history.Count);
                for (var i = 0; i < numberOfEvents; i++)
                {
                    Assert.Equal(i, history[i]);
                }
            }
            finally
            {
                await grain.StopBeingConsumer();
            }
        }

        public virtual async Task OnlyEvenItems()
        {
            EnsureStreamFilterIsRegistered();

            const int numberOfEvents = 10;
            var streamId = StreamId.Create("OnlyEvenItems", "my-stream");
            var grain = this.clusterClient.GetGrain<IStreamingHistoryGrain>("OnlyEvenItems");

            try
            {
                await grain.BecomeConsumer(streamId, ProviderName, "even");

                var stream = this.clusterClient.GetStreamProvider(ProviderName).GetStream<int>(streamId);

                for (var i = 0; i < numberOfEvents; i++)
                {
                    await stream.OnNextAsync(i);
                }

                await Task.Delay(WaitTime);

                var history = await grain.GetReceivedItems();

                var idx = 0;
                for (var i = 0; i < numberOfEvents; i++)
                {
                    if (i % 2 == 0)
                    {
                        Assert.Equal(i, history[idx]);
                        idx++;
                    }
                }
            }
            finally
            {
                await grain.StopBeingConsumer();
            }
        }

        public virtual async Task MultipleSubscriptionsDifferentFilterData()
        {
            EnsureStreamFilterIsRegistered();

            const int numberOfEvents = 10;
            var streamId = StreamId.Create("MultipleSubscriptionsDifferentFilterData", "my-stream");
            var grain = this.clusterClient.GetGrain<IStreamingHistoryGrain>("MultipleSubscriptionsDifferentFilterData");

            try
            {
                await grain.BecomeConsumer(streamId, ProviderName, "only3");
                await grain.BecomeConsumer(streamId, ProviderName, "only7");

                var stream = this.clusterClient.GetStreamProvider(ProviderName).GetStream<int>(streamId);

                for (var i = 0; i < numberOfEvents; i++)
                {
                    await stream.OnNextAsync(i);
                }

                await Task.Delay(WaitTime);

                var history = await grain.GetReceivedItems();

                Assert.Equal(2, history.Count);
                Assert.Contains(3, history);
                Assert.Contains(7, history);

            }
            finally
            {
                await grain.StopBeingConsumer();
            }
        }

        private void EnsureStreamFilterIsRegistered()
        {
            if (this.streamFilter == null)
            {
                throw new XunitException("CustomStreamFilter not registered as a filter!");
            }
        }
    }
}
