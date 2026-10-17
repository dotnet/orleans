using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Diagnostics;
using Orleans.Runtime;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that a start whose invocation its target can't decode is retried, and fails with
/// <see cref="DurableTaskDeliveryException"/> once <see cref="DurableMessagingOptions.OutboxRetryTimeout"/> elapses.
/// </summary>
/// <remarks>
/// A <see cref="DeliveryFaultInjector"/> fails each delivery of the invocation at the target's inbox, as a request type
/// which the target's deployment doesn't know does. The outbox retries on the Durable Jobs clock, which the test advances
/// once the outbox has rescheduled each attempt.
/// </remarks>
public abstract class DurableTaskUndecodableStartTests : IAsyncLifetime
{
    private const string DecodingError = "The durable task invocation doesn't decode: the request type is unknown to this deployment.";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskUndecodableStartTests(string journalFormatKey)
    {
        _fixture = new(journalFormatKey, silo => silo.Services.Configure<DurableMessagingOptions>(options =>
        {
            options.RetryInitialInterval = RetryDelay;
            options.OutboxRetryTimeout = RetryTimeout;
        }));
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task UndecodableStartIsRetriedAndFailsWithDeliveryExceptionAfterTheRetryTimeout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<IUndecodableStartWorkflowGrain>(Guid.NewGuid());
        var target = _fixture.Client.GetGrain<IUndecodableStartTargetGrain>(Guid.NewGuid());
        var (starterId, targetId) = (starter.GetGrainId(), target.GetGrainId());
        using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);
        using var faults = new DeliveryFaultInjector(
            receiving => receiving.GrainContext.GrainId == targetId && receiving.Message.Envelope.Subject == "durable-rpc/invoke",
            DecodingError);

        var workflow = await starter.StartAsync(target.GetPrimaryKey()).WithId("workflow").ScheduleAsync(cancellationToken);
        var outcome = workflow.GetResponseAsync(cancellationToken);

        // The target fails the delivery and stages nothing, so the starter's outbox reschedules it, on the Durable Jobs
        // clock, which the test advances.
        var firstRetry = await WaitForRetryAsync(events, starterId, attemptCount: 1);
        Assert.Equal(targetId, firstRetry.ReceiverId);
        Assert.Contains(DecodingError, firstRetry.Error, StringComparison.Ordinal);
        Assert.Equal(1, faults.Count);
        Assert.False(outcome.IsCompleted);

        _fixture.JobClock.Advance(RetryDelay * 2);
        await WaitForRetryAsync(events, starterId, attemptCount: 2);
        Assert.Equal(2, faults.Count);
        Assert.False(outcome.IsCompleted);

        // Once the retry timeout elapses, the outbox dead-letters the invocation, and the start fails.
        _fixture.JobClock.Advance(RetryTimeout);
        var retired = (MessagingEvents.OutboxMessageRetired)(await events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxMessageRetired),
            evt => evt.Payload is MessagingEvents.OutboxMessageRetired retired
                && retired.GrainContext.GrainId == starterId
                && retired.MessageId == firstRetry.MessageId,
            Timeout,
            cancellationToken)).Payload!;
        Assert.Equal(OutboxMessageRetirementStatus.DeadLettered, retired.Retirement.Status);
        Assert.Contains(DecodingError, retired.Retirement.Reason, StringComparison.Ordinal);

        var response = await outcome.WaitAsync(Timeout, cancellationToken);
        var childId = TaskId.CreateRoot("workflow").Child(UndecodableStartWorkflowGrain.StartId);
        Assert.Equal(
            $"{childId} {targetId}|The invocation of durable task '{childId}' wasn't delivered to grain '{targetId}': {retired.Retirement.Reason}",
            response.GetResult<string>());
        Assert.Empty(await target.GetInboxDeadLettersAsync());
    }

    private static async Task<MessagingEvents.OutboxRetryScheduled> WaitForRetryAsync(
        DiagnosticEventCollector events,
        GrainId starterId,
        int attemptCount) =>
        (MessagingEvents.OutboxRetryScheduled)(await events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxRetryScheduled),
            evt => evt.Payload is MessagingEvents.OutboxRetryScheduled retry
                && retry.GrainContext.GrainId == starterId
                && retry.AttemptCount == attemptCount,
            Timeout,
            TestContext.Current.CancellationToken)).Payload!;
}

/// <summary>Runs undecodable start coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskUndecodableStartTests() : DurableTaskUndecodableStartTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs undecodable start coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskUndecodableStartTests() : DurableTaskUndecodableStartTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface IUndecodableStartWorkflowGrain : IGrainWithGuidKey
{
    DurableTask<string> StartAsync(Guid targetKey);
}

public interface IUndecodableStartTargetGrain : IGrainWithGuidKey
{
    DurableTask<int> AcceptAsync(int value);
    Task<DeadLetter<InboxMessage>[]> GetInboxDeadLettersAsync();
}

[GrainType("durable-task-undecodable-start-workflow-test")]
public sealed class UndecodableStartWorkflowGrain : DurableGrain, IUndecodableStartWorkflowGrain
{
    public const string StartId = "start";

    public async DurableTask<string> StartAsync(Guid targetKey)
    {
        var target = GrainFactory.GetGrain<IUndecodableStartTargetGrain>(targetKey);
        try
        {
            await target.AcceptAsync(1).WithId(StartId).ScheduleAsync();
            return "accepted";
        }
        catch (DurableTaskDeliveryException undelivered)
        {
            return $"{undelivered.TaskId} {undelivered.Target}|{undelivered.Message}";
        }
    }
}

[GrainType("durable-task-undecodable-start-target-test")]
public sealed class UndecodableStartTargetGrain(IMessagingDiagnostics diagnostics) : DurableGrain, IUndecodableStartTargetGrain
{
    public DurableTask<int> AcceptAsync(int value) => DurableTask.FromResult(value);

    public Task<DeadLetter<InboxMessage>[]> GetInboxDeadLettersAsync() => Task.FromResult(diagnostics.InboxDeadLetters.ToArray());
}
