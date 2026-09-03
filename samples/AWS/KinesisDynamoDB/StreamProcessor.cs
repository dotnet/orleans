using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;

internal static class SampleConstants
{
    public const string StreamProvider = "Kinesis";
    public const string StreamNamespace = "aws-sample";
    public static readonly Guid StreamId = Guid.Parse("7a7638f7-8fd7-413d-93aa-13399f60dc47");
}

public interface IStreamProcessorGrain : IGrainWithGuidKey
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask PublishAsync(string message, CancellationToken cancellationToken);

    ValueTask<StreamProcessorState> GetStateAsync(CancellationToken cancellationToken);
}

[GenerateSerializer]
public sealed class StreamProcessorState
{
    [Id(0)]
    public int EventCount { get; set; }

    [Id(1)]
    public int ReminderCount { get; set; }

    [Id(2)]
    public string? LastMessage { get; set; }
}

[GenerateSerializer]
public sealed record SampleEvent(
    [property: Id(0)] string Message,
    [property: Id(1)] DateTimeOffset CreatedAt);

[ImplicitStreamSubscription(SampleConstants.StreamNamespace)]
public sealed class StreamProcessorGrain(
    [PersistentState("stream-processor")] IPersistentState<StreamProcessorState> state,
    ILogger<StreamProcessorGrain> logger)
    : Grain, IStreamProcessorGrain, IStreamSubscriptionObserver, IAsyncObserver<SampleEvent>, IRemindable
{
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.RegisterOrUpdateReminder(
            "report-progress",
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask PublishAsync(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = this.GetStreamProvider(SampleConstants.StreamProvider)
            .GetStream<SampleEvent>(StreamId.Create(SampleConstants.StreamNamespace, this.GetPrimaryKey()));
        await stream.OnNextAsync(new SampleEvent(message, DateTimeOffset.UtcNow));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public ValueTask<StreamProcessorState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(state.State);
    }

    public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
    {
        var handle = handleFactory.Create<SampleEvent>();
        await handle.ResumeAsync(this);
    }

    public async Task OnNextAsync(SampleEvent item, StreamSequenceToken? token = null)
    {
        state.State.EventCount++;
        state.State.LastMessage = item.Message;
        await state.WriteStateAsync();
        logger.LogInformation(
            "Processed event {EventCount} created at {CreatedAt}: {Message}",
            state.State.EventCount,
            item.CreatedAt,
            item.Message);
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception exception)
    {
        logger.LogError(exception, "The Kinesis stream subscription failed");
        return Task.CompletedTask;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        state.State.ReminderCount++;
        await state.WriteStateAsync();
        logger.LogInformation(
            "DynamoDB reminder {ReminderName} fired. Processed events: {EventCount}",
            reminderName,
            state.State.EventCount);
    }
}
