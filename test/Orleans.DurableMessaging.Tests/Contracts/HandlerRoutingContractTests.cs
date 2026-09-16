using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
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

    [Fact]
    public void ExactRouteRegistration_PreservesHandlerIdentityAndRejectsReplacement()
    {
        var inbox = CreateInbox();
        var handler = Substitute.For<IInboxHandler>();
        inbox.RegisterHandler("orders/submit", handler);

        Assert.True(inbox.HasHandler("orders/submit"));
        Assert.True(inbox.TryGetHandler("orders/submit", out var registered));
        Assert.Same(handler, registered);

        Assert.Throws<InvalidOperationException>(
            () => inbox.RegisterHandler("orders/submit", Substitute.For<IInboxHandler>()));
        Assert.True(inbox.TryGetHandler("orders/submit", out registered));
        Assert.Same(handler, registered);
        Assert.False(inbox.TryGetHandler("orders/Submit", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public async Task ExactRouteSelection_PreservesPrecedenceAndOrdinalMatching()
    {
        var inbox = CreateInbox();
        var handler = Substitute.For<IInboxHandler>();
        var fallback = Substitute.For<IInboxHandler>();
        fallback.CanHandle(Arg.Any<IInboxHandlerContext>()).Returns(true);
        inbox.RegisterHandler(fallback);
        inbox.RegisterHandler("orders/submit", handler);
        using var exactContext = CreateContext("orders/submit");
        using var differentCaseContext = CreateContext("orders/Submit");
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var select = inbox.GetType().GetMethod("TryFindHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [exactContext, null];

        Assert.True((bool)select.Invoke(inbox, arguments)!);
        Assert.Same(handler, arguments[1]);
        await ((IInboxHandler)arguments[1]!).HandleAsync(exactContext, cancellationToken);

        handler.DidNotReceive().CanHandle(Arg.Any<IInboxHandlerContext>());
        fallback.DidNotReceive().CanHandle(Arg.Any<IInboxHandlerContext>());
        await handler.Received(1).HandleAsync(exactContext, cancellationToken);

        arguments = [differentCaseContext, null];
        Assert.True((bool)select.Invoke(inbox, arguments)!);
        Assert.Same(fallback, arguments[1]);
        fallback.Received(1).CanHandle(differentCaseContext);
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
        var apply = await ((IInboxHandler)handler).PrepareAsync(context, cancellation.Token);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(new RoutedMessage(81, "typed-body"), handler.Message);
        Assert.Same(context, handler.Context);
        Assert.Equal(cancellation.Token, handler.CancellationToken);
        Assert.NotNull(apply);
        Assert.Equal(0, handler.ApplyCount);
        Assert.Null(handler.AppliedMessage);

        apply();

        Assert.Equal(1, handler.ApplyCount);
        Assert.Equal(new RoutedMessage(81, "typed-body"), handler.AppliedMessage);
    }

    [Fact]
    public async Task TypedHandler_WrongType_ThrowsBeforeInvokingTypedMethod()
    {
        var handler = new TypedHandler();
        using var context = CreateContext("typed", body: "not-a-routed-message");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IInboxHandler)handler).PrepareAsync(context, CancellationToken.None));

        Assert.Contains(typeof(RoutedMessage).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("typed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
        Assert.Null(handler.Message);
        Assert.Equal(0, handler.ApplyCount);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task PrepareAsync_CompletedPreparation_PreservesActionAndArguments(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        using var context = CreatePreparedContext();
        var preparationCalls = 0;
        var applied = new List<string>();
        Action expected = () => applied.Add("applied");
        var handler = CreatePreparedHandler(kind, (actualContext, token) =>
        {
            Assert.Same(context, actualContext);
            Assert.Equal(cancellation.Token, token);
            preparationCalls++;
            return ValueTask.FromResult(expected);
        });

        Assert.True(handler.CanHandle(context));
        var preparation = handler.PrepareAsync(context, cancellation.Token);
        Assert.True(preparation.IsCompletedSuccessfully);
        var apply = await preparation;

        Assert.Equal(1, preparationCalls);
        Assert.Same(expected, apply);
        Assert.Empty(applied);

        apply();

        Assert.Equal(["applied"], applied);
        Assert.Equal(1, preparationCalls);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task PrepareAsync_AsyncPreparation_DefersApplyUntilInvoked(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        using var context = CreatePreparedContext();
        var completion = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCalls = 0;
        var applyCount = 0;
        Action expected = () => applyCount++;
        var handler = CreatePreparedHandler(kind, (actualContext, token) =>
        {
            Assert.Same(context, actualContext);
            Assert.Equal(cancellation.Token, token);
            preparationCalls++;
            return new ValueTask<Action>(completion.Task);
        });

        var preparation = handler.PrepareAsync(context, cancellation.Token);

        Assert.False(preparation.IsCompleted);
        Assert.Equal(1, preparationCalls);
        Assert.Equal(0, applyCount);
        completion.SetResult(expected);
        var apply = await preparation;
        Assert.Same(expected, apply);
        Assert.Equal(0, applyCount);

        apply();

        Assert.Equal(1, applyCount);
        Assert.Equal(1, preparationCalls);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task PrepareAsync_PreparationFailure_PreservesException(string kind)
    {
        using var context = CreatePreparedContext();
        var failure = new InvalidOperationException("Preparation failed.");
        var completion = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreatePreparedHandler(kind, (_, _) => new ValueTask<Action>(completion.Task));
        var preparation = handler.PrepareAsync(context, CancellationToken.None);
        Assert.False(preparation.IsCompleted);

        completion.SetException(failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => await preparation);
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task PrepareAsync_Cancellation_PreservesCancellationToken(string kind)
    {
        using var context = CreatePreparedContext();
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreatePreparedHandler(kind, (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            return new ValueTask<Action>(completion.Task);
        });
        var preparation = handler.PrepareAsync(context, cancellation.Token);
        Assert.False(preparation.IsCompleted);

        cancellation.Cancel();
        completion.SetCanceled(cancellation.Token);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await preparation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("correlation")]
    [InlineData("untyped")]
    [InlineData("typed")]
    public async Task PrepareAsync_ApplyFailure_IsDeferredToSynchronousInvocation(string kind)
    {
        using var context = CreatePreparedContext();
        var failure = new InvalidOperationException("Apply failed.");
        var applyCount = 0;
        Action expected = () =>
        {
            applyCount++;
            throw failure;
        };
        var handler = CreatePreparedHandler(kind, (_, _) => ValueTask.FromResult(expected));

        var apply = await handler.PrepareAsync(context, CancellationToken.None);

        Assert.Same(expected, apply);
        Assert.Equal(0, applyCount);
        var actual = Assert.Throws<InvalidOperationException>(apply);
        Assert.Same(failure, actual);
        Assert.Equal(1, applyCount);
    }

    [Fact]
    public async Task TypedHandler_NullBody_PreservesNullablePayloadAndDeferredApply()
    {
        var sender = GrainId.Create("sender", "null-body");
        var receiver = GrainId.Create("receiver", "null-body");
        var envelope = new DurableEnvelopeBuilder(_sessions, sender)
            .To(receiver, "typed/request")
            .WithBody<RoutedMessage?>(null)
            .Build();
        using var context = new TestContext(envelope, receiver);
        using var cancellation = new CancellationTokenSource();
        var preparationCalls = 0;
        var applyCount = 0;
        IInboxHandler handler = new DelegateTypedHandler((message, actualContext, token) =>
        {
            Assert.Null(message);
            Assert.Same(context, actualContext);
            Assert.Equal(cancellation.Token, token);
            preparationCalls++;
            return ValueTask.FromResult<Action>(() => applyCount++);
        });

        var apply = await handler.PrepareAsync(context, cancellation.Token);

        Assert.Equal(1, preparationCalls);
        Assert.Equal(0, applyCount);
        Assert.NotNull(apply);
        apply();
        Assert.Equal(1, applyCount);
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
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions);

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
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions);

        var actual = Assert.Throws<InvalidOperationException>(() => context.Send(batch));

        Assert.Same(expected, actual);
        Assert.Empty(outbox.Sent);
        Assert.Equal(0, batch.DisposeCount);
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
    public async Task PrepareAsync_PreparedBatch_RemainsLocalUntilApply(string kind)
    {
        using var input = CreatePreparedContext();
        using var batch = new TestBatch();
        using var cancellation = new CancellationTokenSource();
        var outbox = new RecordingOutbox();
        var context = CreateInternalContext("InboxHandlerContext", input.Envelope, input.GrainId, outbox, _sessions);
        IReadOnlyList<DurableEnvelope> messages = [input.Envelope];
        var applyCount = 0;
        var handler = CreatePreparedHandler(kind, async (actualContext, token) =>
        {
            var prepared = await actualContext.Outbox.PrepareSendAsync(messages, token);
            return () =>
            {
                applyCount++;
                actualContext.Send(prepared);
            };
        });

        var preparation = handler.PrepareAsync(context, cancellation.Token);

        Assert.False(preparation.IsCompleted);
        Assert.Same(messages, outbox.PreparedMessages);
        Assert.Equal(cancellation.Token, outbox.PreparationToken);
        Assert.Empty(outbox.Sent);
        Assert.Equal(0, applyCount);
        outbox.Preparation.SetResult(batch);
        var apply = await preparation;
        Assert.Empty(outbox.Sent);
        Assert.Equal(0, applyCount);
        Assert.Equal(0, batch.DisposeCount);

        apply();

        Assert.Equal(1, applyCount);
        Assert.Same(batch, Assert.Single(outbox.Sent));
        Assert.Equal(0, batch.DisposeCount);
    }

    public void Dispose() => _services.Dispose();

    private static IInboxHandlerContext CreateInternalContext(string typeName, params object[] arguments)
    {
        var type = typeof(IInboxHandlerContext).Assembly.GetType($"Orleans.DurableMessaging.{typeName}", throwOnError: true)!;
        return Assert.IsAssignableFrom<IInboxHandlerContext>(Activator.CreateInstance(type, arguments));
    }

    private static IDurableInbox CreateInbox()
    {
        var inboxType = typeof(IDurableInbox).Assembly.GetType("Orleans.DurableMessaging.DurableInbox", throwOnError: true)!;
        var messages = Substitute.For<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>();
        return (IDurableInbox)Activator.CreateInstance(inboxType, messages, 1000)!;
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

    private static IInboxHandler CreatePreparedHandler(
        string kind, Func<IInboxHandlerContext, CancellationToken, ValueTask<Action>> prepare) => kind switch
        {
            "exact" => new DelegatingExactHandler(prepare),
            "prefix" => new DelegatingPrefixHandler(prepare),
            "correlation" => new DelegatingCorrelationHandler(prepare),
            "untyped" => new DelegateHandler(prepare),
            "typed" => new DelegateTypedHandler((_, context, token) => prepare(context, token)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown handler kind.")
        };

    private sealed class DelegatingExactHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask<Action>> prepare) : RouteKeyHandler("typed/request")
    {
        protected override ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            prepare(context, cancellationToken);
    }

    private sealed class DelegatingPrefixHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask<Action>> prepare) : RoutePrefixHandler("typed")
    {
        protected override ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            prepare(context, cancellationToken);
    }

    private sealed class DelegatingCorrelationHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask<Action>> prepare) : CorrelationHandler(HierarchicalKey.Create("workflow"))
    {
        protected override ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            prepare(context, cancellationToken);
    }

    private sealed class DelegateHandler(
        Func<IInboxHandlerContext, CancellationToken, ValueTask<Action>> prepare) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            prepare(context, cancellationToken);
    }

    private sealed class DelegateTypedHandler(
        Func<RoutedMessage?, IInboxHandlerContext, CancellationToken, ValueTask<Action>> prepare) : IInboxHandler<RoutedMessage>
    {
        public ValueTask<Action> PrepareAsync(RoutedMessage? message, IInboxHandlerContext context, CancellationToken cancellationToken) =>
            prepare(message, context, cancellationToken);
    }

    private sealed class ExactHandler(string route) : RouteKeyHandler(route)
    {
        public string ExposedRoute => RouteKey;
        protected override ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Action>(static () => { });
    }

    private sealed class PrefixHandler(string prefix) : RoutePrefixHandler(prefix)
    {
        public string ExposedPrefix => Prefix;
        public string? Suffix(string? route) => GetRouteSuffix(route);
        protected override ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Action>(static () => { });
    }

    private sealed class HierarchyHandler(HierarchicalKey correlation) : CorrelationHandler(correlation)
    {
        public HierarchicalKey ExposedCorrelation => CorrelationKey;
        protected override ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Action>(static () => { });
    }

    private sealed class TypedHandler : IInboxHandler<RoutedMessage>
    {
        public int CallCount { get; private set; }
        public RoutedMessage? Message { get; private set; }
        public IInboxHandlerContext? Context { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int ApplyCount { get; private set; }
        public RoutedMessage? AppliedMessage { get; private set; }

        public ValueTask<Action> PrepareAsync(RoutedMessage? message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            Message = message ?? throw new InvalidOperationException("A routed message is required.");
            Context = context;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult<Action>(() =>
            {
                ApplyCount++;
                AppliedMessage = message;
            });
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
        public Exception? SendFailure { get; init; }
        public int Count => throw new NotSupportedException();
        public IEnumerable<DurableEnvelope> Messages => throw new NotSupportedException();

        public ValueTask<IPreparedOutboxBatch> PrepareSendAsync(IReadOnlyList<DurableEnvelope> messages, CancellationToken cancellationToken = default)
        {
            PreparedMessages = messages;
            PreparationToken = cancellationToken;
            return new(Preparation.Task);
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

    private sealed class TestContext(DurableEnvelope envelope, GrainId grainId) : IInboxHandlerContext, IDisposable
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public GrainId GrainId { get; } = grainId;
        public IDurableOutbox Outbox => throw new NotSupportedException();
        public DurableEnvelopeBuilder CreateEnvelope() => throw new NotSupportedException();
        public void Send(IPreparedOutboxBatch batch) => throw new NotSupportedException();
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
