using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for IInboxHandler.CanHandle method behavior.
/// </summary>
[TestCategory("BVT")]
public class InboxHandlerCanHandleTests
{
    /// <summary>
    /// Tests that typed handlers (IInboxHandler&lt;TMessage&gt;) return true by default.
    /// </summary>
    [Fact]
    public void TypedHandler_CanHandle_ReturnsTrue()
    {
        var handler = new TestTypedHandler();
        var context = CreateMockContext();

        // Cast to IInboxHandler to access the explicit interface implementation
        var result = ((IInboxHandler)handler).CanHandle(context);

        Assert.True(result);
    }

    /// <summary>
    /// Tests that custom CanHandle implementation can filter based on route key.
    /// </summary>
    [Fact]
    public void CustomHandler_CanHandle_FiltersBasedOnRouteKey()
    {
        var handler = new RouteFilteringHandler("expected-route");
        var contextMatch = CreateMockContext(routeKey: "expected-route");
        var contextNoMatch = CreateMockContext(routeKey: "different-route");

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that custom CanHandle implementation can filter based on correlation key.
    /// </summary>
    [Fact]
    public void CustomHandler_CanHandle_FiltersBasedOnCorrelationKey()
    {
        var expectedKey = global::Orleans.HierarchicalKey.Create("workflow-123");
        var handler = new CorrelationFilteringHandler(expectedKey);
        
        var contextMatch = CreateMockContext(correlationKey: expectedKey);
        var contextNoMatch = CreateMockContext(correlationKey: global::Orleans.HierarchicalKey.Create("workflow-456"));

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that custom CanHandle implementation can filter based on prefix matching.
    /// </summary>
    [Fact]
    public void CustomHandler_CanHandle_FiltersBasedOnPrefix()
    {
        var handler = new PrefixFilteringHandler("rpc/");
        
        var contextMatch1 = CreateMockContext(routeKey: "rpc/request");
        var contextMatch2 = CreateMockContext(routeKey: "rpc/reply");
        var contextNoMatch = CreateMockContext(routeKey: "job/execute");

        Assert.True(handler.CanHandle(contextMatch1));
        Assert.True(handler.CanHandle(contextMatch2));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that typed handler with custom CanHandle can override default behavior.
    /// </summary>
    [Fact]
    public void TypedHandlerWithCustomCanHandle_FiltersMessages()
    {
        var handler = new SelectiveTypedHandler();
        
        var contextMatch = CreateMockContext(routeKey: "payment/process");
        var contextNoMatch = CreateMockContext(routeKey: "order/process");

        // Cast to IInboxHandler to access the explicit interface implementation
        Assert.True(((IInboxHandler)handler).CanHandle(contextMatch));
        Assert.False(((IInboxHandler)handler).CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that CanHandle receives correct context with envelope data.
    /// </summary>
    [Fact]
    public void CanHandle_ReceivesCorrectContext()
    {
        var handler = new ContextVerifyingHandler();
        var senderId = GrainId.Create("test-sender", Guid.NewGuid().ToString("N"));
        var receiverId = GrainId.Create("test-receiver", Guid.NewGuid().ToString("N"));
        var correlationKey = global::Orleans.HierarchicalKey.Create("test/correlation");
        
        var context = CreateMockContext(
            routeKey: "test-route",
            senderId: senderId,
            receiverId: receiverId,
            correlationKey: correlationKey);

        handler.CanHandle(context);

        Assert.Equal("test-route", handler.LastRouteKey);
        Assert.Equal(correlationKey, handler.LastCorrelationKey);
        Assert.Equal(receiverId, handler.LastGrainId);
    }

    // Helper method to create a mock context
    private static IInboxHandlerContext CreateMockContext(
        string routeKey = "default-route",
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
            RouteKey = routeKey,
            CorrelationKey = correlationKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new DurableEnvelopeData(null!)
        };

        return new MockInboxHandlerContext(envelope, receiverId.Value);
    }

    // Test handler implementations

    private sealed class TestTypedHandler : IInboxHandler<TestMessage>
    {
        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RouteFilteringHandler : IInboxHandler
    {
        private readonly string _expectedRoute;

        public RouteFilteringHandler(string expectedRoute)
        {
            _expectedRoute = expectedRoute;
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey == _expectedRoute;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CorrelationFilteringHandler : IInboxHandler
    {
        private readonly global::Orleans.HierarchicalKey _expectedKey;

        public CorrelationFilteringHandler(global::Orleans.HierarchicalKey expectedKey)
        {
            _expectedKey = expectedKey;
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.CorrelationKey?.Equals(_expectedKey) == true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PrefixFilteringHandler : IInboxHandler
    {
        private readonly string _prefix;

        public PrefixFilteringHandler(string prefix)
        {
            _prefix = prefix;
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey?.StartsWith(_prefix) == true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SelectiveTypedHandler : IInboxHandler<TestMessage>
    {
        bool IInboxHandler.CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey == "payment/process";
        }

        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextVerifyingHandler : IInboxHandler
    {
        public string? LastRouteKey { get; private set; }
        public global::Orleans.HierarchicalKey? LastCorrelationKey { get; private set; }
        public GrainId LastGrainId { get; private set; }

        public bool CanHandle(IInboxHandlerContext context)
        {
            LastRouteKey = context.Envelope.RouteKey;
            LastCorrelationKey = context.Envelope.CorrelationKey;
            LastGrainId = context.GrainId;
            return true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
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

    [GenerateSerializer]
    public sealed record TestMessage
    {
        [Id(0)] public required string Data { get; init; }
    }
}
