using Orleans;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

// <explicit_subscription_grain>
public class ExplicitSubscriptionGrain : Grain, IAsyncObserver<string>
{
    private const string PROVIDER_NAME = "SimpleStreamProvider";

    // <explicit_subscription_activate>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(PROVIDER_NAME);
        var streamId = StreamId.Create("MyStreamNamespace", this.GetPrimaryKey());
        var stream = streamProvider.GetStream<string>(streamId);

        var subscriptionHandles = await stream.GetAllSubscriptionHandles();
        foreach (var handle in subscriptionHandles)
        {
            await handle.ResumeAsync(this);
        }
    }
    // </explicit_subscription_activate>

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
// </explicit_subscription_grain>
