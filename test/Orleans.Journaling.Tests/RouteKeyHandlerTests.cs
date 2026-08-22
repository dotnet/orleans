using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for the RouteKeyHandler base class.
/// </summary>
[TestCategory("BVT")]
public class RouteKeyHandlerTests
{
    /// <summary>
    /// Tests that RouteKeyHandler matches messages with exact route key.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_CanHandle_MatchesExactRouteKey()
    {
        var handler = new TestRouteKeyHandler("order/process");
        var contextMatch = CreateMockContext(routeKey: "order/process");
        var contextNoMatch = CreateMockContext(routeKey: "order/cancel");

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that RouteKeyHandler does not match null route keys.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_CanHandle_DoesNotMatchNullRouteKey()
    {
        var handler = new TestRouteKeyHandler("order/process");
        var context = CreateMockContext(routeKey: null);

        Assert.False(handler.CanHandle(context));
    }

    /// <summary>
    /// Tests that RouteKeyHandler performs case-sensitive matching.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_CanHandle_IsCaseSensitive()
    {
        var handler = new TestRouteKeyHandler("order/process");
        var contextLowerCase = CreateMockContext(routeKey: "order/process");
        var contextUpperCase = CreateMockContext(routeKey: "ORDER/PROCESS");
        var contextMixedCase = CreateMockContext(routeKey: "Order/Process");

        Assert.True(handler.CanHandle(contextLowerCase));
        Assert.False(handler.CanHandle(contextUpperCase));
        Assert.False(handler.CanHandle(contextMixedCase));
    }

    /// <summary>
    /// Tests that RouteKeyHandler does not perform prefix matching.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_CanHandle_DoesNotMatchPrefix()
    {
        var handler = new TestRouteKeyHandler("order");
        var contextExact = CreateMockContext(routeKey: "order");
        var contextPrefix = CreateMockContext(routeKey: "order/process");
        var contextPrefixWithSlash = CreateMockContext(routeKey: "order/");

        Assert.True(handler.CanHandle(contextExact));
        Assert.False(handler.CanHandle(contextPrefix));
        Assert.False(handler.CanHandle(contextPrefixWithSlash));
    }

    /// <summary>
    /// Tests that RouteKeyHandler throws ArgumentNullException for null route key in constructor.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_Constructor_ThrowsOnNullRouteKey()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new TestRouteKeyHandler(null!));
        Assert.Equal("routeKey", exception.ParamName);
    }

    /// <summary>
    /// Tests that RouteKeyHandler throws ArgumentException for empty route key in constructor.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_Constructor_ThrowsOnEmptyRouteKey()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TestRouteKeyHandler(""));
        Assert.Equal("routeKey", exception.ParamName);
    }

    /// <summary>
    /// Tests that RouteKeyHandler throws ArgumentException for whitespace route key in constructor.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_Constructor_ThrowsOnWhitespaceRouteKey()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TestRouteKeyHandler("   "));
        Assert.Equal("routeKey", exception.ParamName);
    }

    /// <summary>
    /// Tests that RouteKeyHandler.RouteKey property returns the configured route key.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_RouteKeyProperty_ReturnsConfiguredValue()
    {
        var handler = new TestRouteKeyHandler("payment/process");
        Assert.Equal("payment/process", handler.ExposedRouteKey);
    }

    /// <summary>
    /// Tests that RouteKeyHandler.HandleAsync is called when CanHandle returns true.
    /// </summary>
    [Fact]
    public async Task RouteKeyHandler_HandleAsync_CalledWhenCanHandleReturnsTrue()
    {
        var handler = new TestRouteKeyHandler("order/process");
        var context = CreateMockContext(routeKey: "order/process");

        Assert.True(handler.CanHandle(context));

        // Call HandleAsync through the interface
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.True(handler.WasHandleCalled);
        Assert.Same(context, handler.LastContext);
    }

    /// <summary>
    /// Tests that RouteKeyHandler respects cancellation token.
    /// </summary>
    [Fact]
    public async Task RouteKeyHandler_HandleAsync_RespectsCancellationToken()
    {
        var handler = new CancellableRouteKeyHandler("order/process");
        var context = CreateMockContext(routeKey: "order/process");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await ((IInboxHandler)handler).HandleAsync(context, cts.Token);
        });
    }

    /// <summary>
    /// Tests that multiple RouteKeyHandler instances with different routes work independently.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_MultipleInstances_WorkIndependently()
    {
        var orderHandler = new TestRouteKeyHandler("order/process");
        var paymentHandler = new TestRouteKeyHandler("payment/process");

        var orderContext = CreateMockContext(routeKey: "order/process");
        var paymentContext = CreateMockContext(routeKey: "payment/process");

        // Order handler should only match order routes
        Assert.True(orderHandler.CanHandle(orderContext));
        Assert.False(orderHandler.CanHandle(paymentContext));

        // Payment handler should only match payment routes
        Assert.False(paymentHandler.CanHandle(orderContext));
        Assert.True(paymentHandler.CanHandle(paymentContext));
    }

    /// <summary>
    /// Tests that RouteKeyHandler works with routes containing special characters.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_CanHandle_WorksWithSpecialCharacters()
    {
        var handler = new TestRouteKeyHandler("route/with-dashes_and_underscores.and.dots");
        var contextMatch = CreateMockContext(routeKey: "route/with-dashes_and_underscores.and.dots");
        var contextNoMatch = CreateMockContext(routeKey: "route/with-dashes");

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that RouteKeyHandler works with routes containing forward slashes.
    /// </summary>
    [Fact]
    public void RouteKeyHandler_CanHandle_WorksWithForwardSlashes()
    {
        var handler = new TestRouteKeyHandler("rpc/v2/order/process");
        var contextMatch = CreateMockContext(routeKey: "rpc/v2/order/process");
        var contextNoMatch = CreateMockContext(routeKey: "rpc/v2/order/cancel");

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    // Helper method to create a mock context
    private static IInboxHandlerContext CreateMockContext(
        string? routeKey = "default-route",
        GrainId? senderId = null,
        GrainId? receiverId = null,
        global::Orleans.HierarchicalKey? correlationKey = null)
    {
        senderId ??= GrainId.Create("test-sender", Guid.NewGuid().ToString("N"));
        receiverId ??= GrainId.Create("test-receiver", Guid.NewGuid().ToString("N"));

        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = senderId.Value,
            ReceiverId = receiverId.Value,
            RouteKey = routeKey!, // Intentionally allowing null for testing
            CorrelationKey = correlationKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new DurableEnvelopeData(null!)
        };

        return new MockInboxHandlerContext(envelope, receiverId.Value);
    }

    // Test handler implementation
    private sealed class TestRouteKeyHandler : RouteKeyHandler
    {
        public TestRouteKeyHandler(string routeKey) : base(routeKey)
        {
        }

        public bool WasHandleCalled { get; private set; }
        public IInboxHandlerContext? LastContext { get; private set; }

        // Expose the protected RouteKey property for testing
        public string ExposedRouteKey => RouteKey;

        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasHandleCalled = true;
            LastContext = context;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellableRouteKeyHandler : RouteKeyHandler
    {
        public CancellableRouteKeyHandler(string routeKey) : base(routeKey)
        {
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);
        }
    }

    private sealed class MockInboxHandlerContext : IInboxHandlerContext
    {
        public MockInboxHandlerContext(DurableEnvelope envelope, GrainId grainId)
        {
            Envelope = envelope;
            GrainId = grainId;
        }

        public DurableEnvelope Envelope { get; }
        public GrainId GrainId { get; }

        public DurableEnvelopeBuilder CreateEnvelope()
        {
            throw new NotImplementedException();
        }

        public void Send(DurableEnvelope envelope)
        {
            throw new NotImplementedException();
        }

        public IDurableOutbox Outbox => throw new NotImplementedException();

        public void SendError(string errorCode, string message, bool isRetriable = false)
        {
            // No-op for testing
        }

        public void SendError(Exception exception, bool isRetriable = false)
        {
            // No-op for testing
        }
    }
}
