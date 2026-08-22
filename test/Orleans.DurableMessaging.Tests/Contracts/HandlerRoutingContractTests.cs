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

        Assert.True(((IInboxHandler)handler).CanHandle(context));
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(new RoutedMessage(81, "typed-body"), handler.Message);
        Assert.Same(context, handler.Context);
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
    }

    public void Dispose() => _services.Dispose();

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

    private sealed class ExactHandler(string route) : RouteKeyHandler(route)
    {
        public string ExposedRoute => RouteKey;
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) => default;
    }

    private sealed class PrefixHandler(string prefix) : RoutePrefixHandler(prefix)
    {
        public string ExposedPrefix => Prefix;
        public string? Suffix(string? route) => GetRouteSuffix(route);
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) => default;
    }

    private sealed class HierarchyHandler(HierarchicalKey correlation) : CorrelationHandler(correlation)
    {
        public HierarchicalKey ExposedCorrelation => CorrelationKey;
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) => default;
    }

    private sealed class TypedHandler : IInboxHandler<RoutedMessage>
    {
        public int CallCount { get; private set; }
        public RoutedMessage? Message { get; private set; }
        public IInboxHandlerContext? Context { get; private set; }

        public ValueTask HandleAsync(RoutedMessage? message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            Message = message ?? throw new InvalidOperationException("A routed message is required.");
            Context = context;
            return default;
        }
    }

    private sealed class TestContext(DurableEnvelope envelope, GrainId grainId) : IInboxHandlerContext, IDisposable
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public GrainId GrainId { get; } = grainId;
        public IDurableOutbox Outbox => throw new NotSupportedException();
        public DurableEnvelopeBuilder CreateEnvelope() => throw new NotSupportedException();
        public void Send(DurableEnvelope envelope) => throw new NotSupportedException();
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
