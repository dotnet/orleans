#nullable enable
using System;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Unit tests for <see cref="DurableTaskMessageTransport"/>.
/// </summary>
public class DurableTaskMessageTransportTests
{
    private static (DurableTaskMessageTransport Transport, RpcTestDurableOutbox Outbox, RpcTestDurableMessageScheduler Scheduler, RpcTestJournaledStateManager StateManager) CreateTransport()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var sessionPool = services.GetRequiredService<SerializerSessionPool>();
        var outbox = new RpcTestDurableOutbox();
        var scheduler = new RpcTestDurableMessageScheduler();
        var stateManager = new RpcTestJournaledStateManager();
        var transport = new DurableTaskMessageTransport(
            outbox,
            scheduler,
            stateManager,
            sessionPool,
            new DurableMessagingCommitCoordinator());
        return (transport, outbox, scheduler, stateManager);
    }

    [Fact]
    public void SendInvocation_SendsEnvelopeWithInvocationRouteAndReplyToSetToSender()
    {
        var (transport, outbox, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "sender-1");
        var target = GrainId.Create("rpc-target", "target-1");
        var taskId = TaskId.Create("workflow-1/step-1");
        var request = new RpcTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = target } };

        transport.SendInvocation(sender, target, taskId, request);

        var envelope = Assert.Single(outbox.SentEnvelopes);
        Assert.Equal(DurableTaskMessageTransport.InvocationRoute, envelope.RouteKey);
        Assert.Equal(sender, envelope.SenderId);
        Assert.Equal(target, envelope.ReceiverId);
        Assert.Equal(taskId.ToHierarchicalKey(), envelope.CorrelationKey);
        Assert.Equal(sender, envelope.ReplyTo);

        Assert.True(envelope.Data.TryGetBody<DurableTaskInvocationMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
        Assert.IsType<RpcTestDurableTaskRequest>(body.Request);
        Assert.Equal(request.ResultValue, ((RpcTestDurableTaskRequest)body.Request).ResultValue);
    }

    [Fact]
    public void SendInvocation_CapturesCurrentRequestContext()
    {
        var (transport, outbox, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "context-sender");
        var target = GrainId.Create("rpc-target", "context-target");
        var taskId = TaskId.Create("workflow/context");
        var request = new RpcTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = target } };

        RequestContext.Set("tenant", "contoso");
        try
        {
            transport.SendInvocation(sender, target, taskId, request);
        }
        finally
        {
            RequestContext.Clear();
        }

        var envelope = Assert.Single(outbox.SentEnvelopes);
        var contextKey = Assert.Single(envelope.Data.ContextKeys);
        Assert.True(envelope.Data.TryGetContextValue<Dictionary<string, object>>(contextKey, out var requestContext));
        Assert.NotNull(requestContext);
        Assert.Equal("contoso", requestContext["tenant"]);
    }

    [Fact]
    public void SendCompletion_SendsEnvelopeWithCompletionRouteAndNoReplyTo()
    {
        var (transport, outbox, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "sender-2");
        var target = GrainId.Create("rpc-target", "target-2");
        var taskId = TaskId.Create("workflow-2/step-1");
        var response = DurableTaskResponse.FromResult(7);

        transport.SendCompletion(sender, target, taskId, response);

        var envelope = Assert.Single(outbox.SentEnvelopes);
        Assert.Equal(DurableTaskMessageTransport.CompletionRoute, envelope.RouteKey);
        Assert.Equal(sender, envelope.SenderId);
        Assert.Equal(target, envelope.ReceiverId);
        Assert.Equal(taskId.ToHierarchicalKey(), envelope.CorrelationKey);
        Assert.Null(envelope.ReplyTo);

        Assert.True(envelope.Data.TryGetBody<DurableTaskCompletionMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
        Assert.True(body.Response.IsCompleted);
    }

    [Fact]
    public void SendCancellation_SendsEnvelopeWithCancellationRouteAndNoReplyTo()
    {
        var (transport, outbox, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "sender-3");
        var target = GrainId.Create("rpc-target", "target-3");
        var taskId = TaskId.Create("workflow-3/step-1");

        transport.SendCancellation(sender, target, taskId);

        var envelope = Assert.Single(outbox.SentEnvelopes);
        Assert.Equal(DurableTaskMessageTransport.CancellationRoute, envelope.RouteKey);
        Assert.Equal(sender, envelope.SenderId);
        Assert.Equal(target, envelope.ReceiverId);
        Assert.Equal(taskId.ToHierarchicalKey(), envelope.CorrelationKey);
        Assert.Null(envelope.ReplyTo);

        Assert.True(envelope.Data.TryGetBody<DurableTaskCancellationMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
    }

    [Fact]
    public void SendCancellationAcknowledgement_SendsCorrelatedAcknowledgementEnvelope()
    {
        var (transport, outbox, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-target", "ack-sender");
        var target = GrainId.Create("rpc-sender", "ack-target");
        var taskId = TaskId.Create("workflow-ack");
        var response = DurableTaskResponse.FromResult(42);

        transport.SendCancellationAcknowledgement(sender, target, taskId, response);

        var envelope = Assert.Single(outbox.SentEnvelopes);
        Assert.Equal(DurableTaskMessageTransport.CancellationAcknowledgementRoute, envelope.RouteKey);
        Assert.Equal(sender, envelope.SenderId);
        Assert.Equal(target, envelope.ReceiverId);
        Assert.Equal(taskId.ToHierarchicalKey(), envelope.CorrelationKey);
        Assert.True(envelope.Data.TryGetBody<DurableTaskCancellationAcknowledgementMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
        Assert.Equal(42, body.Response.GetResult<int>());
    }

    [Fact]
    public async Task ScheduleResumeAsync_CallsSchedulerWithResumeRouteAndDueTime()
    {
        var (transport, _, scheduler, _) = CreateTransport();
        var target = GrainId.Create("rpc-target", "target-4");
        var taskId = TaskId.Create("workflow-4/step-1");
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(5);

        await transport.ScheduleResumeAsync(target, taskId, dueTime, CancellationToken.None);

        var scheduled = Assert.Single(scheduler.Scheduled);
        Assert.Equal(dueTime, scheduled.DueTime);
        Assert.Equal(DurableTaskMessageTransport.ResumeRoute, scheduled.Message.RouteKey);
        Assert.Equal(target, scheduled.Message.SenderId);
        Assert.Equal(target, scheduled.Message.ReceiverId);
        Assert.Equal(taskId.ToHierarchicalKey(), scheduled.Message.CorrelationKey);
        Assert.Null(scheduled.Message.ReplyTo);

        Assert.True(scheduled.Message.Data.TryGetBody<DurableTaskResumeMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
    }

    [Fact]
    public async Task CommitAsync_DelegatesToStateManagerWriteStateAsync()
    {
        var (transport, _, _, stateManager) = CreateTransport();
        Assert.Equal(0, stateManager.WriteStateAsyncCallCount);

        await transport.CommitAsync(CancellationToken.None);

        Assert.Equal(1, stateManager.WriteStateAsyncCallCount);

        await transport.CommitAsync(CancellationToken.None);
        Assert.Equal(2, stateManager.WriteStateAsyncCallCount);
    }

    [Fact]
    public void SendInvocation_WithDefaultTaskId_ThrowsArgumentException()
    {
        var (transport, _, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "sender-5");
        var target = GrainId.Create("rpc-target", "target-5");
        var request = new RpcTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = target } };

        var exception = Assert.Throws<ArgumentException>(() =>
            transport.SendInvocation(sender, target, TaskId.None, request));

        Assert.Equal("taskId", exception.ParamName);
    }

    [Fact]
    public void SendCompletion_WithDefaultTaskId_ThrowsArgumentException()
    {
        var (transport, _, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "sender-6");
        var target = GrainId.Create("rpc-target", "target-6");

        var exception = Assert.Throws<ArgumentException>(() =>
            transport.SendCompletion(sender, target, TaskId.None, DurableTaskResponse.Completed));

        Assert.Equal("taskId", exception.ParamName);
    }

    [Fact]
    public void SendCancellation_WithDefaultTaskId_ThrowsArgumentException()
    {
        var (transport, _, _, _) = CreateTransport();
        var sender = GrainId.Create("rpc-sender", "sender-7");
        var target = GrainId.Create("rpc-target", "target-7");

        var exception = Assert.Throws<ArgumentException>(() =>
            transport.SendCancellation(sender, target, TaskId.None));

        Assert.Equal("taskId", exception.ParamName);
    }

    [Fact]
    public async Task ScheduleResumeAsync_WithDefaultTaskId_ThrowsArgumentException()
    {
        var (transport, _, _, _) = CreateTransport();
        var target = GrainId.Create("rpc-target", "target-8");

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await transport.ScheduleResumeAsync(target, TaskId.None, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal("taskId", exception.ParamName);
    }
}
