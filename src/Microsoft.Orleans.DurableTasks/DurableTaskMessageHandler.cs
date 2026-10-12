using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Applies durable grain RPC messages delivered to the grain's inbox.
/// </summary>
/// <remarks>
/// Each message is decoded and validated, then its runtime changes are staged and the message is completed in one
/// synchronous block. The inbox persists the staged changes with the message outcome in its batch write, and the
/// runtime runs acknowledgement-dependent effects after that write completes. Handling never waits for a journal write,
/// so later messages in the same batch cannot wait on the batch's own write.
/// </remarks>
internal sealed class DurableTaskMessageHandler(
    DurableTaskGrainRuntime runtime,
    DurableTaskMessageTypes messageTypes) : IInboxHandler
{
    public bool CanHandle(IInboxHandlerContext context) =>
        context.Message.Envelope.Subject is { } subject
        && subject.StartsWith(DurableTaskMessageTypes.SubjectPrefix, StringComparison.Ordinal);

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = context.Message;
        switch (message.Envelope.Subject)
        {
            case DurableTaskMessageTypes.InvocationSubject:
                var invocation = messageTypes.Invocation.Decode(message);
                var request = invocation.Request
                    ?? throw new InvalidOperationException("The durable task invocation request is missing.");
                var requestContext = request.Context
                    ?? throw new InvalidOperationException("The durable task invocation request has no context.");
                var sender = GetSender(invocation.Sender);
                requestContext.CallerId = sender;
                requestContext.SupportsDurableCompletion = true;
                runtime.ScheduleFromInbox(invocation.TaskId, request);
                break;
            case DurableTaskMessageTypes.CompletionSubject:
                var completion = messageTypes.Completion.Decode(message);
                var response = completion.Response
                    ?? throw new InvalidOperationException("The durable task completion response is missing.");
                runtime.AcceptResponseFromInbox(completion.TaskId, response, GetSender(completion.Sender));
                break;
            case DurableTaskMessageTypes.CompletionAckSubject:
                var acknowledgement = messageTypes.CompletionAck.Decode(message);
                runtime.AcknowledgeCompletionFromInbox(acknowledgement.TaskId, GetSender(acknowledgement.Sender));
                break;
            case DurableTaskMessageTypes.CancellationSubject:
                var cancellation = messageTypes.Cancellation.Decode(message);
                runtime.SignalCancellationFromInbox(cancellation.TaskId, GetSender(cancellation.Sender));
                break;
            default:
                throw new InvalidOperationException($"Unsupported durable task message subject '{message.Envelope.Subject}'.");
        }

        context.Complete();
        return ValueTask.CompletedTask;
    }

    private static Orleans.Runtime.GrainId GetSender(Orleans.Runtime.GrainId sender) =>
        sender.IsDefault
            ? throw new InvalidOperationException("The durable task message has no sender.")
            : sender;
}
