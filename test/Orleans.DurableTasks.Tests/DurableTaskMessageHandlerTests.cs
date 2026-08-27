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
/// Unit tests for <see cref="DurableTaskMessageHandler"/>.
/// </summary>
/// <remarks>
/// <see cref="DurableTaskMessageHandler"/> depends on a real (non-mockable) <see cref="Orleans.Runtime.DurableTasks.DurableTaskGrainRuntime"/>,
/// which is a concrete sealed class. Rather than mocking it, these tests construct a real runtime backed by
/// <see cref="RpcTestDurableTaskGrainStorage"/> and drive it end-to-end via <see cref="RpcTestDurableTaskRequest"/>,
/// whose <see cref="RpcTestDurableTaskRequest.CreateTask"/> resolves synchronously (via <see cref="DurableTask.FromResult{TResult}"/>),
/// enabling deterministic assertions on both the "still pending" and "already completed" code paths.
/// </remarks>
public class DurableTaskMessageHandlerTests
{
    private static SerializerSessionPool CreateSessionPool() =>
        new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<SerializerSessionPool>();

    private static (DurableTaskMessageHandler Handler, RpcTestDurableTaskGrainStorage Storage, RpcTestMessageTransport HandlerTransport, RpcTestMessageTransport RuntimeTransport, GrainId GrainId, SerializerSessionPool SessionPool) CreateHandler()
    {
        var sessionPool = CreateSessionPool();
        var grainId = GrainId.Create("rpc-handler-grain", Guid.NewGuid().ToString("N"));
        var grainContext = new TestGrainContext(grainId);
        var storage = new RpcTestDurableTaskGrainStorage();

        // The runtime needs its own message transport so that completion-destination fan-out (which is triggered
        // whenever a completion destination has been registered, as happens for every invocation) does not throw.
        var runtimeTransport = new RpcTestMessageTransport();
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext, runtimeTransport);

        // The handler's own transport, used to assert its explicit SendCompletion call.
        var handlerTransport = new RpcTestMessageTransport();
        var handler = new DurableTaskMessageHandler(runtime, handlerTransport);
        return (handler, storage, handlerTransport, runtimeTransport, grainId, sessionPool);
    }

    private static IInboxHandlerContext CreateContext<T>(
        SerializerSessionPool sessionPool,
        GrainId sender,
        GrainId receiver,
        string route,
        T body,
        GrainId? replyTo = null)
    {
        var builder = new DurableEnvelopeBuilder(sessionPool, sender).To(receiver, route).WithBody(body);
        if (replyTo is { } r)
        {
            builder.WithReplyTo(r);
        }

        return new RpcMockInboxHandlerContext(builder.Build(), receiver);
    }

    private static IInboxHandlerContext CreateUndeserializableContext(GrainId sender, GrainId receiver, string route)
    {
        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = sender,
            ReceiverId = receiver,
            RouteKey = route,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new DurableEnvelopeData(null!),
        };
        return new RpcMockInboxHandlerContext(envelope, receiver);
    }

    [Fact]
    public async Task InvocationRoute_FirstCall_SchedulesTask_DoesNotSendCompletionViaHandlerTransport()
    {
        var (handler, storage, handlerTransport, runtimeTransport, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-1");
        var taskId = TaskId.CreateRandom();
        var request = new RpcTestDurableTaskRequest { ResultValue = 99, Context = new DurableTaskRequestContext { TargetId = grainId } };
        var context = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request });

        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        // The handler's own transport must NOT be used on the first call: ScheduleAsync unconditionally returns
        // a non-completed (Subscribed) response on first schedule, regardless of internal synchronous completion.
        Assert.Empty(handlerTransport.Completions);

        // The task actually completes synchronously (RpcTestDurableTaskRequest.CreateTask() resolves immediately),
        // so the *runtime's own* internal transport is used to notify the registered completion destination (the caller).
        var completion = Assert.Single(runtimeTransport.Completions);
        Assert.Equal(sender, completion.Target);
        Assert.Equal(taskId, completion.TaskId);
        Assert.True(completion.Response.IsCompleted);
        Assert.Equal(99, completion.Response.GetResult<int>());

        Assert.True(storage.TryGetTask(taskId, out var state));
        Assert.NotNull(state!.Result);
        Assert.True(state.Result!.IsCompleted);
        var storedRequest = Assert.IsType<RpcTestDurableTaskRequest>(state.Request);
        Assert.Equal(1, storedRequest.CreateTaskCallCount);
    }

    [Fact]
    public async Task InvocationRoute_SecondCallForSameTask_SendsCompletionViaHandlerTransport()
    {
        var (handler, storage, handlerTransport, _, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-2");
        var taskId = TaskId.CreateRandom();

        // First call: schedules and (synchronously) completes the task, but does not yet report completion via
        // the handler's own transport (see the test above).
        var firstRequest = new RpcTestDurableTaskRequest { ResultValue = 7, Context = new DurableTaskRequestContext { TargetId = grainId } };
        var firstContext = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = firstRequest });
        await ((IInboxHandler)handler).HandleAsync(firstContext, CancellationToken.None);
        Assert.Empty(handlerTransport.Completions);

        // Second call for the same task id: the runtime finds the already-completed local task handle and returns
        // the completed response directly, which the handler must forward via its own transport.
        var secondRequest = new RpcTestDurableTaskRequest { ResultValue = 7, Context = new DurableTaskRequestContext { TargetId = grainId } };
        var secondContext = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = secondRequest });
        await ((IInboxHandler)handler).HandleAsync(secondContext, CancellationToken.None);

        var completion = Assert.Single(handlerTransport.Completions);
        Assert.Equal(grainId, completion.Sender);
        Assert.Equal(sender, completion.Target);
        Assert.Equal(taskId, completion.TaskId);
        Assert.True(completion.Response.IsCompleted);
        Assert.Equal(7, completion.Response.GetResult<int>());

        Assert.True(storage.TryGetTask(taskId, out var state));
        Assert.True(state!.Result!.IsCompleted);
    }

    [Fact]
    public async Task InvocationRoute_CallerId_UsesReplyToWhenPresent()
    {
        var (handler, _, _, runtimeTransport, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-3");
        var replyTo = GrainId.Create("rpc-reply-target", "reply-3");
        var taskId = TaskId.CreateRandom();
        var request = new RpcTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = grainId } };
        var context = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
            replyTo: replyTo);

        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        var completion = Assert.Single(runtimeTransport.Completions);
        Assert.Equal(replyTo, completion.Target);
        Assert.NotEqual(sender, completion.Target);
    }

    [Fact]
    public async Task InvocationRoute_CallerId_FallsBackToSenderIdWhenReplyToAbsent()
    {
        var (handler, _, _, runtimeTransport, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-4");
        var taskId = TaskId.CreateRandom();
        var request = new RpcTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = grainId } };
        var context = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
            replyTo: null);

        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        var completion = Assert.Single(runtimeTransport.Completions);
        Assert.Equal(sender, completion.Target);
    }

    [Fact]
    public async Task InvocationRoute_DeserializationFailure_ThrowsInvalidOperationException()
    {
        var (handler, _, _, _, grainId, _) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-5");
        var context = CreateUndeserializableContext(sender, grainId, DurableTaskMessageTransport.InvocationRoute);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Equal("The durable task invocation payload could not be deserialized.", exception.Message);
    }

    [Fact]
    public async Task InvocationRoute_MissingRequestContext_ThrowsInvalidOperationException()
    {
        var (handler, _, _, _, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "missing-context");
        var request = new RpcTestDurableTaskRequest();
        var context = CreateContext(
            sessionPool,
            sender,
            grainId,
            DurableTaskMessageTransport.InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = TaskId.CreateRandom(), Request = request });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Equal("The durable task invocation request did not include a request context.", exception.Message);
    }

    [Fact]
    public async Task CompletionRoute_DelegatesToRuntimeAcceptResponse()
    {
        var (handler, storage, _, _, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-6");
        var taskId = TaskId.CreateRandom();
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetRemoteTarget(taskId, state, sender);
        var context = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.CompletionRoute,
            new DurableTaskCompletionMessage { TaskId = taskId, Response = DurableTaskResponse.FromResult(5) });

        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.True(storage.TryGetTask(taskId, out state));
        Assert.NotNull(state.Result);
        Assert.True(state.Result!.IsCompleted);
        Assert.Equal(5, state.Result.GetResult<int>());
    }

    [Fact]
    public async Task CompletionRoute_DeserializationFailure_ThrowsInvalidOperationException()
    {
        var (handler, _, _, _, grainId, _) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-7");
        var context = CreateUndeserializableContext(sender, grainId, DurableTaskMessageTransport.CompletionRoute);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Equal("The durable task completion payload could not be deserialized.", exception.Message);
    }

    [Fact]
    public async Task CancellationRoute_DelegatesToRuntimeSignalCancellationAsync()
    {
        var (handler, storage, handlerTransport, _, grainId, sessionPool) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-8");
        var taskId = TaskId.CreateRandom();
        var context = CreateContext(sessionPool, sender, grainId, DurableTaskMessageTransport.CancellationRoute,
            new DurableTaskCancellationMessage { TaskId = taskId });

        Assert.False(storage.TryGetTask(taskId, out _));

        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.True(storage.TryGetTask(taskId, out var state));
        Assert.True(state!.CancellationRequestedAt.HasValue);
        var acknowledgement = Assert.Single(handlerTransport.CancellationAcknowledgements);
        Assert.Equal((grainId, sender, taskId), (acknowledgement.Sender, acknowledgement.Target, acknowledgement.TaskId));
        Assert.Equal(DurableTaskStatus.Canceled, acknowledgement.Response.Status);
        Assert.Equal(1, handlerTransport.CommitAsyncCallCount);
    }

    [Fact]
    public async Task CancellationRoute_DeserializationFailure_ThrowsInvalidOperationException()
    {
        var (handler, _, _, _, grainId, _) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-9");
        var context = CreateUndeserializableContext(sender, grainId, DurableTaskMessageTransport.CancellationRoute);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Equal("The durable task cancellation payload could not be deserialized.", exception.Message);
    }

    [Fact]
    public async Task ResumeRoute_DelegatesToRuntimeAcceptResponseWithCompleted()
    {
        var (handler, storage, _, _, grainId, sessionPool) = CreateHandler();
        var taskId = TaskId.CreateRandom();
        storage.GetOrCreateTask(taskId, request: null);
        var context = CreateContext(sessionPool, grainId, grainId, DurableTaskMessageTransport.ResumeRoute,
            new DurableTaskResumeMessage { TaskId = taskId });

        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.True(storage.TryGetTask(taskId, out var state));
        Assert.NotNull(state!.Result);
        Assert.True(state.Result!.IsCompleted);
        Assert.Equal(DurableTaskResponseKind.CompletedSuccessfully, state.Result.ResponseKind);
    }

    [Fact]
    public async Task ResumeRoute_DeserializationFailure_ThrowsInvalidOperationException()
    {
        var (handler, _, _, _, grainId, _) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-11");
        var context = CreateUndeserializableContext(sender, grainId, DurableTaskMessageTransport.ResumeRoute);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Equal("The durable task resume payload could not be deserialized.", exception.Message);
    }

    [Fact]
    public async Task UnsupportedRoute_ThrowsInvalidOperationExceptionWithRouteInMessage()
    {
        var (handler, _, _, _, grainId, _) = CreateHandler();
        var sender = GrainId.Create("rpc-caller", "caller-12");
        const string unsupportedRoute = "durable-rpc/unsupported-operation";
        var context = CreateContext(CreateSessionPool(), sender, grainId, unsupportedRoute, new DurableTaskCancellationMessage { TaskId = TaskId.CreateRandom() });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Equal($"Unsupported durable task route '{unsupportedRoute}'.", exception.Message);
    }
}
