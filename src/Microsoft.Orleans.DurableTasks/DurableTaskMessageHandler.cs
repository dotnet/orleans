using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Applies durable grain RPC messages delivered to the grain's inbox.
/// </summary>
/// <remarks>
/// <para>
/// As an <see cref="IInboxMessageSink"/>, it accepts invocations, subscriptions, completions, cancellations,
/// unsubscriptions, and signals directly into task state, so the request, result, or signal payload is journaled once, in
/// task state, and the inbox journals only the message's processed record in the same write. Each message is decoded and
/// validated before any change is staged. An invocation whose identifier already identifies another request or caller is
/// final, so it is dead-lettered with the <see cref="DurableTaskRejectionCodes.Conflict"/> code and the conflict's
/// message, which the sender's outbox reports when it retires the invocation as rejected. A rejected signal is
/// dead-lettered with its rejection as the reason. Another failure, such as a message which doesn't decode, throws, so
/// the sender's outbox retries the delivery until Messaging's <c>OutboxRetryTimeout</c>: during a rolling deployment, a
/// retry can reach an activation which decodes it.
/// </para>
/// <para>
/// As an <see cref="IInboxHandler"/>, it handles the delay resumes the grain schedules for itself, which the inbox holds
/// until they are due. The handler stages the resume's changes and completes the message in one synchronous block.
/// </para>
/// <para>
/// The runtime runs acknowledgement-dependent effects, such as starting requests, completing handles, and propagating
/// cancellation, after the write which commits the staged changes. Neither path waits for a journal write.
/// </para>
/// </remarks>
internal sealed class DurableTaskMessageHandler(
    DurableTaskGrainRuntime runtime,
    DurableTaskMessageTypes messageTypes) : IInboxMessageSink, IInboxHandler
{
    public bool TryAccept(InboxMessage message, out DeliveryResult result)
    {
        result = DeliveryResult.Accepted();
        switch (message.Envelope.Subject)
        {
            case DurableTaskMessageTypes.InvocationSubject:
                var invocation = messageTypes.Invocation.Decode(message.Envelope);
                var sender = GetSender(invocation.Sender);
                if (invocation.Request is not { } request)
                {
                    // A subscription carries no request and claims no caller: it adds the sender's task to the existing
                    // task's completion destinations.
                    if (invocation.SubscriberId is not { } subscriberId || invocation.Detached)
                    {
                        throw new InvalidOperationException("A durable task subscription carries a subscriber and nothing else.");
                    }

                    runtime.SubscribeFromInbox(invocation.TaskId, new(sender, subscriberId));
                    return true;
                }

                try
                {
                    // An attached invocation addresses the sender's child, which this grain stores under the sender's
                    // scope; a detached invocation addresses a root task.
                    runtime.InvokeFromInbox(
                        CallerScope.Resolve(sender, invocation.TaskId),
                        request,
                        sender,
                        invocation.Detached,
                        invocation.SubscriberId);
                }
                catch (DurableTaskConflictException conflict)
                {
                    // A conflicting invocation stages nothing.
                    result = DeliveryResult.DeadLettered(conflict.Message, DurableTaskRejectionCodes.Conflict);
                }

                return true;
            case DurableTaskMessageTypes.CompletionSubject:
                var completion = messageTypes.Completion.Decode(message.Envelope);
                var response = completion.Response
                    ?? throw new InvalidOperationException("The durable task completion response is missing.");
                runtime.AcceptResponseFromInbox(
                    completion.SubscriberId ?? completion.TaskId,
                    completion.TaskId,
                    response,
                    GetSender(completion.Sender));
                return true;
            case DurableTaskMessageTypes.CancellationSubject:
                var cancellation = messageTypes.Cancellation.Decode(message.Envelope);
                if (cancellation.SubscriberId is { } unsubscribedId)
                {
                    runtime.UnsubscribeFromInbox(cancellation.TaskId, new(GetSender(cancellation.Sender), unsubscribedId));
                }
                else
                {
                    // A cancellation or an abort addresses the sender's remote child, which this grain stores under the
                    // sender's scope.
                    var caller = GetSender(cancellation.Sender);
                    var taskId = CallerScope.Resolve(caller, cancellation.TaskId);
                    if (cancellation.AbortReason is { } abortReason)
                    {
                        runtime.AbortFromInbox(taskId, abortReason, caller);
                    }
                    else
                    {
                        runtime.SignalCancellationFromInbox(taskId, caller, cancellation.CancellationReason);
                    }
                }

                return true;
            case DurableTaskMessageTypes.SignalSubject:
                var signal = messageTypes.Signal.Decode(message.Envelope);
                _ = GetSender(signal.Sender);

                // A rejected signal, such as one for a terminal task or with a conflicting value, stages nothing and is
                // dead-lettered with the rejection as its reason. A signal which precedes its invocation is recorded.
                if (runtime.StageSignalFromInbox(signal.TaskId, signal.Name, signal.Value) is { } rejection)
                {
                    result = DeliveryResult.DeadLettered(rejection.Message);
                }

                return true;
            default:
                // Delay resumes are declined, so the inbox holds each one until its due time and then dispatches it to
                // the handler.
                result = default;
                return false;
        }
    }

    public bool CanHandle(IInboxHandlerContext context) =>
        string.Equals(context.Message.Envelope.Subject, DurableTaskMessageTypes.ResumeSubject, StringComparison.Ordinal);

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resume = messageTypes.Resume.Decode(context.Message.Envelope);
        runtime.ResumeDelayFromInbox(resume.TaskId, resume.Generation, GetSender(resume.Sender));
        context.Complete();
        return ValueTask.CompletedTask;
    }

    private static Orleans.Runtime.GrainId GetSender(Orleans.Runtime.GrainId sender) =>
        sender.IsDefault
            ? throw new InvalidOperationException("The durable task message has no sender.")
            : sender;
}
