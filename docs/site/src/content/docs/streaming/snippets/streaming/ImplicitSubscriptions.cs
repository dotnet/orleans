using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans.Docs.Snippets.Streaming;

// <implicit_subscription_grain>
[ImplicitStreamSubscription("MyStreamNamespace")]
public class ImplicitSubscriptionGrain : Grain, IAsyncObserver<string>, IStreamSubscriptionObserver
{
    private readonly ILogger<ImplicitSubscriptionGrain> _logger;

    public ImplicitSubscriptionGrain(ILogger<ImplicitSubscriptionGrain> logger)
    {
        _logger = logger;
    }

    // <on_next_async>
    public Task OnNextAsync(string item, StreamSequenceToken? token = null)
    {
        _logger.LogInformation("Received an item from the stream: {Item}", item);
        return Task.CompletedTask;
    }
    // </on_next_async>

    // <on_subscribed>
    public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
    {
        var handle = handleFactory.Create<string>();
        await handle.ResumeAsync(this);
    }
    // </on_subscribed>

    public Task OnCompletedAsync()
    {
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "Error in stream processing");
        return Task.CompletedTask;
    }
}
// </implicit_subscription_grain>

// Grain for implicit subscription setup during activation
public class ImplicitSetupGrain : Grain
{
    // <implicit_subscription_setup>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        IStreamProvider streamProvider =
            this.GetStreamProvider("SimpleStreamProvider");

        StreamId streamId =
            StreamId.Create("MyStreamNamespace", this.GetPrimaryKey());
        IAsyncStream<string> stream =
            streamProvider.GetStream<string>(streamId);

        StreamSubscriptionHandle<string> subscription =
            await stream.SubscribeAsync(new MyStreamObserver());
    }
    // </implicit_subscription_setup>
}

// Helper observer class
public class MyStreamObserver : IAsyncObserver<string>
{
    public Task OnNextAsync(string item, StreamSequenceToken? token = null)
    {
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync()
    {
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        return Task.CompletedTask;
    }
}
