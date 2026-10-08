using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class HandlerRoutingContractTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly SerializerSessionPool _sessions;

    public HandlerRoutingContractTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        _services = services.BuildServiceProvider();
        _sessions = _services.GetRequiredService<SerializerSessionPool>();
    }

    [Theory]
    [InlineData("orders/submit", true)]
    [InlineData("orders/Submit", false)]
    [InlineData("orders/submit/child", false)]
    [InlineData("orders", false)]
    public void RouteKeyHandler_MatchesOnlyExactOrdinalRoute(string route, bool expected)
    {
        var handler = new ExactHandler("orders/submit");
        using var context = CreateContext(route);

        Assert.Equal(expected, handler.CanHandle(context));
        Assert.Equal("orders/submit", handler.ExposedRoute);
    }

    [Fact]
    public void RouteKeyHandler_NullRoute_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new ExactHandler(null!));

        Assert.Equal("routeKey", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void RouteKeyHandler_EmptyOrWhitespaceRoute_ThrowsArgumentException(string route)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ExactHandler(route));

        Assert.IsNotType<ArgumentNullException>(exception);
        Assert.Equal("routeKey", exception.ParamName);
    }

    [Theory]
    [InlineData("orders/new", true, "new")]
    [InlineData("orders/new/priority", true, "new/priority")]
    [InlineData("orders", false, null)]
    [InlineData("orders-archive/new", false, null)]
    [InlineData("Orders/new", false, null)]
    public void RoutePrefixHandler_NormalizesBoundaryAndExtractsSuffix(string route, bool expected, string? suffix)
    {
        var handler = new PrefixHandler("orders");
        using var context = CreateContext(route);

        Assert.Equal(expected, handler.CanHandle(context));
        Assert.Equal("orders/", handler.ExposedPrefix);
        Assert.Equal(suffix, handler.Suffix(route));
    }

    [Fact]
    public void RoutePrefixHandler_NullPrefix_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new PrefixHandler(null!));

        Assert.Equal("prefix", exception.ParamName);
    }

    [Theory]
    [InlineData("workflow/order-42", true)]
    [InlineData("workflow/order-42/payment", true)]
    [InlineData("workflow/order-420", false)]
    [InlineData("workflow/other", false)]
    public void CorrelationHandler_MatchesOnlyConfiguredHierarchy(string correlation, bool expected)
    {
        var root = HierarchicalKey.Create("workflow/order-42");
        var handler = new HierarchyHandler(root);
        using var context = CreateContext("events", HierarchicalKey.Create(correlation));

        Assert.Equal(expected, handler.CanHandle(context));
        Assert.Equal(root, handler.ExposedCorrelation);
    }

    [Fact]
    public async Task TypedHandler_DeserializesExpectedTypeAndInvokesTypedMethod()
    {
        var handler = new TypedHandler();
        using var context = CreateContext("typed", body: new RoutedMessage(81, "typed-body"));
        using var cancellation = new CancellationTokenSource();

        Assert.True(((IInboxHandler)handler).CanHandle(context));
        await ((IInboxHandler)handler).HandleAsync(context, cancellation.Token);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(new RoutedMessage(81, "typed-body"), handler.Message);
        Assert.Same(context, handler.Context);
        Assert.Equal(cancellation.Token, handler.CancellationToken);
        Assert.Equal(1, context.CompleteCount);
    }

    [Fact]
    public async Task TypedHandler_WrongType_ThrowsBeforeInvokingTypedMethod()
    {
        var handler = new TypedHandler();
        using var context = CreateContext("typed", body: "not-a-routed-message");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));

        Assert.Contains(typeof(RoutedMessage).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("typed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
        Assert.Null(handler.Message);
        Assert.Equal(0, context.CompleteCount);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task HandleAsync_SynchronousHandling_PreservesArgumentsAndCompletes(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        using var context = CreatePreparedContext();
        var handlingCalls = 0;
        var mutations = new List<string>();
        var handler = CreateHandler(kind, (actualContext, token) =>
        {
            Assert.Same(context, actualContext);
            Assert.Equal(cancellation.Token, token);
            token.ThrowIfCancellationRequested();
            handlingCalls++;
            mutations.Add("business");
            actualContext.Complete();
            return default;
        });

        Assert.True(handler.CanHandle(context));
        var handling = handler.HandleAsync(context, cancellation.Token);

        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        Assert.Equal(1, handlingCalls);
        Assert.Equal(["business"], mutations);
        Assert.Equal(1, context.CompleteCount);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task HandleAsync_AsyncLocalWork_CompletesOnlyAfterRelease(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        using var context = CreatePreparedContext();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localWorkCalls = 0;
        var mutations = new List<string>();
        var handler = CreateHandler(kind, async (actualContext, token) =>
        {
            Assert.Same(context, actualContext);
            Assert.Equal(cancellation.Token, token);
            localWorkCalls++;
            await release.Task;
            token.ThrowIfCancellationRequested();
            mutations.Add("business");
            actualContext.Complete();
        });

        var handling = handler.HandleAsync(context, cancellation.Token);

        Assert.False(handling.IsCompleted);
        Assert.Equal(1, localWorkCalls);
        Assert.Empty(mutations);
        Assert.Equal(0, context.CompleteCount);
        release.SetResult();
        await handling;
        Assert.Equal(["business"], mutations);
        Assert.Equal(1, context.CompleteCount);
        Assert.Equal(1, localWorkCalls);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task HandleAsync_LocalFailure_PreservesException(string kind)
    {
        using var context = CreatePreparedContext();
        var failure = new InvalidOperationException("Local preparation failed.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateHandler(kind, (_, _) => new ValueTask(completion.Task));
        var handling = handler.HandleAsync(context, CancellationToken.None);
        Assert.False(handling.IsCompleted);

        completion.SetException(failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => await handling);
        Assert.Same(failure, actual);
        Assert.Equal(0, context.CompleteCount);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task HandleAsync_Cancellation_PreservesCancellationToken(string kind)
    {
        using var context = CreatePreparedContext();
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateHandler(kind, (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            return new ValueTask(completion.Task);
        });
        var handling = handler.HandleAsync(context, cancellation.Token);
        Assert.False(handling.IsCompleted);

        cancellation.Cancel();
        completion.SetCanceled(cancellation.Token);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handling);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, context.CompleteCount);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public void HandleAsync_SynchronousFailure_PropagatesDuringInvocation(string kind)
    {
        using var context = CreatePreparedContext();
        var failure = new InvalidOperationException("Handler failed.");
        var handlingCalls = 0;
        var handler = CreateHandler(kind, (_, _) =>
        {
            handlingCalls++;
            throw failure;
        });

        var actual = Assert.Throws<InvalidOperationException>(() => handler.HandleAsync(context, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(1, handlingCalls);
        Assert.Equal(0, context.CompleteCount);
    }

    [Fact]
    public async Task TypedHandler_NullBody_PreservesNullablePayloadAndCompletion()
    {
        var sender = GrainId.Create("sender", "null-body");
        var receiver = GrainId.Create("receiver", "null-body");
        var envelope = new DurableEnvelopeBuilder(_sessions, sender)
            .To(receiver, "typed/request")
            .WithBody<RoutedMessage?>(null)
            .Build();
        using var context = new TestContext(envelope, receiver);
        using var cancellation = new CancellationTokenSource();
        var handlingCalls = 0;
        IInboxHandler handler = new DelegateTypedHandler((message, actualContext, token) =>
        {
            Assert.Null(message);
            Assert.Same(context, actualContext);
            Assert.Equal(cancellation.Token, token);
            handlingCalls++;
            actualContext.Complete();
            return default;
        });

        await handler.HandleAsync(context, cancellation.Token);

        Assert.Equal(1, handlingCalls);
        Assert.Equal(1, context.CompleteCount);
    }

    [Fact]
    public void ExternalConsumerAssembly_HasNoFriendAccessToDurableMessaging()
    {
        var sourceAssembly = typeof(IDurableInbox).Assembly;
        var consumerName = typeof(HandlerRoutingContractTests).Assembly.GetName().Name;
        var friendDeclarations = sourceAssembly
            .GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(attribute => attribute.ConstructorArguments[0].Value?.ToString())
            .ToArray();

        Assert.DoesNotContain(friendDeclarations, declaration =>
            declaration?.StartsWith(consumerName!, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void PreparedOutboxBatch_ExposesOnlyDisposal()
    {
        var type = typeof(IPreparedOutboxBatch);

        Assert.Equal([typeof(IDisposable)], type.GetInterfaces());
        Assert.Empty(type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HandlerContext_Send_ForwardsBatchAndPreservesOwnership(int sends)
    {
        using var input = CreatePreparedContext();
        using var batch = new TestBatch();
        var outbox = new RecordingOutbox();
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() => throw new InvalidOperationException("Unexpected completion.")));

        for (var i = 0; i < sends; i++)
        {
            context.Send(batch);
        }

        Assert.Same(outbox, context.Outbox);
        Assert.Equal(sends, outbox.Sent.Count);
        Assert.All(outbox.Sent, actual => Assert.Same(batch, actual));
        Assert.Equal(0, batch.DisposeCount);
    }

    [Fact]
    public void HandlerContext_Send_PropagatesOutboxFailure()
    {
        using var input = CreatePreparedContext();
        using var batch = new TestBatch();
        var expected = new InvalidOperationException("Batch rejected.");
        var outbox = new RecordingOutbox { SendFailure = expected };
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() => throw new InvalidOperationException("Unexpected completion.")));

        var actual = Assert.Throws<InvalidOperationException>(() => context.Send(batch));

        Assert.Same(expected, actual);
        Assert.Empty(outbox.Sent);
        Assert.Equal(0, batch.DisposeCount);
    }

    [Fact]
    public void HandlerContext_SendEnvelope_ForwardsWithoutPreparingBatch()
    {
        using var input = CreatePreparedContext();
        var outbox = new RecordingOutbox();
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() => throw new InvalidOperationException("Unexpected completion.")));

        context.Send(input.Envelope);

        Assert.Equal(input.Envelope, Assert.Single(outbox.SentEnvelopes));
        Assert.Same(input.Envelope.Data, outbox.SentEnvelopes[0].Data);
        Assert.Null(outbox.PreparedMessages);
        Assert.Empty(outbox.Sent);
    }

    [Fact]
    public void HandlerContext_DefaultSendEnvelope_ForwardsToOutbox()
    {
        using var input = CreatePreparedContext();
        var outbox = new RecordingOutbox();
        IInboxHandlerContext context = new TestContext(input.Envelope, input.GrainId, outbox);

        context.Send(input.Envelope);

        Assert.Equal(input.Envelope, Assert.Single(outbox.SentEnvelopes));
        Assert.Null(outbox.PreparedMessages);
        Assert.Empty(outbox.Sent);
    }

    [Fact]
    public void HandlerContext_SendEnvelope_PropagatesOutboxFailure()
    {
        using var input = CreatePreparedContext();
        var expected = new InvalidOperationException("Envelope rejected.");
        var outbox = new RecordingOutbox { SendFailure = expected };
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() => throw new InvalidOperationException("Unexpected completion.")));

        var actual = Assert.Throws<InvalidOperationException>(() => context.Send(input.Envelope));

        Assert.Same(expected, actual);
        Assert.Empty(outbox.SentEnvelopes);
        Assert.Null(outbox.PreparedMessages);
    }

    [Fact]
    public void SelectionContext_SendEnvelope_RejectsBeforeOutboxAccess()
    {
        using var input = CreatePreparedContext();
        var context = CreateInternalContext("InboxHandlerSelectionContext", input.Envelope, input.GrainId);

        var exception = Assert.Throws<InvalidOperationException>(() => context.Send(input.Envelope));

        Assert.Contains("read-only", exception.Message, StringComparison.Ordinal);
        Assert.Equal(input.Envelope, context.Envelope);
        Assert.Equal(input.GrainId, context.GrainId);
    }

    [Fact]
    public void SelectionContext_Send_RejectsPreparedBatch()
    {
        using var input = CreatePreparedContext();
        using var batch = new TestBatch();
        var context = CreateInternalContext("InboxHandlerSelectionContext", input.Envelope, input.GrainId);

        var exception = Assert.Throws<InvalidOperationException>(() => context.Send(batch));

        Assert.Contains("read-only", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, batch.DisposeCount);
        Assert.Equal(input.Envelope, context.Envelope);
        Assert.Equal(input.GrainId, context.GrainId);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task HandleAsync_OptionalPreparedBatch_AwaitsBeforeSendAndCompletion(string kind)
    {
        using var input = CreatePreparedContext();
        using var batch = new TestBatch();
        using var cancellation = new CancellationTokenSource();
        var outbox = new RecordingOutbox();
        var events = new List<string>();
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() =>
            {
                Assert.Same(batch, Assert.Single(outbox.Sent));
                events.Add("complete");
            }));
        IReadOnlyList<DurableEnvelope> messages = [input.Envelope];
        var handler = CreateHandler(kind, async (actualContext, token) =>
        {
            var prepared = await actualContext.Outbox.PrepareSendAsync(messages, token);
            token.ThrowIfCancellationRequested();
            events.Add("business");
            actualContext.Send(prepared);
            actualContext.Complete();
        });

        var handling = handler.HandleAsync(context, cancellation.Token);

        Assert.False(handling.IsCompleted);
        Assert.Same(messages, outbox.PreparedMessages);
        Assert.Equal(cancellation.Token, outbox.PreparationToken);
        Assert.Empty(outbox.Sent);
        Assert.Empty(events);
        Assert.Equal(0, batch.DisposeCount);
        outbox.Preparation.SetResult(batch);
        await handling;
        Assert.Equal(["business", "complete"], events);
        Assert.Same(batch, Assert.Single(outbox.Sent));
        Assert.Equal(0, batch.DisposeCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HandlerContext_Complete_ForwardsEveryInvocationToAttemptOwner(int calls)
    {
        using var input = CreatePreparedContext();
        var outbox = new RecordingOutbox();
        var completions = 0;
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() => completions++));

        for (var i = 0; i < calls; i++)
        {
            context.Complete();
        }

        Assert.Equal(calls, completions);
        Assert.Empty(outbox.Sent);
        Assert.Empty(outbox.SentEnvelopes);
        Assert.Null(outbox.PreparedMessages);
        Assert.Equal(input.Envelope, context.Envelope);
        Assert.Equal(input.GrainId, context.GrainId);
    }

    [Fact]
    public void HandlerContext_Complete_PropagatesCallbackFailure()
    {
        using var input = CreatePreparedContext();
        var outbox = new RecordingOutbox();
        var expected = new InvalidOperationException("Wrong attempt.");
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions,
            (Action)(() => throw expected));

        var actual = Assert.Throws<InvalidOperationException>(context.Complete);

        Assert.Same(expected, actual);
        Assert.Empty(outbox.Sent);
        Assert.Empty(outbox.SentEnvelopes);
    }

    [Fact]
    public void HandlerContext_Constructor_RequiresCompletionCallback()
    {
        using var input = CreatePreparedContext();
        var type = typeof(IInboxHandlerContext).Assembly.GetType("Orleans.DurableMessaging.InboxHandlerContext", throwOnError: true)!;

        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            Activator.CreateInstance(type, [input.Envelope, input.GrainId, new RecordingOutbox(), _sessions, null]));

        Assert.Equal("complete", Assert.IsType<ArgumentNullException>(error.InnerException).ParamName);
        var constructor = Assert.Single(type.GetConstructors());
        var callback = constructor.GetParameters()[^1];
        Assert.Equal(typeof(Action), callback.ParameterType);
        Assert.False(callback.IsOptional);
    }

    [Fact]
    public async Task HandleAsync_CancellationAfterFirstMutation_StillCompletesFinalBlock()
    {
        using var input = CreatePreparedContext();
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, new RecordingOutbox(), _sessions,
            (Action)(() => events.Add("complete")));
        var handler = new DelegateHandler((actualContext, token) =>
        {
            token.ThrowIfCancellationRequested();
            events.Add("business");
            cancellation.Cancel();
            actualContext.Complete();
            return default;
        });

        await handler.HandleAsync(context, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["business", "complete"], events);
    }

    [Fact]
    public void SelectionContext_Complete_RejectsReadOnlyOperation()
    {
        using var input = CreatePreparedContext();
        var context = CreateInternalContext("InboxHandlerSelectionContext", input.Envelope, input.GrainId);

        var error = Assert.Throws<InvalidOperationException>(context.Complete);

        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
        Assert.Equal(input.Envelope, context.Envelope);
        Assert.Equal(input.GrainId, context.GrainId);
    }

    [Fact]
    public void HandlerAndContext_RequireAsyncHandleAndSynchronousCompletion()
    {
        var method = typeof(IInboxHandlerContext).GetMethod(nameof(IInboxHandlerContext.Complete))!;

        Assert.True(method.IsAbstract);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Empty(method.GetParameters());
        var handle = typeof(IInboxHandler).GetMethod(nameof(IInboxHandler.HandleAsync))!;
        Assert.True(handle.IsAbstract);
        Assert.Equal(typeof(ValueTask), handle.ReturnType);
        Assert.Null(typeof(IInboxHandler).GetMethod("PrepareAsync"));
        var typedHandle = typeof(IInboxHandler<RoutedMessage>).GetMethod(nameof(IInboxHandler.HandleAsync))!;
        Assert.True(typedHandle.IsAbstract);
        Assert.Equal(typeof(ValueTask), typedHandle.ReturnType);
        Assert.Contains(typedHandle.GetParameters()[0].GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(System.Diagnostics.CodeAnalysis.AllowNullAttribute));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task HandleAsync_Adapters_DoNotCompleteOnHandlersBehalf(string kind)
    {
        using var context = CreatePreparedContext();
        var calls = 0;
        var handler = CreateHandler(kind, (_, _) =>
        {
            calls++;
            return default;
        });

        await handler.HandleAsync(context, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompleteCount);
    }

    public void Dispose() => _services.Dispose();

    private static IInboxHandlerContext CreateInternalContext(string typeName, params object[] arguments)
    {
        var type = typeof(IInboxHandlerContext).Assembly.GetType($"Orleans.DurableMessaging.{typeName}", throwOnError: true)!;
        return Assert.IsAssignableFrom<IInboxHandlerContext>(Activator.CreateInstance(type, arguments));
    }

    private TestContext CreateContext(string route, HierarchicalKey? correlation = null, object? body = null)
    {
        var sender = GrainId.Create("sender", Guid.NewGuid().ToString("N"));
        var receiver = GrainId.Create("receiver", Guid.NewGuid().ToString("N"));
        var builder = new DurableEnvelopeBuilder(_sessions, sender).To(receiver, route);
        var envelope = body switch
        {
            RoutedMessage message => builder.WithBody(message).WithCorrelationKeyIfPresent(correlation).Build(),
            string text => builder.WithBody(text).WithCorrelationKeyIfPresent(correlation).Build(),
            _ => builder.WithBody(0).WithCorrelationKeyIfPresent(correlation).Build(),
        };
        return new TestContext(envelope, receiver);
    }

    private TestContext CreatePreparedContext() =>
        CreateContext("typed/request", HierarchicalKey.Create("workflow/request"), new RoutedMessage(42, "prepared"));

    private static IInboxHandler CreateHandler(
        string kind, Func<IInboxHandlerContext, CancellationToken, ValueTask> handle) => kind switch
        {
            "exact" => new DelegatingExactHandler(handle),
            "prefix" => new DelegatingPrefixHandler(handle),
            "correlation" => new DelegatingCorrelationHandler(handle),
            "untyped" => new DelegateHandler(handle),
            "typed" => new DelegateTypedHandler((message, context, token) =>
            {
                Assert.Equal(new RoutedMessage(42, "prepared"), message);
                return handle(context, token);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown handler kind.")
        };

    private sealed class DelegatingExactHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask> handle) : RouteKeyHandler("typed/request")
    {
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handle(context, cancellationToken);
    }

    private sealed class DelegatingPrefixHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask> handle) : RoutePrefixHandler("typed")
    {
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handle(context, cancellationToken);
    }

    private sealed class DelegatingCorrelationHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask> handle) : CorrelationHandler(HierarchicalKey.Create("workflow"))
    {
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handle(context, cancellationToken);
    }

    private sealed class DelegateHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask> handle) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handle(context, cancellationToken);
    }

    private sealed class DelegateTypedHandler(
        Func<RoutedMessage?, IInboxHandlerContext, CancellationToken, ValueTask> handle) : IInboxHandler<RoutedMessage>
    {
        public ValueTask HandleAsync(RoutedMessage? message, IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handle(message, context, cancellationToken);
    }

    private sealed class ExactHandler(string route) : RouteKeyHandler(route)
    {
        public string ExposedRoute => RouteKey;
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            context.Complete();
            return default;
        }
    }

    private sealed class PrefixHandler(string prefix) : RoutePrefixHandler(prefix)
    {
        public string ExposedPrefix => Prefix;
        public string? Suffix(string? route) => GetRouteSuffix(route);
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            context.Complete();
            return default;
        }
    }

    private sealed class HierarchyHandler(HierarchicalKey correlation) : CorrelationHandler(correlation)
    {
        public HierarchicalKey ExposedCorrelation => CorrelationKey;
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            context.Complete();
            return default;
        }
    }

    private sealed class TypedHandler : IInboxHandler<RoutedMessage>
    {
        public int CallCount { get; private set; }
        public RoutedMessage? Message { get; private set; }
        public IInboxHandlerContext? Context { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public ValueTask HandleAsync(RoutedMessage? message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            Message = message ?? throw new InvalidOperationException("A routed message is required.");
            Context = context;
            CancellationToken = cancellationToken;
            context.Complete();
            return default;
        }
    }

    private sealed class TestBatch : IPreparedOutboxBatch
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingOutbox : IDurableOutbox
    {
        public TaskCompletionSource<IPreparedOutboxBatch> Preparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<DurableEnvelope>? PreparedMessages { get; private set; }
        public CancellationToken PreparationToken { get; private set; }
        public List<IPreparedOutboxBatch> Sent { get; } = [];
        public List<DurableEnvelope> SentEnvelopes { get; } = [];
        public Exception? SendFailure { get; init; }
        public int Count => throw new NotSupportedException();
        public IEnumerable<DurableEnvelope> Messages => throw new NotSupportedException();

        public ValueTask<IPreparedOutboxBatch> PrepareSendAsync(IReadOnlyList<DurableEnvelope> messages, CancellationToken cancellationToken = default)
        {
            PreparedMessages = messages;
            PreparationToken = cancellationToken;
            return new(Preparation.Task);
        }

        public void Send(DurableEnvelope envelope)
        {
            if (SendFailure is { } failure)
            {
                throw failure;
            }

            SentEnvelopes.Add(envelope);
        }

        public void Send(IPreparedOutboxBatch batch)
        {
            if (SendFailure is { } failure)
            {
                throw failure;
            }

            Sent.Add(batch);
        }

        public bool TryGetMessage(Guid messageId, out DurableEnvelope envelope) => throw new NotSupportedException();
    }

    private sealed class TestContext(DurableEnvelope envelope, GrainId grainId, IDurableOutbox? outbox = null) : IInboxHandlerContext, IDisposable
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public GrainId GrainId { get; } = grainId;
        public IDurableOutbox Outbox => outbox ?? throw new NotSupportedException();
        public DurableEnvelopeBuilder CreateEnvelope() => throw new NotSupportedException();
        public void Send(IPreparedOutboxBatch batch) => throw new NotSupportedException();
        public int CompleteCount { get; private set; }
        public void Complete() => CompleteCount++;
        public void Dispose()
        {
        }
    }

    [GenerateSerializer, Immutable]
    public sealed record RoutedMessage([property: Id(0)] int Id, [property: Id(1)] string Value);
}

internal static class DurableEnvelopeBuilderTestExtensions
{
    public static DurableEnvelopeBuilder WithCorrelationKeyIfPresent(
        this DurableEnvelopeBuilder builder,
        HierarchicalKey? correlation) =>
        correlation is null ? builder : builder.WithCorrelationKey(correlation);
}
