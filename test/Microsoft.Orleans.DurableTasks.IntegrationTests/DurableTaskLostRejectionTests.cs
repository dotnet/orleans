using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
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
/// Verifies that a start which its target rejects stays rejected when the starter's outbox doesn't receive the
/// rejection and delivers the invocation again.
/// </summary>
/// <remarks>
/// A <see cref="LostRejectionFilter"/> on the starter's delivery calls lets the target reject the invocation, then fails the
/// starter's call, as a call whose answer is lost does, so the starter's outbox retries the delivery on the Durable Jobs
/// clock, which the test advances. The retry reaches the target's inbox, which already processed the invocation.
/// </remarks>
public abstract class DurableTaskLostRejectionTests : IAsyncLifetime
{
    private const string TakenRootId = "taken";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly LostRejectionFilter _deliveries = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskLostRejectionTests(string journalFormatKey)
    {
        _fixture = new(journalFormatKey, silo => silo.AddOutgoingGrainCallFilter(_deliveries).Services.Configure<DurableMessagingOptions>(options =>
        {
            options.RetryInitialInterval = RetryDelay;
        }));
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ConflictingStartWhoseRejectionIsLostFailsWithConflictWhenItsInvocationIsDeliveredAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var starter = _fixture.Client.GetGrain<ILostRejectionStarterGrain>(Guid.NewGuid());
        var target = _fixture.Client.GetGrain<ILostRejectionTargetGrain>(Guid.NewGuid());
        var starterId = starter.GetGrainId();
        using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);
        await target.EchoAgainAsync(1).WithId(TakenRootId).ScheduleAsync(cancellationToken);

        var workflow = await starter.StartAsync(target.GetPrimaryKey(), TakenRootId).WithId("workflow").ScheduleAsync(cancellationToken);
        var outcome = workflow.GetResponseAsync(cancellationToken);

        // The target rejects the conflicting start, but the starter doesn't receive the rejection, so its outbox
        // reschedules the invocation.
        var lost = await _deliveries.Lost.WaitAsync(Timeout, cancellationToken);
        Assert.Equal(DurableTaskRejectionCodes.Conflict, lost.Code);
        var retry = (MessagingEvents.OutboxRetryScheduled)(await events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxRetryScheduled),
            evt => evt.Payload is MessagingEvents.OutboxRetryScheduled retry && retry.GrainContext.GrainId == starterId,
            Timeout,
            cancellationToken)).Payload!;
        Assert.Equal(target.GetGrainId(), retry.ReceiverId);
        Assert.False(outcome.IsCompleted);

        // The target answers the redelivered invocation with its original rejection.
        _fixture.JobClock.Advance(RetryDelay * 2);
        var retired = (MessagingEvents.OutboxMessageRetired)(await events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxMessageRetired),
            evt => evt.Payload is MessagingEvents.OutboxMessageRetired retired
                && retired.GrainContext.GrainId == starterId
                && retired.MessageId == retry.MessageId,
            Timeout,
            cancellationToken)).Payload!;
        Assert.Equal(OutboxMessageRetirementStatus.Rejected, retired.Retirement.Status);
        Assert.Equal(DurableTaskRejectionCodes.Conflict, retired.Retirement.Code);
        Assert.Equal(lost.Message, retired.Retirement.Reason);
        Assert.Equal([lost, lost], _deliveries.Answers);

        var response = await outcome.WaitAsync(Timeout, cancellationToken);
        Assert.Equal($"conflict: {TakenRootId}", response.GetResult<string>());
        Assert.Equal(1, (await target.GetDurableTask<int>(TakenRootId).GetResponseAsync(cancellationToken)).GetResult<int>());
    }

    /// <summary>
    /// Fails the starter's call after the target's first rejection, as a call whose answer is lost does, and records
    /// every rejection which the target answers.
    /// </summary>
    private sealed class LostRejectionFilter : IOutgoingGrainCallFilter
    {
        private readonly TaskCompletionSource<DeliveryResult> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<DeliveryResult> _answers = [];

        /// <summary>Gets the rejection whose answer the filter hid from the starter.</summary>
        public Task<DeliveryResult> Lost => _lost.Task;

        /// <summary>Gets the rejections which the target answered, whether or not the starter received them.</summary>
        public IReadOnlyList<DeliveryResult> Answers
        {
            get
            {
                lock (_answers) return [.. _answers];
            }
        }

        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            await context.Invoke();
            if (context.InterfaceMethod?.DeclaringType == typeof(IMessagingExtension)
                && context.Result is DeliveryResult { Status: DeliveryStatus.DeadLettered } answer)
            {
                lock (_answers) _answers.Add(answer);
                if (_lost.TrySetResult(answer))
                {
                    throw new TimeoutException("Injected loss of the receiver's answer.");
                }
            }
        }
    }
}

/// <summary>Runs lost rejection coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskLostRejectionTests() : DurableTaskLostRejectionTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs lost rejection coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskLostRejectionTests() : DurableTaskLostRejectionTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface ILostRejectionStarterGrain : IGrainWithGuidKey
{
    DurableTask<string> StartAsync(Guid targetKey, string rootId);
}

public interface ILostRejectionTargetGrain : IGrainWithGuidKey
{
    DurableTask<int> EchoAsync(int value);

    /// <summary>Echoes through another method, so its task conflicts with an echo under the same root ID.</summary>
    DurableTask<int> EchoAgainAsync(int value);
}

[GrainType("durable-task-lost-rejection-starter-test")]
public sealed class LostRejectionStarterGrain : DurableGrain, ILostRejectionStarterGrain
{
    public async DurableTask<string> StartAsync(Guid targetKey, string rootId)
    {
        try
        {
            await GrainFactory.GetGrain<ILostRejectionTargetGrain>(targetKey).EchoAsync(2).Detached(rootId).ScheduleAsync();
            return "accepted";
        }
        catch (DurableTaskConflictException conflict)
        {
            return $"conflict: {conflict.TaskId}";
        }
    }
}

[GrainType("durable-task-lost-rejection-target-test")]
public sealed class LostRejectionTargetGrain : DurableGrain, ILostRejectionTargetGrain
{
    public DurableTask<int> EchoAsync(int value) => DurableTask.FromResult(value);

    public DurableTask<int> EchoAgainAsync(int value) => DurableTask.FromResult(value);
}
