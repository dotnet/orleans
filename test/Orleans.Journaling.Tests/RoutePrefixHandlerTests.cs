using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for the RoutePrefixHandler base class.
/// </summary>
[TestCategory("BVT")]
public class RoutePrefixHandlerTests
{
    /// <summary>
    /// Tests that RoutePrefixHandler matches messages with route keys that start with the prefix.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_MatchesRouteKeysWithPrefix()
    {
        var handler = new TestRoutePrefixHandler("rpc/");
        var contextRequestMatch = CreateMockContext(routeKey: "rpc/request");
        var contextReplyMatch = CreateMockContext(routeKey: "rpc/reply");
        var contextNoMatch = CreateMockContext(routeKey: "order/process");

        Assert.True(handler.CanHandle(contextRequestMatch));
        Assert.True(handler.CanHandle(contextReplyMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler does not match null route keys.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_DoesNotMatchNullRouteKey()
    {
        var handler = new TestRoutePrefixHandler("rpc/");
        var context = CreateMockContext(routeKey: null);

        Assert.False(handler.CanHandle(context));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler performs case-sensitive prefix matching.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_IsCaseSensitive()
    {
        var handler = new TestRoutePrefixHandler("rpc/");
        var contextLowerCase = CreateMockContext(routeKey: "rpc/request");
        var contextUpperCase = CreateMockContext(routeKey: "RPC/request");
        var contextMixedCase = CreateMockContext(routeKey: "Rpc/request");

        Assert.True(handler.CanHandle(contextLowerCase));
        Assert.False(handler.CanHandle(contextUpperCase));
        Assert.False(handler.CanHandle(contextMixedCase));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler constructor normalizes prefix to end with '/'.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_Constructor_NormalizesPrefixToEndWithSlash()
    {
        // Without trailing slash
        var handlerWithoutSlash = new TestRoutePrefixHandler("rpc");
        Assert.Equal("rpc/", handlerWithoutSlash.ExposedPrefix);
        Assert.True(handlerWithoutSlash.CanHandle(CreateMockContext(routeKey: "rpc/request")));

        // With trailing slash
        var handlerWithSlash = new TestRoutePrefixHandler("rpc/");
        Assert.Equal("rpc/", handlerWithSlash.ExposedPrefix);
        Assert.True(handlerWithSlash.CanHandle(CreateMockContext(routeKey: "rpc/request")));
    }

    /// <summary>
    /// Tests that prefix normalization prevents false matches.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_PreventsPartialWordMatches()
    {
        var handler = new TestRoutePrefixHandler("order");
        
        // Should match "order/process" (normalized to "order/")
        Assert.True(handler.CanHandle(CreateMockContext(routeKey: "order/process")));
        
        // Should NOT match "order-archive/process" (boundary protection)
        Assert.False(handler.CanHandle(CreateMockContext(routeKey: "order-archive/process")));
        
        // Should NOT match exact "order" without slash
        Assert.False(handler.CanHandle(CreateMockContext(routeKey: "order")));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler matches the prefix itself when it ends with '/'.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_MatchesPrefixItself()
    {
        var handler = new TestRoutePrefixHandler("rpc/");
        
        // Should match exactly "rpc/"
        Assert.True(handler.CanHandle(CreateMockContext(routeKey: "rpc/")));
        
        // Should also match longer routes
        Assert.True(handler.CanHandle(CreateMockContext(routeKey: "rpc/request")));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler throws ArgumentNullException for null prefix in constructor.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_Constructor_ThrowsOnNullPrefix()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new TestRoutePrefixHandler(null!));
        Assert.Equal("prefix", exception.ParamName);
    }

    /// <summary>
    /// Tests that RoutePrefixHandler throws ArgumentException for empty prefix in constructor.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_Constructor_ThrowsOnEmptyPrefix()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TestRoutePrefixHandler(""));
        Assert.Equal("prefix", exception.ParamName);
    }

    /// <summary>
    /// Tests that RoutePrefixHandler throws ArgumentException for whitespace prefix in constructor.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_Constructor_ThrowsOnWhitespacePrefix()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TestRoutePrefixHandler("   "));
        Assert.Equal("prefix", exception.ParamName);
    }

    /// <summary>
    /// Tests that RoutePrefixHandler.Prefix property returns the normalized prefix.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_PrefixProperty_ReturnsNormalizedValue()
    {
        var handler1 = new TestRoutePrefixHandler("payment");
        Assert.Equal("payment/", handler1.ExposedPrefix);

        var handler2 = new TestRoutePrefixHandler("payment/");
        Assert.Equal("payment/", handler2.ExposedPrefix);
    }

    /// <summary>
    /// Tests that GetRouteSuffix returns the correct suffix after the prefix.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_GetRouteSuffix_ReturnsCorrectSuffix()
    {
        var handler = new TestRoutePrefixHandler("rpc/");

        Assert.Equal("request", handler.GetRouteSuffix("rpc/request"));
        Assert.Equal("reply", handler.GetRouteSuffix("rpc/reply"));
        Assert.Equal("v2/request", handler.GetRouteSuffix("rpc/v2/request"));
        Assert.Equal("", handler.GetRouteSuffix("rpc/"));
    }

    /// <summary>
    /// Tests that GetRouteSuffix returns null for non-matching route keys.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_GetRouteSuffix_ReturnsNullForNonMatching()
    {
        var handler = new TestRoutePrefixHandler("rpc/");

        Assert.Null(handler.GetRouteSuffix("order/process"));
        Assert.Null(handler.GetRouteSuffix(null));
        Assert.Null(handler.GetRouteSuffix(""));
    }

    /// <summary>
    /// Tests that GetRouteSuffix works correctly with nested paths.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_GetRouteSuffix_WorksWithNestedPaths()
    {
        var handler = new TestRoutePrefixHandler("api/v1/");

        Assert.Equal("users/create", handler.GetRouteSuffix("api/v1/users/create"));
        Assert.Equal("orders/process", handler.GetRouteSuffix("api/v1/orders/process"));
        Assert.Null(handler.GetRouteSuffix("api/v2/users/create"));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler.HandleAsync is called when CanHandle returns true.
    /// </summary>
    [Fact]
    public async Task RoutePrefixHandler_HandleAsync_CalledWhenCanHandleReturnsTrue()
    {
        var handler = new TestRoutePrefixHandler("rpc/");
        var context = CreateMockContext(routeKey: "rpc/request");

        Assert.True(handler.CanHandle(context));
        
        // Call HandleAsync through the interface
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.True(handler.WasHandleCalled);
        Assert.Same(context, handler.LastContext);
    }

    /// <summary>
    /// Tests that RoutePrefixHandler respects cancellation token.
    /// </summary>
    [Fact]
    public async Task RoutePrefixHandler_HandleAsync_RespectsCancellationToken()
    {
        var handler = new CancellableRoutePrefixHandler("rpc/");
        var context = CreateMockContext(routeKey: "rpc/request");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await ((IInboxHandler)handler).HandleAsync(context, cts.Token);
        });
    }

    /// <summary>
    /// Tests that multiple RoutePrefixHandler instances with different prefixes work independently.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_MultipleInstances_WorkIndependently()
    {
        var rpcHandler = new TestRoutePrefixHandler("rpc/");
        var orderHandler = new TestRoutePrefixHandler("order/");

        var rpcContext = CreateMockContext(routeKey: "rpc/request");
        var orderContext = CreateMockContext(routeKey: "order/process");

        // RPC handler should only match RPC routes
        Assert.True(rpcHandler.CanHandle(rpcContext));
        Assert.False(rpcHandler.CanHandle(orderContext));

        // Order handler should only match order routes
        Assert.False(orderHandler.CanHandle(rpcContext));
        Assert.True(orderHandler.CanHandle(orderContext));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler works with prefixes containing special characters.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_WorksWithSpecialCharacters()
    {
        var handler = new TestRoutePrefixHandler("my-prefix_v1.0/");
        var contextMatch = CreateMockContext(routeKey: "my-prefix_v1.0/action");
        var contextNoMatch = CreateMockContext(routeKey: "my-prefix_v2.0/action");

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler works with deeply nested prefixes.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_WorksWithDeeplyNestedPrefixes()
    {
        var handler = new TestRoutePrefixHandler("api/v1/internal/");
        var contextMatch = CreateMockContext(routeKey: "api/v1/internal/users/list");
        var contextPartialMatch = CreateMockContext(routeKey: "api/v1/public/users/list");
        var contextNoMatch = CreateMockContext(routeKey: "api/v2/internal/users/list");

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextPartialMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler matches routes with additional slashes.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_CanHandle_MatchesRoutesWithAdditionalSlashes()
    {
        var handler = new TestRoutePrefixHandler("workflow/");
        
        Assert.True(handler.CanHandle(CreateMockContext(routeKey: "workflow/start")));
        Assert.True(handler.CanHandle(CreateMockContext(routeKey: "workflow/task/execute")));
        Assert.True(handler.CanHandle(CreateMockContext(routeKey: "workflow/task/complete/success")));
    }

    /// <summary>
    /// Tests that RoutePrefixHandler can be used to implement operation-based routing.
    /// </summary>
    [Fact]
    public void RoutePrefixHandler_GetRouteSuffix_EnablesOperationBasedRouting()
    {
        var handler = new TestRoutePrefixHandler("rpc/");
        
        var requestContext = CreateMockContext(routeKey: "rpc/request");
        var replyContext = CreateMockContext(routeKey: "rpc/reply");
        
        Assert.Equal("request", handler.GetRouteSuffix(requestContext.Envelope.RouteKey));
        Assert.Equal("reply", handler.GetRouteSuffix(replyContext.Envelope.RouteKey));
        
        // Handler can use suffix for switch/case routing
        Assert.True(handler.CanHandle(requestContext));
        Assert.True(handler.CanHandle(replyContext));
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
    private sealed class TestRoutePrefixHandler : RoutePrefixHandler
    {
        public TestRoutePrefixHandler(string prefix) : base(prefix)
        {
        }

        public bool WasHandleCalled { get; private set; }
        public IInboxHandlerContext? LastContext { get; private set; }

        // Expose the protected Prefix property for testing
        public string ExposedPrefix => Prefix;

        // Expose the protected GetRouteSuffix method for testing
        public new string? GetRouteSuffix(string? routeKey) => base.GetRouteSuffix(routeKey);

        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasHandleCalled = true;
            LastContext = context;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellableRoutePrefixHandler : RoutePrefixHandler
    {
        public CancellableRoutePrefixHandler(string prefix) : base(prefix)
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
