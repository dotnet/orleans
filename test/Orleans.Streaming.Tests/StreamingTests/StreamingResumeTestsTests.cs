using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Streams;
using Xunit;

namespace Tester.StreamingTests;

public class StreamingResumeTestsTests
{
    private const string StreamProviderName = "LazyStreamProvider";

    [Fact]
    public async Task GetProviderQueueCountAsync_InitializesLazyAdapterFactory()
    {
        var adapter = Substitute.For<IQueueAdapter>();
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 3 },
            StreamProviderName);
        var adapterFactory = new LazyQueueAdapterFactory(adapter, mapper);
        using var services = new ServiceCollection()
            .AddKeyedSingleton<IQueueAdapterFactory>(StreamProviderName, adapterFactory)
            .BuildServiceProvider();

        var queueCount = await StreamingResumeTests.GetProviderQueueCountAsync(services, StreamProviderName);

        Assert.True(adapterFactory.AdapterCreated);
        Assert.Equal(3, queueCount);
    }

    private sealed class LazyQueueAdapterFactory(IQueueAdapter adapter, IStreamQueueMapper mapper) : IQueueAdapterFactory
    {
        public bool AdapterCreated { get; private set; }

        public Task<IQueueAdapter> CreateAdapter()
        {
            AdapterCreated = true;
            return Task.FromResult(adapter);
        }

        public IQueueAdapterCache GetQueueAdapterCache() => Substitute.For<IQueueAdapterCache>();

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            Assert.True(AdapterCreated);
            return mapper;
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
            => Task.FromResult(Substitute.For<IStreamFailureHandler>());
    }
}
