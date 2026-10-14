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
/// As an <see cref="IInboxMessageSink"/>, it accepts invocations, completions, cancellations, and signals directly into
/// task state, so the request, result, or signal payload is journaled once, in task state, and the inbox journals only
/// the message's processed record in the same write. Each message is decoded and validated before any change is
/// staged; a message which fails validation throws, and a rejected signal reports a dead-lettered result, so the inbox
/// dead-letters either one.
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
                var request = invocation.Request
                    ?? throw new InvalidOperationException("The durable task invocation request is missing.");
                var requestContext = request.Context
                    ?? throw new InvalidOperationException("The durable task invocation request has no context.");
                var sender = GetSender(invocation.Sender);

                // A detached task has no caller: like a client-scheduled root, it's retained for polling and its
                // result is sent to no grain.
                requestContext.CallerId = invocation.Detached ? default : sender;
                requestContext.SupportsDurableCompletion = !invocation.Detached;
                runtime.ScheduleFromInbox(invocation.TaskId, request);
                return true;
            case DurableTaskMessageTypes.CompletionSubject:
                var completion = messageTypes.Completion.Decode(message.Envelope);
                var response = completion.Response
                    ?? throw new InvalidOperationException("The durable task completion response is missing.");
                runtime.AcceptResponseFromInbox(completion.TaskId, response, GetSender(completion.Sender));
                return true;
            case DurableTaskMessageTypes.CancellationSubject:
                var cancellation = messageTypes.Cancellation.Decode(message.Envelope);
                if (cancellation.AbortReason is { } abortReason)
                {
                    runtime.AbortFromInbox(cancellation.TaskId, abortReason, GetSender(cancellation.Sender));
                }
                else
                {
                    runtime.SignalCancellationFromInbox(cancellation.TaskId, GetSender(cancellation.Sender), cancellation.CancellationReason);
                }

                return true;
            case DurableTaskMessageTypes.SignalSubject:
                var signal = messageTypes.Signal.Decode(message.Envelope);
                _ = GetSender(signal.Sender);

                // A rejected signal, such as one for an unknown or terminal task or with a conflicting value, stages
                // nothing and is dead-lettered with the rejection as its reason.
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
