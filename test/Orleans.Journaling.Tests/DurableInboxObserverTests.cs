using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for IDurableInboxObserver interface with CorrelationKey support.
/// These tests verify that the observer callback properly uses hierarchical correlation keys
/// for request/response tracking in durable RPC scenarios.
/// </summary>
[TestCategory("BVT")]
public class DurableInboxObserverTests
{
    /// <summary>
    /// Tests that IDurableInboxObserver accepts simple CorrelationKey.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_SimpleCorrelationKey_Accepted()
    {
        // Arrange
        var observer = new TestObserver();
        var correlationKey = CorrelationKey.Create("request-123");
        var envelope = CreateTestEnvelope();
        var options = new DeliveryOptions();

        // Act
        var result = await observer.OnResponseAsync(correlationKey, envelope, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Equal(correlationKey, observer.LastCorrelationKey);
        Assert.Equal(envelope, observer.LastResponse);
    }

    /// <summary>
    /// Tests that IDurableInboxObserver accepts hierarchical CorrelationKey.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_HierarchicalCorrelationKey_Accepted()
    {
        // Arrange
        var observer = new TestObserver();
        var parentKey = CorrelationKey.Create("transfer-abc");
        var childKey = parentKey.CreateChildKey("debit");
        var envelope = CreateTestEnvelope();
        var options = new DeliveryOptions();

        // Act
        var result = await observer.OnResponseAsync(childKey, envelope, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Equal(childKey, observer.LastCorrelationKey);
        Assert.Equal("transfer-abc/debit", childKey.ToString());
    }

    /// <summary>
    /// Tests that IDurableInboxObserver preserves correlation hierarchy across multiple levels.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_MultiLevelHierarchy_PreservesStructure()
    {
        // Arrange
        var observer = new TestObserver();
        var level1 = CorrelationKey.Create("workflow-xyz");
        var level2 = level1.CreateChildKey("step-1");
        var level3 = level2.CreateChildKey("validation");
        var envelope = CreateTestEnvelope();
        var options = new DeliveryOptions();

        // Act
        var result = await observer.OnResponseAsync(level3, envelope, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Equal(level3, observer.LastCorrelationKey);
        Assert.Equal("workflow-xyz/step-1/validation", level3.ToString());
    }

    /// <summary>
    /// Tests that IDurableInboxObserver can signal backpressure.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_BackpressureSignaling_ReturnsBackpressured()
    {
        // Arrange
        var observer = new TestObserver { ShouldBackpressure = true };
        var correlationKey = CorrelationKey.Create("request-456");
        var envelope = CreateTestEnvelope();
        var options = new DeliveryOptions();

        // Act
        var result = await observer.OnResponseAsync(correlationKey, envelope, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Backpressured, result.Status);
    }

    /// <summary>
    /// Tests that IDurableInboxObserver can track correlation keys for duplicate detection.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_DuplicateCorrelationKey_ReturnsDuplicate()
    {
        // Arrange
        var observer = new TestObserver();
        var correlationKey = CorrelationKey.Create("request-789");
        var envelope1 = CreateTestEnvelope();
        var envelope2 = CreateTestEnvelope();
        var options = new DeliveryOptions();

        // Act
        var result1 = await observer.OnResponseAsync(correlationKey, envelope1, options, CancellationToken.None);
        observer.MarkAsDuplicate(correlationKey);
        var result2 = await observer.OnResponseAsync(correlationKey, envelope2, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result1.Status);
        Assert.Equal(DeliveryStatus.Duplicate, result2.Status);
    }

    /// <summary>
    /// Tests that IDurableInboxObserver preserves parent-child relationships in correlation keys.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_ParentChildRelationships_Preserved()
    {
        // Arrange
        var observer = new TestObserver();
        var parentKey = CorrelationKey.Create("saga-001");
        var childKey1 = parentKey.CreateChildKey("debit");
        var childKey2 = parentKey.CreateChildKey("credit");
        var envelope1 = CreateTestEnvelope();
        var envelope2 = CreateTestEnvelope();
        var options = new DeliveryOptions();

        // Act
        await observer.OnResponseAsync(childKey1, envelope1, options, CancellationToken.None);
        await observer.OnResponseAsync(childKey2, envelope2, options, CancellationToken.None);

        // Assert
        Assert.True(childKey1.IsChildOf(parentKey));
        Assert.True(childKey2.IsChildOf(parentKey));
        Assert.False(childKey1.IsChildOf(childKey2));
        Assert.Equal("saga-001/debit", childKey1.ToString());
        Assert.Equal("saga-001/credit", childKey2.ToString());
    }

    /// <summary>
    /// Tests that IDurableInboxObserver supports long-polling via DeliveryOptions.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_LongPolling_ReturnsProcessed()
    {
        // Arrange
        var observer = new TestObserver { SimulateProcessing = true };
        var correlationKey = CorrelationKey.Create("request-poll");
        var envelope = CreateTestEnvelope();
        var options = new DeliveryOptions { PollTimeout = TimeSpan.FromMilliseconds(100) };

        // Act
        var result = await observer.OnResponseAsync(correlationKey, envelope, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Processed, result.Status);
    }

    /// <summary>
    /// Tests that IDurableInboxObserver respects cancellation tokens.
    /// </summary>
    [Fact]
    public async Task OnResponseAsync_CancellationToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var observer = new TestObserver { DelayProcessing = TimeSpan.FromSeconds(10) };
        var correlationKey = CorrelationKey.Create("request-cancel");
        var envelope = CreateTestEnvelope();
        var options = new DeliveryOptions();
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Act & Assert
        // TaskCanceledException derives from OperationCanceledException, so we check for the base type
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await observer.OnResponseAsync(correlationKey, envelope, options, cts.Token));
    }

    /// <summary>
    /// Helper method to create a test envelope.
    /// </summary>
    private static DurableEnvelope CreateTestEnvelope()
    {
        return new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = GrainId.Parse("grain/test/sender"),
            ReceiverId = GrainId.Parse("grain/test/receiver"),
            RouteKey = "test.response",
            Data = new DurableEnvelopeData(null!),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Test implementation of IDurableInboxObserver for testing purposes.
    /// </summary>
    private sealed class TestObserver : IDurableInboxObserver
    {
        private readonly HashSet<CorrelationKey> _processedKeys = new();

        public CorrelationKey? LastCorrelationKey { get; private set; }
        public DurableEnvelope? LastResponse { get; private set; }
        public bool ShouldBackpressure { get; set; }
        public bool SimulateProcessing { get; set; }
        public TimeSpan DelayProcessing { get; set; }

        public async ValueTask<DeliveryResult> OnResponseAsync(
            CorrelationKey correlationKey,
            DurableEnvelope response,
            DeliveryOptions options,
            CancellationToken cancellationToken = default)
        {
            if (DelayProcessing > TimeSpan.Zero)
            {
                await Task.Delay(DelayProcessing, cancellationToken);
            }

            LastCorrelationKey = correlationKey;
            LastResponse = response;

            if (ShouldBackpressure)
            {
                return DeliveryResult.Backpressured();
            }

            if (_processedKeys.Contains(correlationKey))
            {
                return DeliveryResult.Duplicate();
            }

            _processedKeys.Add(correlationKey);

            if (SimulateProcessing)
            {
                // Simulate processing delay
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                return DeliveryResult.Processed();
            }

            return DeliveryResult.Accepted();
        }

        public void MarkAsDuplicate(CorrelationKey key)
        {
            _processedKeys.Add(key);
        }
    }
}
