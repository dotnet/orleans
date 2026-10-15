#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Microsoft.Orleans.DurableTasks.Tests;

internal sealed partial class RecordingDurableTaskMessageTransport
{
    private readonly List<(int Count, TaskCompletionSource<RecordedSubscription> Waiter)> _subscriptionWaiters = [];

    /// <summary>Gets every subscription and unsubscription which the runtime staged, in send order.</summary>
    public List<RecordedSubscription> Subscriptions { get; } = [];

    /// <summary>Gets every completion which the runtime staged with its response in the body, in send order.</summary>
    public List<(RecordedSubscription Subscriber, DurableTaskResponse Response)> Responses { get; } = [];

    /// <summary>Gets the subscribers which the runtime completed as not found, in send order.</summary>
    public List<RecordedSubscription> NotFound =>
        [.. Responses.Where(static entry => entry.Response.Exception is DurableTaskNotFoundException).Select(static entry => entry.Subscriber)];

    public void SendResponse(GrainId sender, DurableTaskSubscriber destination, TaskId taskId, DurableTaskResponse response)
    {
        Responses.Add((new(sender, destination.GrainId, taskId, destination.TaskId, Unsubscribe: false), response));
        StateChanges.Notify();
    }

    public void SendSubscription(GrainId sender, GrainId target, TaskId taskId, TaskId subscriberId) =>
        AddSubscription(new(sender, target, taskId, subscriberId, Unsubscribe: false));

    public void SendUnsubscription(GrainId sender, GrainId target, TaskId taskId, TaskId subscriberId) =>
        AddSubscription(new(sender, target, taskId, subscriberId, Unsubscribe: true));

    private void AddSubscription(RecordedSubscription subscription)
    {
        Subscriptions.Add(subscription);
        foreach (var (count, waiter) in _subscriptionWaiters)
        {
            if (Subscriptions.Count >= count)
            {
                waiter.TrySetResult(Subscriptions[count - 1]);
            }
        }

        StateChanges.Notify();
    }

    /// <summary>Waits until the runtime has staged <paramref name="count"/> subscription messages, and returns the last.</summary>
    public Task<RecordedSubscription> WaitForSubscriptionAsync(int count)
    {
        if (Subscriptions.Count >= count)
        {
            return Task.FromResult(Subscriptions[count - 1]);
        }

        var waiter = new TaskCompletionSource<RecordedSubscription>(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscriptionWaiters.Add((count, waiter));
        return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>A subscription, an unsubscription, or a not-found completion which the runtime staged.</summary>
internal sealed record RecordedSubscription(GrainId Sender, GrainId Target, TaskId TaskId, TaskId SubscriberId, bool Unsubscribe)
{
    private static readonly RecordingOutbox Outbox = new();
    private static readonly DurableTaskMessageTransport Encoder = MessagingTestHelpers.CreateTransport(Outbox);

    /// <summary>Creates the inbox message which delivers this subscription or unsubscription to its target.</summary>
    public InboxMessage ToInboxMessage() => Encode(transport =>
    {
        if (Unsubscribe)
        {
            transport.SendUnsubscription(Sender, Target, TaskId, SubscriberId);
        }
        else
        {
            transport.SendSubscription(Sender, Target, TaskId, SubscriberId);
        }
    });

    /// <summary>Creates the inbox message which delivers the not-found completion which this record describes.</summary>
    public InboxMessage ToNotFoundInboxMessage() =>
        Encode(transport => transport.SendResponse(
            Sender,
            new(Target, SubscriberId),
            TaskId,
            DurableTaskResponse.FromException(new DurableTaskNotFoundException(TaskId))));

    private static InboxMessage Encode(Action<DurableTaskMessageTransport> send)
    {
        lock (Outbox)
        {
            var count = Outbox.Messages.Count;
            send(Encoder);
            return Outbox.Messages[count].ToInboxMessage();
        }
    }
}
