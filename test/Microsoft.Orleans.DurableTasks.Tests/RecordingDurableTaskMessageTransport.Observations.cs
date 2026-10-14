#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Microsoft.Orleans.DurableTasks.Tests;

internal sealed partial class RecordingDurableTaskMessageTransport
{
    /// <summary>Gets every subscription and unsubscription which the runtime staged, in send order.</summary>
    public List<RecordedObservation> Observations { get; } = [];

    /// <summary>Gets every not-found completion which the runtime staged for an observation, in send order.</summary>
    public List<RecordedObservation> ObservationsNotFound { get; } = [];

    public MessageId GetObservationCompletionMessageId(GrainId sender, GrainId target, TaskId taskId, TaskId observerId)
    {
        var messageId = DurableTaskMessageTransport.CreateMessageId(
            sender,
            target,
            taskId,
            DurableTaskMessageTypes.CompletionSubject,
            observerId.ToString());
        _completionIds[messageId] = (sender, target, taskId);
        return messageId;
    }

    public void SendObservationNotFound(GrainId sender, GrainId target, TaskId taskId, TaskId observerId) =>
        ObservationsNotFound.Add(new(sender, target, taskId, observerId, Unsubscribe: false));

    public void SendObservation(GrainId sender, GrainId target, TaskId taskId, TaskId observerId, bool unsubscribe)
    {
        Observations.Add(new(sender, target, taskId, observerId, unsubscribe));
        foreach (var (count, waiter) in _observationWaiters)
        {
            if (Observations.Count >= count)
            {
                waiter.TrySetResult(Observations[count - 1]);
            }
        }
    }

    private readonly List<(int Count, TaskCompletionSource<RecordedObservation> Waiter)> _observationWaiters = [];

    /// <summary>Waits until the runtime has staged <paramref name="count"/> observation messages, and returns the last.</summary>
    public Task<RecordedObservation> WaitForObservationAsync(int count)
    {
        if (Observations.Count >= count)
        {
            return Task.FromResult(Observations[count - 1]);
        }

        var waiter = new TaskCompletionSource<RecordedObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        _observationWaiters.Add((count, waiter));
        return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>An observation message which the runtime staged.</summary>
internal sealed record RecordedObservation(GrainId Sender, GrainId Target, TaskId TaskId, TaskId ObserverId, bool Unsubscribe)
{
    /// <summary>Creates the inbox message which delivers this subscription or unsubscription to its target.</summary>
    public InboxMessage ToInboxMessage() =>
        MessagingTestHelpers.MessageTypes.Observation.Create(
            DurableTaskMessageTransport.CreateMessageId(
                Sender,
                Target,
                TaskId,
                DurableTaskMessageTypes.ObservationSubject,
                ObserverId.ToString(),
                Unsubscribe ? "unsubscribe" : null),
            Target,
            new DurableTaskObservationMessage { TaskId = TaskId, ObserverId = ObserverId, Sender = Sender, Unsubscribe = Unsubscribe })
            .ToInboxMessage();

    /// <summary>Creates the inbox message which delivers the not-found completion for this observation to the observer.</summary>
    public InboxMessage ToNotFoundInboxMessage() =>
        MessagingTestHelpers.MessageTypes.Completion.Create(
            DurableTaskMessageTransport.CreateMessageId(
                Sender,
                Target,
                TaskId,
                DurableTaskMessageTypes.CompletionSubject,
                ObserverId.ToString(),
                "not-found"),
            Target,
            new DurableTaskCompletionMessage
            {
                TaskId = TaskId,
                Response = DurableTaskResponse.FromException(new DurableTaskNotFoundException(TaskId)),
                Sender = Sender,
                ObserverId = ObserverId,
            })
            .ToInboxMessage();
}
