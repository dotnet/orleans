using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

public static class SubscriptionStartPositions
{
    // <subscribe_earliest_available>
    public static Task<StreamSubscriptionHandle<T>> SubscribeFromCacheStart<T>(
        IAsyncStream<T> stream,
        IAsyncObserver<T> observer) =>
        stream.SubscribeAsync(
            observer,
            StreamSubscriptionStartPosition.EarliestAvailable);
    // </subscribe_earliest_available>

    // <subscribe_batch_earliest_available>
    public static Task<StreamSubscriptionHandle<T>> SubscribeBatchFromCacheStart<T>(
        IAsyncBatchObservable<T> stream,
        IAsyncBatchObserver<T> observer) =>
        stream.SubscribeAsync(
            observer,
            StreamSubscriptionStartPosition.EarliestAvailable);
    // </subscribe_batch_earliest_available>

    // <configure_default_start_position>
    public static void ConfigureDefaultStartPosition(
        ISiloPersistentStreamConfigurator streams)
    {
        streams.ConfigurePullingAgent(optionsBuilder =>
            optionsBuilder.Configure(options =>
                options.InitialSubscriptionStartPosition =
                    StreamSubscriptionStartPosition.EarliestAvailable));
    }
    // </configure_default_start_position>
}

