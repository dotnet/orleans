using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;

internal static class SampleConstants
{
    public const string StreamProvider = "RabbitMQ";
    public const string StreamNamespace = "rabbitmq-sample";
    public static readonly Guid StreamId = Guid.Parse("537b1f42-58aa-4bcb-a83d-c6f5a8ab7ca2");
}

public interface IStreamProducerGrain : IGrainWithStringKey
{
    ValueTask PublishAsync(Guid streamKey, SampleEvent item);
}

public interface IStreamConsumerGrain : IGrainWithGuidKey;

[GenerateSerializer]
public sealed record SampleEvent(
    [property: Id(0)] int Sequence,
    [property: Id(1)] DateTimeOffset CreatedAt);

public sealed class StreamProducerGrain : Grain, IStreamProducerGrain
{
    public async ValueTask PublishAsync(Guid streamKey, SampleEvent item)
    {
        var stream = this.GetStreamProvider(SampleConstants.StreamProvider)
            .GetStream<SampleEvent>(StreamId.Create(SampleConstants.StreamNamespace, streamKey));
        await stream.OnNextAsync(item);
    }
}

[ImplicitStreamSubscription(SampleConstants.StreamNamespace)]
public sealed class StreamConsumerGrain(
    ILogger<StreamConsumerGrain> logger)
    : Grain, IStreamConsumerGrain, IStreamSubscriptionObserver, IAsyncObserver<SampleEvent>
{
    public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
    {
        var handle = handleFactory.Create<SampleEvent>();
        await handle.ResumeAsync(this);
    }

    public Task OnNextAsync(SampleEvent item, StreamSequenceToken? token = null)
    {
        logger.LogInformation(
            "Consumed event {Sequence}, created at {CreatedAt}, with token {Token}",
            item.Sequence,
            item.CreatedAt,
            token);
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception exception)
    {
        logger.LogError(exception, "RabbitMQ stream subscription failed");
        return Task.CompletedTask;
    }
}
