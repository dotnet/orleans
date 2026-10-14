#nullable enable
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies that a durable execution observes the terminal result of an invoked task which it didn't invoke: the record
/// in the observer's tree, the subscription and completion messages, replay, cancellation, and both sides' retention.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskObservationTests
{
    private static readonly DurableSignal<string> Release = new("release");
    private static readonly DurableSignal<string> Continue = new("continue");
    private static readonly GrainId ObserverGrain = GrainId.Create("observer", "one");
    private static readonly GrainId OperationGrain = GrainId.Create("operation", "one");
    private static readonly TaskId OperationId = TaskId.CreateRoot("op");
    private static readonly TaskId RelayId = TaskId.CreateRoot("relay");
    private static readonly TaskId ObservationId = RelayId.Child("$child-1");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task ObservationOfAnotherGrainCompletesWithTheResultWhichItsOutboxDelivers() => ActivationScheduler.RunAsync(async () =>
    {
        await using var observerHost = new ObservationHost(ObserverGrain);
        await using var operationHost = new ObservationHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        observerHost.Workflows["relay"] = () => Observe(operationHost.Address, "op");
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var observer = await observerHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);

        await observer.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await observer.Transport.WaitForObservationAsync(1);
        await observer.WriteAsync(TestCancellation);

        // The observation is a child of the relay, recorded with its subscription.
        Assert.Equal(new RecordedObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, Unsubscribe: false), subscription);
        Assert.StartsWith("$observe:", observer.Get(ObservationId).RequestFingerprint, StringComparison.Ordinal);
        Assert.Equal(
            $"an observation of task 'op' on grain '{OperationGrain}'",
            (await observer.GetDiagnosticsAsync(TestCancellation))[ObservationId].Request);

        // The operation's grain records the subscription in the operation's tree; a redelivery changes nothing.
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        var record = Assert.Single(operation.ObserverRecords(OperationId));
        Assert.Empty(operation.Transport.Completions);

        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        Assert.Equal("done", (await operation.GetResponseAsync(OperationId, TestCancellation)).GetResult<string>());
        var completion = Assert.Single(operation.Transport.Completions);
        Assert.Equal((OperationGrain, ObserverGrain, OperationId), (completion.Sender, completion.Target, completion.TaskId));
        var message = completion.Materialize(operation.Runtime);
        Assert.Equal((OperationId, ObservationId, "done"), (message.TaskId, message.ObserverId, message.Response.GetResult<string>()));

        await observer.DeliverAsync(operation.CreateInboxMessage(completion), TestCancellation);
        Assert.Equal("done", (await observer.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());

        // Retirement removes the record. A client-scheduled operation keeps its result for its retention period.
        ((IOutboxMessageSource)operation.Runtime).OnRetired(completion.MessageId, OutboxMessageRetirement.Delivered);
        await operation.WriteAsync(TestCancellation);
        Assert.False(operation.Contains(record));
        Assert.Equal("done", (await operation.GetResponseAsync(OperationId, TestCancellation)).GetResult<string>());
        Assert.Single(observer.Transport.Observations);
    });

    [Fact]
    public Task ReplayReachesTheRecordedObservationWithoutSubscribingAgain() => ActivationScheduler.RunAsync(async () =>
    {
        await using var observerHost = new ObservationHost(ObserverGrain);
        await using var operationHost = new ObservationHost(OperationGrain);
        observerHost.Workflows["relay"] = () => Observe(operationHost.Address, "op");
        operationHost.Workflows["op"] = static () => WaitForRelease();
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        RecordedObservation subscription;
        await using (var first = await observerHost.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(RelayId, "relay", TestCancellation);
            subscription = await first.Transport.WaitForObservationAsync(1);
            await first.WriteAsync(TestCancellation);
        }

        await using var replacement = await observerHost.ActivateAsync(TestCancellation);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        await replacement.DeliverAsync(operation.CreateInboxMessage(Assert.Single(operation.Transport.Completions)), TestCancellation);

        Assert.Equal("done", (await replacement.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Empty(replacement.Transport.Observations);
    });

    [Fact]
    public Task ObservationOfATerminalTaskCompletesPromptlyAndOfANonInvokedTaskAsNotFound() => ActivationScheduler.RunAsync(async () =>
    {
        await using var observerHost = new ObservationHost(ObserverGrain);
        await using var operationHost = new ObservationHost(OperationGrain);
        var stepId = OperationId.Child("step");
        operationHost.Workflows["op"] = static () => RunStep();
        observerHost.Workflows["relay"] = () => ObserveTwiceThenContinue(operationHost.Address, stepId);
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);

        await using (var first = await observerHost.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(RelayId, "relay", TestCancellation);
            var subscription = await first.Transport.WaitForObservationAsync(1);

            // The terminal task's completion is sent with the subscription's record.
            await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
            var completion = Assert.Single(operation.Transport.Completions);
            Assert.Single(operation.ObserverRecords(OperationId));
            await first.DeliverAsync(operation.CreateInboxMessage(completion), TestCancellation);

            // A stored task which isn't an invoked task completes the observation as not found at once.
            var step = await first.Transport.WaitForObservationAsync(2);
            Assert.Equal(stepId, step.TaskId);
            await operation.DeliverAsync(step.ToInboxMessage(), TestCancellation);
            var notFound = Assert.Single(operation.Transport.ObservationsNotFound);
            Assert.Equal(step with { Sender = OperationGrain, Target = ObserverGrain }, notFound);
            Assert.Empty(operation.ObserverRecords(stepId));
            await first.DeliverAsync(notFound.ToNotFoundInboxMessage(), TestCancellation);
            await first.WriteAsync(TestCancellation);
        }

        // A replay observes the recorded outcomes without subscribing again.
        await using var replacement = await observerHost.ActivateAsync(TestCancellation);
        await replacement.Tasks.SignalAsync(RelayId, Continue, "go", TestCancellation);
        Assert.Equal("done|not found: op/step", (await replacement.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Empty(replacement.Transport.Observations);
    });

    [Fact]
    public Task SubscriptionWhichPrecedesTheInvocationAwaitsItUntilTheSendersRetryTimeout() => ActivationScheduler.RunAsync(async () =>
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var retryTimeout = new DurableMessagingOptions().OutboxRetryTimeout;
        await using var operationHost = new ObservationHost(OperationGrain, time);
        operationHost.Workflows["op"] = static () => DurableTask.FromResult("done");
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        var early = new RecordedObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, Unsubscribe: false);
        var missingId = TaskId.CreateRoot("missing");
        var missing = new RecordedObservation(ObserverGrain, OperationGrain, missingId, RelayId.Child("$child-2"), Unsubscribe: false);

        // Each subscription is recorded for its invocation, with a resume which wakes the grain at its deadline.
        await operation.DeliverAsync(early.ToInboxMessage(), TestCancellation);
        await operation.DeliverAsync(missing.ToInboxMessage(), TestCancellation);
        Assert.Single(operation.ObserverRecords(OperationId));
        var missingRecord = Assert.Single(operation.ObserverRecords(missingId));
        Assert.Empty(operation.Transport.ObservationsNotFound);
        Assert.Contains(operation.Transport.Resumes, resume => resume.TaskId == missingRecord && resume.DueTime == time.GetUtcNow() + retryTimeout);

        // The invocation which arrives finds the subscription in its tree.
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        var completion = Assert.Single(operation.Transport.Completions);
        Assert.Equal(ObservationId, completion.Materialize(operation.Runtime).ObserverId);

        // The other subscription's task never arrives, so its deadline completes its observation as not found.
        time.Advance(retryTimeout - TimeSpan.FromTicks(1));
        await operation.WriteAsync(TestCancellation);
        Assert.Single(operation.ObserverRecords(missingId));
        time.Advance(TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => operation.Runtime.SubscribeOrPollAsync(missingId, new SubscribeOrPollOptions(), TestCancellation).AsTask());
        Assert.Equal(missing with { Sender = OperationGrain, Target = ObserverGrain }, Assert.Single(operation.Transport.ObservationsNotFound));
        Assert.Empty(operation.ObserverRecords(missingId));
    });

    [Fact]
    public Task ObservationOfTheSameGrainCompletesInTheWriteWhichStoresTheResult() => ActivationScheduler.RunAsync(async () =>
    {
        await using var host = new ObservationHost(OperationGrain);
        host.Workflows["op"] = static () => WaitForRelease();
        host.Workflows["relay"] = () => Observe(host.Address, "op");
        await using var grain = await host.ActivateAsync(TestCancellation);
        await grain.ScheduleAsync(OperationId, "op", TestCancellation);
        await grain.ScheduleAsync(RelayId, "relay", TestCancellation);
        await grain.WaitUntilStoredAsync(ObservationId);
        var record = Assert.Single(grain.ObserverRecords(OperationId));

        await grain.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);

        Assert.Equal("done", (await grain.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.False(grain.Contains(record));
        Assert.Empty(grain.Transport.Observations);
        Assert.Empty(grain.Transport.Completions);
        Assert.Equal(grain.Get(OperationId).CompletionSequence, grain.Get(ObservationId).CompletionSequence - 1);
    });

    [Fact]
    public Task DeadlineEndsTheObservationAndItsSubscriptionWithoutCancelingTheObservedTask() => ActivationScheduler.RunAsync(async () =>
    {
        await using var observerHost = new ObservationHost(ObserverGrain);
        await using var operationHost = new ObservationHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        observerHost.Workflows["relay"] = () => ObserveWithDeadline(operationHost.Address);
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var observer = await observerHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await observer.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await observer.Transport.WaitForObservationAsync(1);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);

        var resume = await observer.WaitForResumeAsync();
        observer.Runtime.ResumeDelayFromInbox(resume.TaskId, resume.Generation, ObserverGrain);
        await observer.WriteAsync(TestCancellation);

        Assert.Equal("timed out", (await observer.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        var unsubscription = await observer.Transport.WaitForObservationAsync(2);
        Assert.Equal(subscription with { Unsubscribe = true }, unsubscription);
        Assert.Equal(DurableTaskStatus.Canceled, observer.Get(subscription.ObserverId).Result!.Status);

        // The subscription ends; the observed task continues and completes without a completion for the observation.
        await operation.DeliverAsync(unsubscription.ToInboxMessage(), TestCancellation);
        Assert.Empty(operation.ObserverRecords(OperationId));
        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        Assert.Equal("done", (await operation.GetResponseAsync(OperationId, TestCancellation)).GetResult<string>());
        Assert.Empty(operation.Transport.Completions);
    });

    [Fact]
    public Task RaceSelectsTheObservationAndCancelsTheDelay() => ActivationScheduler.RunAsync(async () =>
    {
        await using var observerHost = new ObservationHost(ObserverGrain);
        await using var operationHost = new ObservationHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        observerHost.Workflows["relay"] = () => RaceWithDelay(operationHost.Address);
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var observer = await observerHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await observer.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await observer.Transport.WaitForObservationAsync(1);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);

        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        await observer.DeliverAsync(operation.CreateInboxMessage(Assert.Single(operation.Transport.Completions)), TestCancellation);

        Assert.Equal("0:done", (await observer.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Single(observer.Transport.Observations);
    });

    [Fact]
    public Task InvokedTaskIsRetainedUntilBothItsCallersAndItsObservationsCompletionsRetire() => ActivationScheduler.RunAsync(async () =>
    {
        var caller = GrainId.Create("caller", "one");
        await using var operationHost = new ObservationHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleFromInboxAsync(OperationId, "op", caller, TestCancellation);
        var subscription = new RecordedObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, Unsubscribe: false);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);

        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        var completions = operation.Transport.Completions;
        Assert.Equal(2, completions.Count);
        var toCaller = Assert.Single(completions, completion => completion.Target == caller);
        var toObserver = Assert.Single(completions, completion => completion.Target == ObserverGrain);

        ((IOutboxMessageSource)operation.Runtime).OnRetired(toCaller.MessageId, OutboxMessageRetirement.Delivered);
        await operation.WriteAsync(TestCancellation);
        Assert.True(operation.Contains(OperationId));

        ((IOutboxMessageSource)operation.Runtime).OnRetired(toObserver.MessageId, OutboxMessageRetirement.Delivered);
        await operation.WriteAsync(TestCancellation);
        Assert.Empty(operation.Ids);
    });

    [Fact]
    public Task RecoveredGrainProvidesPendingObservationCompletionsAndDeadLetteringRestartsRetention() => ActivationScheduler.RunAsync(async () =>
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var operationHost = new ObservationHost(OperationGrain, time);
        operationHost.Workflows["op"] = static () => DurableTask.FromResult("done");
        var subscription = new RecordedObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, Unsubscribe: false);
        await using (var first = await operationHost.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(OperationId, "op", TestCancellation);
            await first.GetResponseAsync(OperationId, TestCancellation);
            await first.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
            Assert.Single(first.Transport.Completions);
        }

        // The recovered grain materializes the pending completion from the record and the stored result.
        time.Advance(TimeSpan.FromHours(2));
        await using var replacement = await operationHost.ActivateAsync(TestCancellation);
        var messageId = DurableTaskMessageTransport.CreateMessageId(
            OperationGrain,
            ObserverGrain,
            OperationId,
            DurableTaskMessageTypes.CompletionSubject,
            ObservationId.ToString());
        Assert.True(((IOutboxMessageSource)replacement.Runtime).TryGetMessage(messageId, out var message));
        var body = MessagingTestHelpers.MessageTypes.Completion.Decode(message.Envelope);
        Assert.Equal((OperationId, ObservationId, "done"), (body.TaskId, body.ObserverId, body.Response.GetResult<string>()));

        // The pending completion held the task beyond its retention period; a dead letter restarts that period.
        ((IOutboxMessageSource)replacement.Runtime).OnRetired(messageId, OutboxMessageRetirement.DeadLettered);
        await replacement.WriteAsync(TestCancellation);
        Assert.Equal([OperationId], replacement.Ids);
        Assert.Equal(time.GetUtcNow(), replacement.Get(OperationId).RetainedSince);

        time.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => replacement.Runtime.SubscribeOrPollAsync(OperationId, new SubscribeOrPollOptions(), TestCancellation).AsTask());
        Assert.Empty(replacement.Ids);
    });

    [Fact]
    public Task AbortingTheObservingExecutionEndsTheSubscription() => ActivationScheduler.RunAsync(async () =>
    {
        await using var observerHost = new ObservationHost(ObserverGrain);
        observerHost.Workflows["relay"] = static () => Observe(new GrainAddress(OperationGrain), "op");
        await using var observer = await observerHost.ActivateAsync(TestCancellation);
        await observer.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await observer.Transport.WaitForObservationAsync(1);
        await observer.WriteAsync(TestCancellation);

        await observer.Tasks.AbortAsync(RelayId, "stopped", TestCancellation);

        Assert.Equal(subscription with { Unsubscribe = true }, await observer.Transport.WaitForObservationAsync(2));
        Assert.IsType<DurableTaskAbortedException>(observer.Get(ObservationId).Result!.Exception);

        // A completion which arrives for the ended observation changes nothing.
        await observer.DeliverAsync(
            MessagingTestHelpers.MessageTypes.Completion.Create(
                DurableTaskMessageTransport.CreateMessageId(OperationGrain, ObserverGrain, OperationId, DurableTaskMessageTypes.CompletionSubject, ObservationId.ToString()),
                ObserverGrain,
                new DurableTaskCompletionMessage { TaskId = OperationId, Response = DurableTaskResponse.FromResult("late"), Sender = OperationGrain, ObserverId = ObservationId }).ToInboxMessage(),
            TestCancellation);
        Assert.IsType<DurableTaskAbortedException>(observer.Get(ObservationId).Result!.Exception);
    });

    [Fact]
    public void ObservationIsCreatedOnlyInADurableExecutionOfAGrain()
    {
        var target = new GrainAddress(OperationGrain);

        Assert.Throws<InvalidOperationException>(() => target.ObserveDurableTask<string>("op"));
        Assert.Throws<ArgumentException>(() => target.ObserveDurableTask<string>(default(TaskId)));
        Assert.Throws<ArgumentException>(() => target.ObserveDurableTask<string>(" "));
    }

    [Fact]
    public void TransportStagesObservationMessagesWithObservationScopedIdentities()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var other = RelayId.Child("$child-2");

        transport.SendObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, unsubscribe: false);
        transport.SendObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, unsubscribe: true);
        transport.SendObservation(ObserverGrain, OperationGrain, OperationId, other, unsubscribe: false);
        transport.SendObservationNotFound(OperationGrain, ObserverGrain, OperationId, ObservationId);

        var ids = outbox.Messages.Select(static message => message.Envelope.MessageId).ToList();
        Assert.Equal(4, ids.Distinct().Count());
        Assert.Equal(new RecordedObservation(ObserverGrain, OperationGrain, OperationId, ObservationId, false).ToInboxMessage().Envelope.MessageId, ids[0]);
        var completionIds = new[]
        {
            transport.GetCompletionMessageId(OperationGrain, ObserverGrain, OperationId),
            transport.GetObservationCompletionMessageId(OperationGrain, ObserverGrain, OperationId, ObservationId),
            transport.GetObservationCompletionMessageId(OperationGrain, ObserverGrain, OperationId, other),
            ids[3],
        };
        Assert.Equal(4, completionIds.Distinct().Count());
        var body = MessagingTestHelpers.MessageTypes.Observation.Decode(outbox.Messages[1].Envelope);
        Assert.Equal((OperationId, ObservationId, ObserverGrain, true), (body.TaskId, body.ObserverId, body.Sender, body.Unsubscribe));
        var notFound = MessagingTestHelpers.MessageTypes.Completion.Decode(outbox.Messages[3].Envelope);
        Assert.Equal((OperationId, ObservationId), (notFound.TaskId, notFound.ObserverId));
        Assert.IsType<DurableTaskNotFoundException>(notFound.Response.Exception);
    }

    private static async DurableTask<string> WaitForRelease() => await Release.WaitAsync();

    private static async DurableTask<string> Observe(IAddressable target, string rootId) =>
        await target.ObserveDurableTask<string>(rootId);

    private static async DurableTask<string> RunStep() => await DurableTask.Run(static _ => "done").WithId("step");

    private static async DurableTask<string> ObserveTwiceThenContinue(IAddressable target, TaskId stepId)
    {
        var first = await target.ObserveDurableTask<string>("op");
        string second;
        try
        {
            second = await target.ObserveDurableTask<string>(stepId);
        }
        catch (DurableTaskNotFoundException exception)
        {
            second = $"not found: {exception.TaskId}";
        }

        await Continue.WaitAsync();
        return $"{first}|{second}";
    }

    private static async DurableTask<string> ObserveWithDeadline(IAddressable target)
    {
        try
        {
            return await target.ObserveDurableTask<string>("op").WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException)
        {
            return "timed out";
        }
    }

    private static async DurableTask<string> RaceWithDelay(IAddressable target)
    {
        var (index, result) = await DurableTask.Race([target.ObserveDurableTask<string>("op"), DelayThenReturn()]);
        return $"{index}:{result}";

        static async DurableTask<string> DelayThenReturn()
        {
            await DurableTask.Delay(TimeSpan.FromHours(1));
            return "timeout";
        }
    }

    /// <summary>A grain identity which observations can address.</summary>
    private sealed class GrainAddress(GrainId grainId) : IGrainBase, IAddressable
    {
        public IGrainContext GrainContext { get; } = new TestGrainContext(grainId);
    }

    /// <summary>Hosts one grain's journaled task state and runtime, recovering each activation from the committed journal.</summary>
    private sealed class ObservationHost(GrainId grainId, TimeProvider? timeProvider = null) : IObservationWorkflows, IAsyncDisposable
    {
        private readonly DurableTaskJournalHarness _journal = new(DurableTaskJournalHarness.OrleansBinary, timeProvider);
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        public GrainId GrainId => grainId;

        public IAddressable Address { get; } = new GrainAddress(grainId);

        public Dictionary<string, Func<DurableTask>> Workflows { get; } = [];

        public async Task<ObservationActivation> ActivateAsync(CancellationToken cancellationToken)
        {
            var scope = _journal.CreateScope();
            try
            {
                var services = scope.ServiceProvider;
                var storage = services.GetRequiredService<DurableTaskGrainStorage>();
                var logicalTimes = services.GetRequiredKeyedService<IDurableDictionary<TaskId, DateTimeOffset>>("$task-logical-times");
                var manager = services.GetRequiredService<IJournaledStateManager>();
                var grainContext = Substitute.For<IGrainContext>();
                grainContext.GrainId.Returns(grainId);
                grainContext.GetTarget().Returns(this);
                var shared = new DurableTaskGrainRuntimeShared(
                    new TestGrainContextAccessor(grainContext),
                    _timeProvider,
                    NullLogger<DurableTaskGrainRuntime>.Instance,
                    Options.Create(new DurableTaskOptions { ResultRetentionPeriod = TimeSpan.FromHours(1) }),
                    Options.Create(new DurableMessagingOptions()),
                    _journal.Serializer);
                var transport = new RecordingDurableTaskMessageTransport();
                var runtime = new DurableTaskGrainRuntime(storage, logicalTimes, shared, [transport]);
                manager.RegisterStateMachine(DurableTaskGrainRuntime.StateMachineName, runtime);
                runtime.InitializeForActivation();
                await storage.ReadAsync(cancellationToken);
                await runtime.RecoverAsync(cancellationToken);
                runtime.StartRecoveredExecutions();
                return new(scope, runtime, storage, manager, transport, grainId);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => _journal.DisposeAsync();
    }

    private sealed class ObservationActivation(
        AsyncServiceScope scope,
        DurableTaskGrainRuntime runtime,
        DurableTaskGrainStorage storage,
        IJournaledStateManager manager,
        RecordingDurableTaskMessageTransport transport,
        GrainId grainId) : IAsyncDisposable
    {
        private readonly DurableTaskMessageHandler _handler = new(runtime, MessagingTestHelpers.MessageTypes);

        public DurableTaskGrainRuntime Runtime => runtime;

        public IDurableTaskManager Tasks => runtime;

        public RecordingDurableTaskMessageTransport Transport => transport;

        public IReadOnlyList<TaskId> Ids => [.. storage.Tasks.Select(static task => task.Id).OrderBy(static id => id.ToString(), StringComparer.Ordinal)];

        public IDurableTaskState Get(TaskId taskId) =>
            storage.TryGetTask(taskId, out var state) ? state : throw new KeyNotFoundException(taskId.ToString());

        public bool Contains(TaskId taskId) => storage.TryGetTask(taskId, out _);

        public IReadOnlyList<TaskId> ObserverRecords(TaskId taskId) =>
            [.. storage.GetChildren(taskId)
                .Where(static child => child.State.RequestFingerprint?.StartsWith("$observer:", StringComparison.Ordinal) is true)
                .Select(static child => child.Id)];

        public async Task ScheduleAsync(TaskId taskId, string workflow, CancellationToken cancellationToken) =>
            await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, CreateRequest(workflow, caller: default), cancellationToken);

        public async Task ScheduleFromInboxAsync(TaskId taskId, string workflow, GrainId caller, CancellationToken cancellationToken)
        {
            runtime.ScheduleFromInbox(taskId, CreateRequest(workflow, caller));
            await WriteAsync(cancellationToken);
        }

        /// <summary>Accepts a message as the inbox does and commits the changes it staged.</summary>
        public async Task DeliverAsync(InboxMessage message, CancellationToken cancellationToken)
        {
            Assert.True(_handler.TryAccept(message, out var result));
            Assert.Equal(DeliveryStatus.Accepted, result.Status);
            await WriteAsync(cancellationToken);
        }

        /// <summary>Materializes a completion which this grain staged, as its outbox does for delivery.</summary>
        public InboxMessage CreateInboxMessage(RecordedCompletion completion)
        {
            Assert.True(((IOutboxMessageSource)runtime).TryGetMessage(completion.MessageId, out var message));
            return message.ToInboxMessage();
        }

        public ValueTask WriteAsync(CancellationToken cancellationToken) => manager.WriteStateAsync(cancellationToken);

        public async Task<DurableTaskResponse> GetResponseAsync(TaskId taskId, CancellationToken cancellationToken) =>
            await Tasks.GetTask(taskId).GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public async Task<Dictionary<TaskId, DurableTaskDiagnosticState>> GetDiagnosticsAsync(CancellationToken cancellationToken)
        {
            var states = new Dictionary<TaskId, DurableTaskDiagnosticState>();
            await foreach (var (taskId, state) in ((IDurableTaskGrainExtension)runtime).GetTasksAsync(cancellationToken))
            {
                states.Add(taskId, state);
            }

            return states;
        }

        public async Task<(GrainId GrainId, TaskId TaskId, long Generation, DateTimeOffset DueTime)> WaitForResumeAsync()
        {
            await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            return Assert.Single(transport.Resumes);
        }

        /// <summary>Waits until the grain stages <paramref name="taskId"/>, which a running execution schedules.</summary>
        public async Task WaitUntilStoredAsync(TaskId taskId)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!Contains(taskId))
            {
                await Task.Delay(5, timeout.Token);
            }

            await WriteAsync(TestCancellation);
        }

        private ObservationWorkflowRequest CreateRequest(string workflow, GrainId caller) => new()
        {
            Workflow = workflow,
            Context = new DurableTaskRequestContext
            {
                CallerId = caller,
                TargetId = grainId,
                SupportsDurableCompletion = !caller.IsDefault,
            },
        };

        public async ValueTask DisposeAsync()
        {
            await runtime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await scope.DisposeAsync();
        }
    }
}

/// <summary>A serializable request which runs a named workflow of the observation test host which it targets.</summary>
[GenerateSerializer]
[Alias("Microsoft.Orleans.DurableTasks.Tests.ObservationWorkflowRequest")]
internal sealed class ObservationWorkflowRequest : IDurableTaskRequest
{
    [NonSerialized]
    private object? _host;

    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public string Workflow { get; set; } = string.Empty;

    [Id(2)]
    public InvokeMethodOptions Options { get; private set; }

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    public object? GetTarget() => _host;

    public void SetTarget(ITargetHolder holder) => _host = holder.GetTarget();

    public ValueTask<Response> Invoke() => throw new NotSupportedException("Durable task requests are not invoked directly.");

    public int GetArgumentCount() => 1;

    public object? GetArgument(int index) => index == 0 ? Workflow : throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) => Workflow = index == 0 ? (string)value : throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => "Run";

    public string GetInterfaceName() => "Microsoft.Orleans.DurableTasks.Tests.IObservationGrain";

    public string GetActivityName() => "Microsoft.Orleans.DurableTasks.Tests.IObservationGrain/Run";

    public MethodInfo GetMethod() => typeof(ObservationWorkflowRequest).GetMethod(nameof(GetMethod))!;

    public Type GetInterfaceType() => typeof(ObservationWorkflowRequest);

    public void Dispose()
    {
    }

    public DurableTask CreateTask() =>
        (_host as IObservationWorkflows ?? throw new InvalidOperationException("The request has no target.")).Workflows[Workflow]();
}

/// <summary>The named workflows of an observation test host.</summary>
internal interface IObservationWorkflows
{
    Dictionary<string, Func<DurableTask>> Workflows { get; }
}
