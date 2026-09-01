using Orleans.BroadcastChannel;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestCategory("BVT"), TestCategory("Streaming")]
public class BroadcastChannelCancellationCompatibilityTests
{
    [Fact]
    public async Task WriterCancellationOverload_DelegatesToLegacyImplementation()
    {
        IBroadcastChannelWriter<int> writer = new LegacyWriter();

        await writer.Publish(42, TestContext.Current.CancellationToken);

        Assert.Equal(42, ((LegacyWriter)writer).Published);
    }

    [Fact]
    public async Task SubscriptionCancellationOverload_DelegatesToLegacyImplementation()
    {
        IBroadcastChannelSubscription subscription = new LegacySubscription();
        CancellationToken deliveredToken = new(canceled: true);

        await subscription.Attach<int>((int item, CancellationToken cancellationToken) =>
        {
            Assert.Equal(42, item);
            deliveredToken = cancellationToken;
            return Task.CompletedTask;
        });
        await ((LegacySubscription)subscription).Publish(42);

        Assert.Equal(CancellationToken.None, deliveredToken);
    }

    private sealed class LegacyWriter : IBroadcastChannelWriter<int>
    {
        public int Published { get; private set; }

        public Task Publish(int item)
        {
            Published = item;
            return Task.CompletedTask;
        }
    }

    private sealed class LegacySubscription : IBroadcastChannelSubscription
    {
        private Func<int, Task>? _onPublished;

        public ChannelId ChannelId => default;

        public string ProviderName => "test";

        public Task Attach<T>(Func<T, Task> onPublished, Func<Exception, Task>? onError = null)
        {
            _onPublished = item => onPublished((T)(object)item);
            return Task.CompletedTask;
        }

        public Task Publish(int item) => _onPublished!(item);
    }
}
