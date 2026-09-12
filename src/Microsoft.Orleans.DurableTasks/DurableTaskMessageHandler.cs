using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableMessaging;
using Orleans.DurableTasks.Protocol;

namespace Orleans.DurableTasks.Runtime;

internal sealed class DurableTaskMessageHandler(
    DurableTaskGrainRuntime runtime) : RoutePrefixHandler("durable-rpc/")
{
    protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        switch (context.Envelope.RouteKey)
        {
            case DurableTaskMessageTransport.InvocationRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskInvocationMessage>(out var invocation)
                    || invocation is null)
                {
                    throw new InvalidOperationException("The durable task invocation payload could not be deserialized.");
                }

                var request = invocation.Request
                    ?? throw new InvalidOperationException("The durable task invocation request is missing.");
                var requestContext = request.Context
                    ?? throw new InvalidOperationException("The durable task invocation request has no context.");
                if (requestContext.TargetId != context.GrainId)
                {
                    throw new InvalidOperationException(
                        $"The durable task invocation targets grain '{requestContext.TargetId}', not receiver '{context.GrainId}'.");
                }

                var sender = context.Envelope.SenderId;
                if (context.Envelope.ReplyTo is { } replyTo && replyTo != sender)
                {
                    throw new InvalidOperationException(
                        $"The durable task invocation reply address '{replyTo}' does not match sender '{sender}'.");
                }

                requestContext.CallerId = sender;
                requestContext.SupportsDurableCompletion = true;
                await runtime.ScheduleFromInboxAsync(
                    invocation.TaskId,
                    request,
                    cancellationToken);
                break;
            case DurableTaskMessageTransport.CompletionRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskCompletionMessage>(out var completion)
                    || completion is null)
                {
                    throw new InvalidOperationException("The durable task completion payload could not be deserialized.");
                }

                await runtime.AcceptResponseAsync(
                    completion.TaskId,
                    completion.Response,
                    context.Envelope.SenderId,
                    cancellationToken,
                    persist: false);
                break;
            case DurableTaskMessageTransport.CompletionAckRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskCompletionAckMessage>(out var acknowledgement)
                    || acknowledgement is null)
                {
                    throw new InvalidOperationException("The durable task completion acknowledgement payload could not be deserialized.");
                }

                await runtime.AcknowledgeCompletionAsync(
                    acknowledgement.TaskId,
                    context.Envelope.SenderId,
                    cancellationToken,
                    persist: false);
                break;
            case DurableTaskMessageTransport.CancellationRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskCancellationMessage>(out var cancellation)
                    || cancellation is null)
                {
                    throw new InvalidOperationException("The durable task cancellation payload could not be deserialized.");
                }

                await runtime.SignalCancellationFromInboxAsync(
                    cancellation.TaskId,
                    context.Envelope.SenderId,
                    cancellationToken);
                runtime.CompleteInboxCancellationHandling(cancellation.TaskId);
                break;
            default:
                throw new InvalidOperationException($"Unsupported durable task route '{context.Envelope.RouteKey}'.");
        }
    }
}
