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
/// Verifies that a durable execution subscribes to the terminal result of an invoked task which it didn't invoke through
/// GetDurableTask: the child in the subscriber's tree, the subscription and completion messages, replay, cancellation,
/// and both sides' retention.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskSubscriptionTests
{
    private static readonly DurableSignal<string> Release = new("release");
    private static readonly DurableSignal<string> Continue = new("continue");
    private static readonly GrainId SubscriberGrain = GrainId.Create("subscriber", "one");
    private static readonly GrainId OperationGrain = GrainId.Create("operation", "one");
    private static readonly TaskId OperationId = TaskId.CreateRoot("op");
    private static readonly TaskId RelayId = TaskId.CreateRoot("relay");
    private static readonly TaskId SubscriptionChildId = RelayId.Child("$child-1");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task SubscriptionToAnotherGrainsTaskCompletesWithTheResultWhichItsOutboxDelivers() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        subscriberHost.Workflows["relay"] = () => AwaitTask(operationHost.Address, "op");
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var subscriber = await subscriberHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);

        await subscriber.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await subscriber.Transport.WaitForSubscriptionAsync(1);
        await subscriber.WriteAsync(TestCancellation);

        // The subscription is a child of the relay, recorded with its subscription.
        Assert.Equal(new RecordedSubscription(SubscriberGrain, OperationGrain, OperationId, SubscriptionChildId, Unsubscribe: false), subscription);
        Assert.StartsWith("$subscribe:", subscriber.Get(SubscriptionChildId).RequestFingerprint, StringComparison.Ordinal);
        Assert.Equal(
            $"a subscription to task 'op' on grain '{OperationGrain}'",
            (await subscriber.GetDiagnosticsAsync(TestCancellation))[SubscriptionChildId].Request);

        // The operation's grain adds the subscriber to the operation's completion destinations; a redelivery changes nothing.
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        Assert.Equal(new DurableTaskSubscriber(SubscriberGrain, SubscriptionChildId), Assert.Single(operation.Subscribers(OperationId)));
        Assert.Empty(operation.Transport.Completions);

        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        Assert.Equal("done", (await operation.GetResponseAsync(OperationId, TestCancellation)).GetResult<string>());
        var completion = Assert.Single(operation.Transport.Completions);
        Assert.Equal((OperationGrain, SubscriberGrain, OperationId), (completion.Sender, completion.Target, completion.TaskId));
        var message = completion.Materialize(operation.Runtime);
        Assert.Equal((OperationId, SubscriptionChildId, "done"), (message.TaskId, message.SubscriberId, message.Response.GetResult<string>()));

        await subscriber.DeliverAsync(operation.CreateInboxMessage(completion), TestCancellation);
        Assert.Equal("done", (await subscriber.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());

        // Retirement removes the subscriber. A client-scheduled operation keeps its result for its retention period.
        ((IOutboxMessageSource)operation.Runtime).OnRetired(completion.MessageId, OutboxMessageRetirement.Delivered());
        await operation.WriteAsync(TestCancellation);
        Assert.Empty(operation.Subscribers(OperationId));
        Assert.Equal("done", (await operation.GetResponseAsync(OperationId, TestCancellation)).GetResult<string>());
        Assert.Single(subscriber.Transport.Subscriptions);
    });

    [Fact]
    public Task SubscriptionHandleNamesTheObservedTask() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        subscriberHost.Workflows["relay"] = () => ObserveTaskThroughItsHandle(operationHost.Address, "op");
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var subscriber = await subscriberHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await subscriber.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await subscriber.Transport.WaitForSubscriptionAsync(1);
        await subscriber.WriteAsync(TestCancellation);

        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        await subscriber.DeliverAsync(operation.CreateInboxMessage(Assert.Single(operation.Transport.Completions)), TestCancellation);

        // The handle's identifier is the subscription's record, and its target identifier is the observed task.
        Assert.Equal(
            $"{SubscriptionChildId} {OperationId} done",
            (await subscriber.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
    });

    [Fact]
    public Task ReplayReachesTheRecordedSubscriptionWithoutSubscribingAgain() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        await using var operationHost = new SubscriptionHost(OperationGrain);
        subscriberHost.Workflows["relay"] = () => AwaitTask(operationHost.Address, "op");
        operationHost.Workflows["op"] = static () => WaitForRelease();
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        RecordedSubscription subscription;
        await using (var first = await subscriberHost.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(RelayId, "relay", TestCancellation);
            subscription = await first.Transport.WaitForSubscriptionAsync(1);
            await first.WriteAsync(TestCancellation);
        }

        await using var replacement = await subscriberHost.ActivateAsync(TestCancellation);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        await replacement.DeliverAsync(operation.CreateInboxMessage(Assert.Single(operation.Transport.Completions)), TestCancellation);

        Assert.Equal("done", (await replacement.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Empty(replacement.Transport.Subscriptions);
    });

    [Fact]
    public Task SubscriptionToATerminalTaskCompletesPromptlyAndToANonInvokedTaskAsNotFound() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        await using var operationHost = new SubscriptionHost(OperationGrain);
        var stepId = OperationId.Child("step");
        operationHost.Workflows["op"] = static () => RunStep();
        subscriberHost.Workflows["relay"] = () => AwaitTwiceThenContinue(operationHost.Address, stepId);
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);

        await using (var first = await subscriberHost.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(RelayId, "relay", TestCancellation);
            var subscription = await first.Transport.WaitForSubscriptionAsync(1);

            // The terminal task's completion is sent when the subscriber is added.
            await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
            var completion = Assert.Single(operation.Transport.Completions);
            Assert.Single(operation.Subscribers(OperationId));
            await first.DeliverAsync(operation.CreateInboxMessage(completion), TestCancellation);

            // A stored task which isn't an invoked task completes the subscription as not found at once.
            var step = await first.Transport.WaitForSubscriptionAsync(2);
            Assert.Equal(stepId, step.TaskId);
            await operation.DeliverAsync(step.ToInboxMessage(), TestCancellation);
            var notFound = Assert.Single(operation.Transport.NotFound);
            Assert.Equal(step with { Sender = OperationGrain, Target = SubscriberGrain }, notFound);
            Assert.Empty(operation.Subscribers(stepId));
            await first.DeliverAsync(notFound.ToNotFoundInboxMessage(), TestCancellation);
            await first.WriteAsync(TestCancellation);
        }

        // A replay observes the recorded outcomes without subscribing again.
        await using var replacement = await subscriberHost.ActivateAsync(TestCancellation);
        await replacement.Tasks.SignalAsync(RelayId, Continue, "go", TestCancellation);
        Assert.Equal("done|not found: op/step", (await replacement.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Empty(replacement.Transport.Subscriptions);
    });

    [Fact]
    public Task SubscriptionToATaskWhichTheGrainDoesNotHoldCompletesAsNotFoundAtOnce() => ActivationScheduler.RunAsync(async () =>
    {
        var caller = GrainId.Create("caller", "one");
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => DurableTask.FromResult("done");
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        var missing = new RecordedSubscription(SubscriberGrain, OperationGrain, TaskId.CreateRoot("missing"), SubscriptionChildId, Unsubscribe: false);

        // The grain holds no state for a subscription to a task which it doesn't hold.
        await operation.DeliverAsync(missing.ToInboxMessage(), TestCancellation);
        Assert.Equal(missing with { Sender = OperationGrain, Target = SubscriberGrain }, Assert.Single(operation.Transport.NotFound));
        Assert.Empty(operation.Ids);
        Assert.Empty(operation.Transport.Resumes);

        // A cancellation which preceded its invocation holds no task to subscribe to either.
        operation.Runtime.SignalCancellationFromInbox(OperationId, caller, reason: null);
        await operation.WriteAsync(TestCancellation);
        var early = missing with { TaskId = OperationId, SubscriberId = RelayId.Child("$child-2") };
        await operation.DeliverAsync(early.ToInboxMessage(), TestCancellation);
        Assert.Equal(early with { Sender = OperationGrain, Target = SubscriberGrain }, operation.Transport.NotFound[1]);
        Assert.Empty(operation.Get(OperationId).CompletionDestinations);

        // The invocation still finds the cancellation, which its caller receives.
        await operation.ScheduleFromInboxAsync(OperationId, "op", caller, TestCancellation);
        Assert.Equal(DurableTaskStatus.Canceled, Assert.Single(operation.Transport.Completions).Materialize(operation.Runtime).Response.Status);
    });

    [Fact]
    public Task SubscriptionToATaskOfTheSameGrainCompletesInTheWriteWhichStoresTheResult() => ActivationScheduler.RunAsync(async () =>
    {
        await using var host = new SubscriptionHost(OperationGrain);
        host.Workflows["op"] = static () => WaitForRelease();
        host.Workflows["relay"] = () => AwaitTask(host.Address, "op");
        await using var grain = await host.ActivateAsync(TestCancellation);
        await grain.ScheduleAsync(OperationId, "op", TestCancellation);
        await grain.ScheduleAsync(RelayId, "relay", TestCancellation);
        await grain.WaitUntilStoredAsync(SubscriptionChildId);
        Assert.Equal(new DurableTaskSubscriber(OperationGrain, SubscriptionChildId), Assert.Single(grain.Subscribers(OperationId)));

        await grain.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);

        Assert.Equal("done", (await grain.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Empty(grain.Subscribers(OperationId));
        Assert.Empty(grain.Transport.Subscriptions);
        Assert.Empty(grain.Transport.Completions);
        Assert.Equal(grain.Get(OperationId).CompletionSequence, grain.Get(SubscriptionChildId).CompletionSequence - 1);
    });

    [Fact]
    public Task DeadlineEndsTheSubscriptionWithoutCancelingTheTask() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        subscriberHost.Workflows["relay"] = () => AwaitWithDeadline(operationHost.Address);
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var subscriber = await subscriberHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await subscriber.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await subscriber.Transport.WaitForSubscriptionAsync(1);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);

        var resume = await subscriber.WaitForResumeAsync();
        subscriber.Runtime.ResumeDelayFromInbox(resume.TaskId, resume.Generation, SubscriberGrain);
        await subscriber.WriteAsync(TestCancellation);

        Assert.Equal("timed out", (await subscriber.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        var unsubscription = await subscriber.Transport.WaitForSubscriptionAsync(2);
        Assert.Equal(subscription with { Unsubscribe = true }, unsubscription);
        Assert.Equal(DurableTaskStatus.Canceled, subscriber.Get(subscription.SubscriberId).Result!.Status);

        // The subscription ends; the task continues and completes without a completion for the subscriber.
        await operation.DeliverAsync(unsubscription.ToInboxMessage(), TestCancellation);
        Assert.Empty(operation.Subscribers(OperationId));
        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        Assert.Equal("done", (await operation.GetResponseAsync(OperationId, TestCancellation)).GetResult<string>());
        Assert.Empty(operation.Transport.Completions);
    });

    [Fact]
    public Task RaceSelectsTheSubscriptionAndCancelsTheDelay() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        subscriberHost.Workflows["relay"] = () => RaceWithDelay(operationHost.Address);
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await using var subscriber = await subscriberHost.ActivateAsync(TestCancellation);
        await operation.ScheduleAsync(OperationId, "op", TestCancellation);
        await subscriber.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await subscriber.Transport.WaitForSubscriptionAsync(1);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);

        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        await subscriber.DeliverAsync(operation.CreateInboxMessage(Assert.Single(operation.Transport.Completions)), TestCancellation);

        Assert.Equal("0:done", (await subscriber.GetResponseAsync(RelayId, TestCancellation)).GetResult<string>());
        Assert.Single(subscriber.Transport.Subscriptions);
    });

    [Fact]
    public Task InvokedTaskIsRetainedUntilItsInvokersAndSubscribersCompletionsRetire() => ActivationScheduler.RunAsync(async () =>
    {
        var caller = GrainId.Create("caller", "one");
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleFromInboxAsync(OperationId, "op", caller, TestCancellation);
        var subscription = new RecordedSubscription(SubscriberGrain, OperationGrain, OperationId, SubscriptionChildId, Unsubscribe: false);
        await operation.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);

        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);
        var completions = operation.Transport.Completions;
        Assert.Equal(2, completions.Count);
        var toCaller = Assert.Single(completions, completion => completion.Target == caller);
        var toSubscriber = Assert.Single(completions, completion => completion.Target == SubscriberGrain);

        ((IOutboxMessageSource)operation.Runtime).OnRetired(toCaller.MessageId, OutboxMessageRetirement.Delivered());
        await operation.WriteAsync(TestCancellation);
        Assert.True(operation.Contains(OperationId));

        ((IOutboxMessageSource)operation.Runtime).OnRetired(toSubscriber.MessageId, OutboxMessageRetirement.Delivered());
        await operation.WriteAsync(TestCancellation);
        Assert.Empty(operation.Ids);
    });

    [Fact]
    public Task RecoveredGrainProvidesPendingSubscriberCompletionsAndDeadLetteringRestartsRetention() => ActivationScheduler.RunAsync(async () =>
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var operationHost = new SubscriptionHost(OperationGrain, time);
        operationHost.Workflows["op"] = static () => DurableTask.FromResult("done");
        var subscription = new RecordedSubscription(SubscriberGrain, OperationGrain, OperationId, SubscriptionChildId, Unsubscribe: false);
        await using (var first = await operationHost.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(OperationId, "op", TestCancellation);
            await first.GetResponseAsync(OperationId, TestCancellation);
            await first.DeliverAsync(subscription.ToInboxMessage(), TestCancellation);
            Assert.Single(first.Transport.Completions);
        }

        // The recovered grain materializes the pending completion from the subscriber and the stored result.
        time.Advance(TimeSpan.FromHours(2));
        await using var replacement = await operationHost.ActivateAsync(TestCancellation);
        var messageId = DurableTaskMessageTransport.CreateMessageId(
            OperationGrain,
            SubscriberGrain,
            OperationId,
            DurableTaskMessageTypes.CompletionSubject,
            SubscriptionChildId.ToString());
        Assert.True(((IOutboxMessageSource)replacement.Runtime).TryGetMessage(messageId, out var message));
        var body = MessagingTestHelpers.MessageTypes.Completion.Decode(message.Envelope);
        Assert.Equal((OperationId, SubscriptionChildId, "done"), (body.TaskId, body.SubscriberId, body.Response.GetResult<string>()));

        // The pending completion held the task beyond its retention period; a dead letter restarts that period.
        ((IOutboxMessageSource)replacement.Runtime).OnRetired(messageId, OutboxMessageRetirement.DeadLettered("The outbox dead-lettered the completion."));
        await replacement.WriteAsync(TestCancellation);
        Assert.Equal([OperationId], replacement.Ids);
        Assert.Equal(time.GetUtcNow(), replacement.Get(OperationId).RetainedSince);

        time.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => replacement.Runtime.SubscribeOrPollAsync(OperationId, new SubscribeOrPollOptions(), TestCancellation).AsTask());
        Assert.Empty(replacement.Ids);
    });

    [Fact]
    public Task AbortingTheSubscribingExecutionEndsTheSubscription() => ActivationScheduler.RunAsync(async () =>
    {
        await using var subscriberHost = new SubscriptionHost(SubscriberGrain);
        subscriberHost.Workflows["relay"] = static () => AwaitTask(new GrainAddress(OperationGrain), "op");
        await using var subscriber = await subscriberHost.ActivateAsync(TestCancellation);
        await subscriber.ScheduleAsync(RelayId, "relay", TestCancellation);
        var subscription = await subscriber.Transport.WaitForSubscriptionAsync(1);
        await subscriber.WriteAsync(TestCancellation);

        await subscriber.Tasks.AbortAsync(RelayId, "stopped", TestCancellation);

        Assert.Equal(subscription with { Unsubscribe = true }, await subscriber.Transport.WaitForSubscriptionAsync(2));
        Assert.IsType<DurableTaskAbortedException>(subscriber.Get(SubscriptionChildId).Result!.Exception);

        // A completion which arrives for the ended subscription changes nothing.
        await subscriber.DeliverAsync(
            MessagingTestHelpers.MessageTypes.Completion.Create(
                DurableTaskMessageTransport.CreateMessageId(OperationGrain, SubscriberGrain, OperationId, DurableTaskMessageTypes.CompletionSubject, SubscriptionChildId.ToString()),
                SubscriberGrain,
                new DurableTaskCompletionMessage { TaskId = OperationId, Response = DurableTaskResponse.FromResult("late"), Sender = OperationGrain, SubscriberId = SubscriptionChildId }).ToInboxMessage(),
            TestCancellation);
        Assert.IsType<DurableTaskAbortedException>(subscriber.Get(SubscriptionChildId).Result!.Exception);
    });

    [Fact]
    public void ReferenceAddressesTheTaskWithoutSchedulingIt()
    {
        var target = new GrainAddress(OperationGrain);

        var reference = target.GetDurableTask<string>("op");

        Assert.Equal((OperationId, OperationGrain), (reference.Id, reference.Target));
        Assert.Equal(OperationId.Child("step"), target.GetDurableTask<string>(OperationId.Child("step")).Id);
        Assert.Throws<ArgumentException>(() => target.GetDurableTask<string>(default(TaskId)));
        Assert.Throws<ArgumentException>(() => target.GetDurableTask<string>(" "));
    }

    [Fact]
    public void TransportStagesSubscriptionsAsInvocationsWithSubscriberScopedIdentities()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var other = RelayId.Child("$child-2");

        transport.SendSubscription(SubscriberGrain, OperationGrain, OperationId, SubscriptionChildId);
        transport.SendUnsubscription(SubscriberGrain, OperationGrain, OperationId, SubscriptionChildId);
        transport.SendSubscription(SubscriberGrain, OperationGrain, OperationId, other);
        transport.SendResponse(OperationGrain, new(SubscriberGrain, SubscriptionChildId), OperationId, DurableTaskResponse.FromException(new DurableTaskNotFoundException(OperationId)));

        var ids = outbox.Messages.Select(static message => message.Envelope.MessageId).ToList();
        Assert.Equal(4, ids.Distinct().Count());
        Assert.DoesNotContain(DurableTaskMessageTransport.CreateMessageId(SubscriberGrain, OperationGrain, OperationId, DurableTaskMessageTypes.InvocationSubject), ids);
        Assert.DoesNotContain(DurableTaskMessageTransport.CreateMessageId(SubscriberGrain, OperationGrain, OperationId, DurableTaskMessageTypes.CancellationSubject), ids);
        Assert.Equal(
            [DurableTaskMessageTypes.InvocationSubject, DurableTaskMessageTypes.CancellationSubject, DurableTaskMessageTypes.InvocationSubject, DurableTaskMessageTypes.CompletionSubject],
            outbox.Messages.Select(static message => message.Envelope.Subject));
        var completionIds = new[]
        {
            transport.GetCompletionMessageId(OperationGrain, new(SubscriberGrain, OperationId), OperationId),
            transport.GetCompletionMessageId(OperationGrain, new(SubscriberGrain, SubscriptionChildId), OperationId),
            transport.GetCompletionMessageId(OperationGrain, new(SubscriberGrain, other), OperationId),
            ids[3],
        };
        Assert.Equal(4, completionIds.Distinct().Count());
        var subscription = MessagingTestHelpers.MessageTypes.Invocation.Decode(outbox.Messages[0].Envelope);
        Assert.Equal((OperationId, (TaskId?)SubscriptionChildId, SubscriberGrain), (subscription.TaskId, subscription.SubscriberId, subscription.Sender));
        Assert.Null(subscription.Request);
        var unsubscription = MessagingTestHelpers.MessageTypes.Cancellation.Decode(outbox.Messages[1].Envelope);
        Assert.Equal((OperationId, (TaskId?)SubscriptionChildId), (unsubscription.TaskId, unsubscription.SubscriberId));
        Assert.Null(unsubscription.AbortReason);
        var notFound = MessagingTestHelpers.MessageTypes.Completion.Decode(outbox.Messages[3].Envelope);
        Assert.Equal((OperationId, (TaskId?)SubscriptionChildId), (notFound.TaskId, notFound.SubscriberId));
        Assert.IsType<DurableTaskNotFoundException>(notFound.Response.Exception);
    }

    [Fact]
    public Task UnsubscriptionDoesNotRemoveTheInvoker() => ActivationScheduler.RunAsync(async () =>
    {
        var caller = GrainId.Create("caller", "one");
        await using var operationHost = new SubscriptionHost(OperationGrain);
        operationHost.Workflows["op"] = static () => WaitForRelease();
        await using var operation = await operationHost.ActivateAsync(TestCancellation);
        await operation.ScheduleFromInboxAsync(OperationId, "op", caller, TestCancellation);

        await operation.DeliverAsync(new RecordedSubscription(caller, OperationGrain, OperationId, OperationId, Unsubscribe: true).ToInboxMessage(), TestCancellation);
        await operation.Tasks.SignalAsync(OperationId, Release, "done", TestCancellation);
        await operation.GetResponseAsync(OperationId, TestCancellation);

        var completion = Assert.Single(operation.Transport.Completions);
        Assert.Equal((caller, OperationId), (completion.Target, completion.SubscriberId));
        Assert.Null(completion.Materialize(operation.Runtime).SubscriberId);
    });

    private static async DurableTask<string> WaitForRelease() => await Release.WaitAsync();

    private static async DurableTask<string> ObserveTaskThroughItsHandle(IAddressable target, string rootId)
    {
        var handle = await target.GetDurableTask<string>(rootId).ScheduleAsync();
        return $"{handle.Id} {handle.TargetTaskId} {await handle}";
    }

    private static async DurableTask<string> AwaitTask(IAddressable target, string rootId) =>
        await target.GetDurableTask<string>(rootId);

    private static async DurableTask<string> RunStep() => await DurableTask.Run(static _ => "done").WithId("step");

    private static async DurableTask<string> AwaitTwiceThenContinue(IAddressable target, TaskId stepId)
    {
        var first = await target.GetDurableTask<string>("op");
        string second;
        try
        {
            second = await target.GetDurableTask<string>(stepId);
        }
        catch (DurableTaskNotFoundException exception)
        {
            second = $"not found: {exception.TaskId}";
        }

        await Continue.WaitAsync();
        return $"{first}|{second}";
    }

    private static async DurableTask<string> AwaitWithDeadline(IAddressable target)
    {
        try
        {
            return await target.GetDurableTask<string>("op").WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException)
        {
            return "timed out";
        }
    }

    private static async DurableTask<string> RaceWithDelay(IAddressable target)
    {
        var (index, result) = await DurableTask.Race([target.GetDurableTask<string>("op"), DelayThenReturn()]);
        return $"{index}:{result}";

        static async DurableTask<string> DelayThenReturn()
        {
            await DurableTask.Delay(TimeSpan.FromHours(1));
            return "timeout";
        }
    }

    /// <summary>A grain identity which references can address.</summary>
    private sealed class GrainAddress(GrainId grainId) : IGrainBase, IAddressable
    {
        public IGrainContext GrainContext { get; } = new TestGrainContext(grainId);
    }

    /// <summary>Hosts one grain's journaled task state and runtime, recovering each activation from the committed journal.</summary>
    private sealed class SubscriptionHost(GrainId grainId, TimeProvider? timeProvider = null) : ISubscriptionWorkflows, IAsyncDisposable
    {
        private readonly DurableTaskJournalHarness _journal = new(DurableTaskJournalHarness.OrleansBinary, timeProvider);
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        public GrainId GrainId => grainId;

        public IAddressable Address { get; } = new GrainAddress(grainId);

        public Dictionary<string, Func<DurableTask>> Workflows { get; } = [];

        public async Task<SubscriptionActivation> ActivateAsync(CancellationToken cancellationToken)
        {
            var scope = _journal.CreateScope();
            try
            {
                var services = scope.ServiceProvider;
                var storage = services.GetRequiredService<DurableTaskGrainStorage>();
                var logicalTimes = services.GetRequiredKeyedService<IDurableDictionary<TaskId, DateTimeOffset>>("$task-logical-times");
                var invocations = services.GetRequiredKeyedService<IDurableDictionary<MessageId, PendingInvocation>>("$task-invocations");
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
                var runtime = new DurableTaskGrainRuntime(storage, logicalTimes, invocations, shared, [transport]);
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

    private sealed class SubscriptionActivation(
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

        /// <summary>Gets the subscribers of a task other than its invoker.</summary>
        public IReadOnlyList<DurableTaskSubscriber> Subscribers(TaskId taskId) =>
            storage.TryGetTask(taskId, out var state)
                ? [.. state.CompletionDestinations.Where(subscriber => subscriber.TaskId != taskId)]
                : [];

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
            await StateChanges.WaitUntilAsync(() => Contains(taskId), description: $"task '{taskId}' is stored");
            await WriteAsync(TestCancellation);
        }

        private SubscriptionWorkflowRequest CreateRequest(string workflow, GrainId caller) => new()
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

/// <summary>A serializable request which runs a named workflow of the subscription test host which it targets.</summary>
[GenerateSerializer]
[Alias("Microsoft.Orleans.DurableTasks.Tests.SubscriptionWorkflowRequest")]
internal sealed class SubscriptionWorkflowRequest : IDurableTaskRequest
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

    public string GetInterfaceName() => "Microsoft.Orleans.DurableTasks.Tests.ISubscriptionGrain";

    public string GetActivityName() => "Microsoft.Orleans.DurableTasks.Tests.ISubscriptionGrain/Run";

    public MethodInfo GetMethod() => typeof(SubscriptionWorkflowRequest).GetMethod(nameof(GetMethod))!;

    public Type GetInterfaceType() => typeof(SubscriptionWorkflowRequest);

    public void Dispose()
    {
    }

    public DurableTask CreateTask() =>
        (_host as ISubscriptionWorkflows ?? throw new InvalidOperationException("The request has no target.")).Workflows[Workflow]();
}

/// <summary>The named workflows of a subscription test host.</summary>
internal interface ISubscriptionWorkflows
{
    Dictionary<string, Func<DurableTask>> Workflows { get; }
}
