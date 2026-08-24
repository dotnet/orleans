using System;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableMessaging;
using Orleans.Runtime.DurableTasks;

namespace Orleans.DurableTasks;

internal sealed class DurableTaskMessageHandler(
    DurableTaskGrainRuntime runtime,
    IDurableTaskMessageTransport transport) : RoutePrefixHandler("durable-rpc/")
{
    protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        switch (context.Envelope.RouteKey)
        {
            case DurableTaskMessageTransport.InvocationRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskInvocationMessage>(out var invocation))
                {
                    throw new InvalidOperationException("The durable task invocation payload could not be deserialized.");
                }

                var requestContext = invocation.Request.Context
                    ?? throw new InvalidOperationException("The durable task invocation request did not include a request context.");
                requestContext.CallerId = context.Envelope.ReplyTo ?? context.Envelope.SenderId;
                var response = await ((IDurableTaskServer)runtime).ScheduleAsync(
                    invocation.TaskId,
                    invocation.Request,
                    cancellationToken);
                if (response.IsCompleted)
                {
                    transport.SendCompletion(
                        context.GrainId,
                        requestContext.CallerId,
                        invocation.TaskId,
                        response);
                }
                break;
            case DurableTaskMessageTransport.CompletionRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskCompletionMessage>(out var completion))
                {
                    throw new InvalidOperationException("The durable task completion payload could not be deserialized.");
                }

                runtime.AcceptResponse(completion.TaskId, completion.Response);
                break;
            case DurableTaskMessageTransport.CancellationRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskCancellationMessage>(out var cancellation))
                {
                    throw new InvalidOperationException("The durable task cancellation payload could not be deserialized.");
                }

                await runtime.SignalCancellationAsync(cancellation.TaskId, cancellationToken);
                transport.SendCancellationAcknowledgement(
                    context.GrainId,
                    context.Envelope.SenderId,
                    cancellation.TaskId,
                    runtime.GetCancellationAcknowledgementResponse(cancellation.TaskId));
                await transport.CommitAsync(cancellationToken);
                break;
            case DurableTaskMessageTransport.CancellationAcknowledgementRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskCancellationAcknowledgementMessage>(out var acknowledgement))
                {
                    throw new InvalidOperationException("The durable task cancellation acknowledgement payload could not be deserialized.");
                }

                await runtime.AcceptCancellationAcknowledgementAsync(
                    acknowledgement.TaskId,
                    context.Envelope.SenderId,
                    acknowledgement.Response,
                    cancellationToken);
                break;
            case DurableTaskMessageTransport.ResumeRoute:
                if (!context.Envelope.Data.TryGetBody<DurableTaskResumeMessage>(out var resume))
                {
                    throw new InvalidOperationException("The durable task resume payload could not be deserialized.");
                }

                runtime.AcceptResponse(resume.TaskId, DurableTaskResponse.Completed);
                break;
            default:
                throw new InvalidOperationException($"Unsupported durable task route '{context.Envelope.RouteKey}'.");
        }
    }
}
