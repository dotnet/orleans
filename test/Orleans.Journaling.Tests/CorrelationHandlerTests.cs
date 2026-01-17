using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for the CorrelationHandler base class.
/// </summary>
[TestCategory("BVT")]
public class CorrelationHandlerTests
{
    /// <summary>
    /// Tests that CorrelationHandler matches messages with exact correlation key.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_MatchesExactCorrelationKey()
    {
        var correlationKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(correlationKey);
        var contextMatch = CreateMockContext(correlationKey: correlationKey);
        var contextNoMatch = CreateMockContext(correlationKey: HierarchicalKey.Create("workflow/order-456"));

        Assert.True(handler.CanHandle(contextMatch));
        Assert.False(handler.CanHandle(contextNoMatch));
    }

    /// <summary>
    /// Tests that CorrelationHandler matches messages with child correlation keys.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_MatchesChildCorrelationKey()
    {
        var parentKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(parentKey);
        
        var childKey = HierarchicalKey.Create("workflow/order-123/payment");
        var contextChild = CreateMockContext(correlationKey: childKey);

        Assert.True(handler.CanHandle(contextChild));
    }

    /// <summary>
    /// Tests that CorrelationHandler matches messages with grandchild correlation keys.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_MatchesGrandchildCorrelationKey()
    {
        var parentKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(parentKey);
        
        var grandchildKey = HierarchicalKey.Create("workflow/order-123/payment/verify");
        var contextGrandchild = CreateMockContext(correlationKey: grandchildKey);

        Assert.True(handler.CanHandle(contextGrandchild));
    }

    /// <summary>
    /// Tests that CorrelationHandler does not match parent correlation keys.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_DoesNotMatchParentCorrelationKey()
    {
        var childKey = HierarchicalKey.Create("workflow/order-123/payment");
        var handler = new TestCorrelationHandler(childKey);
        
        var parentKey = HierarchicalKey.Create("workflow/order-123");
        var contextParent = CreateMockContext(correlationKey: parentKey);

        Assert.False(handler.CanHandle(contextParent));
    }

    /// <summary>
    /// Tests that CorrelationHandler does not match unrelated correlation keys.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_DoesNotMatchUnrelatedCorrelationKey()
    {
        var key1 = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(key1);
        
        var key2 = HierarchicalKey.Create("workflow/payment-456");
        var contextUnrelated = CreateMockContext(correlationKey: key2);

        Assert.False(handler.CanHandle(contextUnrelated));
    }

    /// <summary>
    /// Tests that CorrelationHandler does not match null correlation keys.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_DoesNotMatchNullCorrelationKey()
    {
        var correlationKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(correlationKey);
        var context = CreateMockContext(correlationKey: null);

        Assert.False(handler.CanHandle(context));
    }

    /// <summary>
    /// Tests that CorrelationHandler throws ArgumentNullException for null correlation key in constructor.
    /// </summary>
    [Fact]
    public void CorrelationHandler_Constructor_ThrowsOnNullCorrelationKey()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new TestCorrelationHandler(null!));
        Assert.Equal("correlationKey", exception.ParamName);
    }

    /// <summary>
    /// Tests that CorrelationHandler.CorrelationKey property returns the configured correlation key.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CorrelationKeyProperty_ReturnsConfiguredValue()
    {
        var correlationKey = HierarchicalKey.Create("workflow/payment-789");
        var handler = new TestCorrelationHandler(correlationKey);
        
        Assert.Equal(correlationKey, handler.ExposedCorrelationKey);
    }

    /// <summary>
    /// Tests that CorrelationHandler.HandleAsync is called when CanHandle returns true.
    /// </summary>
    [Fact]
    public async Task CorrelationHandler_HandleAsync_CalledWhenCanHandleReturnsTrue()
    {
        var correlationKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(correlationKey);
        var context = CreateMockContext(correlationKey: correlationKey);

        Assert.True(handler.CanHandle(context));
        
        // Call HandleAsync through the interface
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        Assert.True(handler.WasHandleCalled);
        Assert.Same(context, handler.LastContext);
    }

    /// <summary>
    /// Tests that CorrelationHandler respects cancellation token.
    /// </summary>
    [Fact]
    public async Task CorrelationHandler_HandleAsync_RespectsCancellationToken()
    {
        var correlationKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new CancellableCorrelationHandler(correlationKey);
        var context = CreateMockContext(correlationKey: correlationKey);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await ((IInboxHandler)handler).HandleAsync(context, cts.Token);
        });
    }

    /// <summary>
    /// Tests that multiple CorrelationHandler instances with different correlation keys work independently.
    /// </summary>
    [Fact]
    public void CorrelationHandler_MultipleInstances_WorkIndependently()
    {
        var orderKey = HierarchicalKey.Create("workflow/order-123");
        var paymentKey = HierarchicalKey.Create("workflow/payment-456");
        
        var orderHandler = new TestCorrelationHandler(orderKey);
        var paymentHandler = new TestCorrelationHandler(paymentKey);

        var orderContext = CreateMockContext(correlationKey: orderKey);
        var paymentContext = CreateMockContext(correlationKey: paymentKey);

        // Order handler should only match order correlation keys
        Assert.True(orderHandler.CanHandle(orderContext));
        Assert.False(orderHandler.CanHandle(paymentContext));

        // Payment handler should only match payment correlation keys
        Assert.False(paymentHandler.CanHandle(orderContext));
        Assert.True(paymentHandler.CanHandle(paymentContext));
    }

    /// <summary>
    /// Tests that CorrelationHandler works with hierarchical workflows where child messages are handled.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_HandlesHierarchicalWorkflows()
    {
        var rootKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new TestCorrelationHandler(rootKey);

        // Root workflow message
        var rootContext = CreateMockContext(correlationKey: rootKey);
        Assert.True(handler.CanHandle(rootContext));

        // Payment child workflow message
        var paymentKey = HierarchicalKey.Create("workflow/order-123/payment");
        var paymentContext = CreateMockContext(correlationKey: paymentKey);
        Assert.True(handler.CanHandle(paymentContext));

        // Shipping child workflow message
        var shippingKey = HierarchicalKey.Create("workflow/order-123/shipping");
        var shippingContext = CreateMockContext(correlationKey: shippingKey);
        Assert.True(handler.CanHandle(shippingContext));

        // Verification grandchild workflow message
        var verificationKey = HierarchicalKey.Create("workflow/order-123/payment/verification");
        var verificationContext = CreateMockContext(correlationKey: verificationKey);
        Assert.True(handler.CanHandle(verificationContext));

        // Unrelated workflow message
        var unrelatedKey = HierarchicalKey.Create("workflow/order-456");
        var unrelatedContext = CreateMockContext(correlationKey: unrelatedKey);
        Assert.False(handler.CanHandle(unrelatedContext));
    }

    /// <summary>
    /// Tests that CorrelationHandler can distinguish between exact match and child workflows.
    /// </summary>
    [Fact]
    public async Task CorrelationHandler_HandleAsync_CanDistinguishExactMatchFromChild()
    {
        var rootKey = HierarchicalKey.Create("workflow/order-123");
        var handler = new DistinguishingCorrelationHandler(rootKey);

        // Test exact match
        var exactContext = CreateMockContext(correlationKey: rootKey);
        await ((IInboxHandler)handler).HandleAsync(exactContext, CancellationToken.None);
        Assert.True(handler.WasExactMatch);
        Assert.False(handler.WasChildMatch);

        // Test child match
        handler.Reset();
        var childKey = HierarchicalKey.Create("workflow/order-123/payment");
        var childContext = CreateMockContext(correlationKey: childKey);
        await ((IInboxHandler)handler).HandleAsync(childContext, CancellationToken.None);
        Assert.False(handler.WasExactMatch);
        Assert.True(handler.WasChildMatch);
    }

    /// <summary>
    /// Tests that CorrelationHandler works with complex hierarchical keys containing multiple segments.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_WorksWithComplexHierarchicalKeys()
    {
        var complexKey = HierarchicalKey.Create("tenant/customer-xyz/workflow/order-123");
        var handler = new TestCorrelationHandler(complexKey);

        // Exact match
        var exactContext = CreateMockContext(correlationKey: complexKey);
        Assert.True(handler.CanHandle(exactContext));

        // Child
        var childKey = HierarchicalKey.Create("tenant/customer-xyz/workflow/order-123/payment");
        var childContext = CreateMockContext(correlationKey: childKey);
        Assert.True(handler.CanHandle(childContext));

        // Grandchild
        var grandchildKey = HierarchicalKey.Create("tenant/customer-xyz/workflow/order-123/payment/verification");
        var grandchildContext = CreateMockContext(correlationKey: grandchildKey);
        Assert.True(handler.CanHandle(grandchildContext));

        // Different order in same tenant
        var differentOrderKey = HierarchicalKey.Create("tenant/customer-xyz/workflow/order-456");
        var differentOrderContext = CreateMockContext(correlationKey: differentOrderKey);
        Assert.False(handler.CanHandle(differentOrderContext));

        // Different tenant
        var differentTenantKey = HierarchicalKey.Create("tenant/customer-abc/workflow/order-123");
        var differentTenantContext = CreateMockContext(correlationKey: differentTenantKey);
        Assert.False(handler.CanHandle(differentTenantContext));
    }

    /// <summary>
    /// Tests that CorrelationHandler works with escaped characters in correlation keys.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_WorksWithEscapedCharacters()
    {
        // Create a key with escaped forward slash
        var escapedKey = HierarchicalKey.CreateEscaped("workflow/order-with\\/slash");
        var handler = new TestCorrelationHandler(escapedKey);

        // Exact match
        var exactContext = CreateMockContext(correlationKey: escapedKey);
        Assert.True(handler.CanHandle(exactContext));

        // Child
        var childKey = escapedKey.CreateChildKey("payment");
        var childContext = CreateMockContext(correlationKey: childKey);
        Assert.True(handler.CanHandle(childContext));
    }

    /// <summary>
    /// Tests that CorrelationHandler correctly validates IsAncestorOf semantics.
    /// </summary>
    [Fact]
    public void CorrelationHandler_CanHandle_UsesIsAncestorOfSemantics()
    {
        var key = HierarchicalKey.Create("a/b");
        var handler = new TestCorrelationHandler(key);

        // IsAncestorOf returns true for exact match (a key is an ancestor of itself)
        var exactContext = CreateMockContext(correlationKey: key);
        Assert.True(handler.CanHandle(exactContext));

        // IsAncestorOf returns true for direct child
        var childKey = HierarchicalKey.Create("a/b/c");
        var childContext = CreateMockContext(correlationKey: childKey);
        Assert.True(handler.CanHandle(childContext));

        // IsAncestorOf returns true for grandchild
        var grandchildKey = HierarchicalKey.Create("a/b/c/d");
        var grandchildContext = CreateMockContext(correlationKey: grandchildKey);
        Assert.True(handler.CanHandle(grandchildContext));

        // IsAncestorOf returns false for parent
        var parentKey = HierarchicalKey.Create("a");
        var parentContext = CreateMockContext(correlationKey: parentKey);
        Assert.False(handler.CanHandle(parentContext));

        // IsAncestorOf returns false for unrelated key
        var unrelatedKey = HierarchicalKey.Create("x/y");
        var unrelatedContext = CreateMockContext(correlationKey: unrelatedKey);
        Assert.False(handler.CanHandle(unrelatedContext));
    }

    // Helper method to create a mock context
    private static IInboxHandlerContext CreateMockContext(
        string? routeKey = "default-route",
        GrainId? senderId = null,
        GrainId? receiverId = null,
        HierarchicalKey? correlationKey = null)
    {
        senderId ??= GrainId.Create("test-sender", Guid.NewGuid().ToString("N"));
        receiverId ??= GrainId.Create("test-receiver", Guid.NewGuid().ToString("N"));

        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = senderId.Value,
            ReceiverId = receiverId.Value,
            RouteKey = routeKey!,
            CorrelationKey = correlationKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new DurableEnvelopeData(null!)
        };

        return new MockInboxHandlerContext(envelope, receiverId.Value);
    }

    // Test handler implementation
    private sealed class TestCorrelationHandler : CorrelationHandler
    {
        public TestCorrelationHandler(HierarchicalKey correlationKey) : base(correlationKey)
        {
        }

        public bool WasHandleCalled { get; private set; }
        public IInboxHandlerContext? LastContext { get; private set; }

        // Expose the protected CorrelationKey property for testing
        public HierarchicalKey ExposedCorrelationKey => CorrelationKey;

        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasHandleCalled = true;
            LastContext = context;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellableCorrelationHandler : CorrelationHandler
    {
        public CancellableCorrelationHandler(HierarchicalKey correlationKey) : base(correlationKey)
        {
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);
        }
    }

    private sealed class DistinguishingCorrelationHandler : CorrelationHandler
    {
        public DistinguishingCorrelationHandler(HierarchicalKey correlationKey) : base(correlationKey)
        {
        }

        public bool WasExactMatch { get; private set; }
        public bool WasChildMatch { get; private set; }

        public void Reset()
        {
            WasExactMatch = false;
            WasChildMatch = false;
        }

        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            if (context.Envelope.CorrelationKey?.Equals(CorrelationKey) == true)
            {
                WasExactMatch = true;
            }
            else
            {
                WasChildMatch = true;
            }
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
    }
}
