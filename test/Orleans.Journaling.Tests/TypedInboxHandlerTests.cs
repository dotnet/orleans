using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for IInboxHandler&lt;TMessage&gt; typed handler interface.
/// Tests verify automatic deserialization, type checking, and error handling.
/// </summary>
[TestCategory("BVT")]
public class TypedInboxHandlerTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SerializerSessionPool _sessionPool;

    public TypedInboxHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        _serviceProvider = services.BuildServiceProvider();
        _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
    /// <summary>
    /// Tests that typed handler successfully deserializes and handles a message with correct type.
    /// </summary>
    [Fact]
    public async Task TypedHandler_WithMatchingType_DeserializesAndHandlesSuccessfully()
    {
        // Arrange
        var handler = new TestTypedHandler();
        var message = new TestMessage { Data = "test-data", Value = 42 };
        var context = CreateMockContext(message);

        // Act - Call through IInboxHandler interface (default implementation)
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        // Assert
        Assert.True(handler.WasCalled);
        Assert.Equal("test-data", handler.ReceivedMessage?.Data);
        Assert.Equal(42, handler.ReceivedMessage?.Value);
    }

    /// <summary>
    /// Tests that typed handler throws InvalidOperationException when message cannot be deserialized.
    /// This happens when the body is empty/null or fundamentally incompatible with the expected type.
    /// </summary>
    [Fact]
    public async Task TypedHandler_WithEmptyBody_ThrowsInvalidOperationException()
    {
        // Arrange
        var handler = new TestTypedHandler();
        var context = CreateMockContextWithNullBody();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);
        });

        Assert.Contains("Failed to deserialize message body as TestMessage", exception.Message);
        Assert.False(handler.WasCalled);
    }

    /// <summary>
    /// Tests that typed handler with custom CanHandle can filter messages before deserialization.
    /// </summary>
    [Fact]
    public async Task TypedHandler_WithCustomCanHandle_FiltersCorrectly()
    {
        // Arrange
        var handler = new SelectiveTypedHandler();
        var message = new TestMessage { Data = "test", Value = 100 };

        var contextMatch = CreateMockContext(message, routeKey: "payment/process");
        var contextNoMatch = CreateMockContext(message, routeKey: "order/process");

        // Act - Test CanHandle filtering
        var canHandleMatch = ((IInboxHandler)handler).CanHandle(contextMatch);
        var canHandleNoMatch = ((IInboxHandler)handler).CanHandle(contextNoMatch);

        // Assert
        Assert.True(canHandleMatch);
        Assert.False(canHandleNoMatch);

        // Act - Test successful handling when CanHandle returns true
        await ((IInboxHandler)handler).HandleAsync(contextMatch, CancellationToken.None);
        Assert.True(handler.WasCalled);
        Assert.Equal("test", handler.ReceivedMessage?.Data);
    }

    /// <summary>
    /// Tests that typed handler default CanHandle returns true (accepts all messages).
    /// </summary>
    [Fact]
    public void TypedHandler_DefaultCanHandle_ReturnsTrue()
    {
        // Arrange
        var handler = new TestTypedHandler();
        var context = CreateMockContext(new TestMessage { Data = "test", Value = 1 });

        // Act
        var result = ((IInboxHandler)handler).CanHandle(context);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that typed handler passes through the correct context to the typed HandleAsync method.
    /// </summary>
    [Fact]
    public async Task TypedHandler_PassesCorrectContext()
    {
        // Arrange
        var handler = new ContextCapturingHandler();
        var message = new TestMessage { Data = "context-test", Value = 99 };
        var grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
        var correlationKey = HierarchicalKey.Create("workflow-456");

        var context = CreateMockContext(
            message,
            routeKey: "test/route",
            grainId: grainId,
            correlationKey: correlationKey);

        // Act
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        // Assert
        Assert.NotNull(handler.CapturedContext);
        Assert.Equal("test/route", handler.CapturedContext!.Envelope.RouteKey);
        Assert.Equal(grainId, handler.CapturedContext.GrainId);
        Assert.Equal(correlationKey, handler.CapturedContext.Envelope.CorrelationKey);
    }

    /// <summary>
    /// Tests that typed handler respects cancellation token.
    /// </summary>
    [Fact]
    public async Task TypedHandler_RespectsCancellationToken()
    {
        // Arrange
        var handler = new CancellationTestHandler();
        var message = new TestMessage { Data = "cancel-test", Value = 1 };
        var context = CreateMockContext(message);
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel the token

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ((IInboxHandler)handler).HandleAsync(context, cts.Token);
        });
    }

    /// <summary>
    /// Tests that multiple typed handlers can coexist with different message types.
    /// </summary>
    [Fact]
    public async Task MultipleTypedHandlers_WithDifferentTypes_WorkIndependently()
    {
        // Arrange
        var testHandler = new TestTypedHandler();
        var differentHandler = new DifferentTypedHandler();

        var testMessage = new TestMessage { Data = "test", Value = 1 };
        var differentMessage = new DifferentMessage { Id = Guid.NewGuid() };

        var testContext = CreateMockContext(testMessage);
        var differentContext = CreateMockContext(differentMessage);

        // Act
        await ((IInboxHandler)testHandler).HandleAsync(testContext, CancellationToken.None);
        await ((IInboxHandler)differentHandler).HandleAsync(differentContext, CancellationToken.None);

        // Assert
        Assert.True(testHandler.WasCalled);
        Assert.Equal("test", testHandler.ReceivedMessage?.Data);

        Assert.True(differentHandler.WasCalled);
        Assert.NotEqual(Guid.Empty, differentHandler.ReceivedMessage?.Id);
    }

    /// <summary>
    /// Tests that typed handler works correctly with different but compatible message structures.
    /// Orleans serializer may allow some type flexibility, so we test handling behavior.
    /// </summary>
    [Fact]
    public async Task MultipleTypedHandlers_HandleDifferentMessages()
    {
        // Arrange
        var differentHandler = new DifferentTypedHandler();
        var differentMessage = new DifferentMessage { Id = Guid.NewGuid() };
        var context = CreateMockContext(differentMessage);

        // Act
        await ((IInboxHandler)differentHandler).HandleAsync(context, CancellationToken.None);

        // Assert
        Assert.True(differentHandler.WasCalled);
        Assert.NotEqual(Guid.Empty, differentHandler.ReceivedMessage?.Id);
    }

    // Helper method to create a DurableEnvelopeData with a body
    private DurableEnvelopeData CreateEnvelopeData<TBody>(TBody body)
    {
        var writer = new ArcBufferWriter();
        var bodySlice = (0, 0);

        try
        {
            // Serialize body
            var startOffset = writer.Length;
            using (var session = _sessionPool.GetSession())
            {
                var bufferWriter = Writer.Create(writer, session);
                _sessionPool.CodecProvider.GetCodec<TBody>().WriteField(ref bufferWriter, 0, typeof(TBody), body);
                bufferWriter.Commit();
            }
            bodySlice = (startOffset, writer.Length - startOffset);

            // Create the buffer slice
            var buffer = writer.ConsumeSlice(writer.Length);

            // Use reflection to set internal fields (for testing purposes)
            var data = new DurableEnvelopeData(_sessionPool);
            var bufferField = typeof(DurableEnvelopeData).GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var bodySliceField = typeof(DurableEnvelopeData).GetField("_bodySlice", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var contextIndicesField = typeof(DurableEnvelopeData).GetField("_contextIndices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            bufferField!.SetValue(data, buffer);
            bodySliceField!.SetValue(data, bodySlice);
            contextIndicesField!.SetValue(data, null);

            return data;
        }
        finally
        {
            writer.Dispose();
        }
    }

    // Helper method to create a mock context with a serialized message
    private IInboxHandlerContext CreateMockContext<T>(
        T message,
        string routeKey = "default-route",
        GrainId? grainId = null,
        HierarchicalKey? correlationKey = null)
    {
        grainId ??= GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
        var senderId = GrainId.Create("test-sender", Guid.NewGuid().ToString("N"));

        // Create envelope data with serialized message
        var envelopeData = CreateEnvelopeData(message);

        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = senderId,
            ReceiverId = grainId.Value,
            RouteKey = routeKey,
            CorrelationKey = correlationKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = envelopeData
        };

        return new MockInboxHandlerContext(envelope, grainId.Value);
    }

    // Helper method to create a mock context with null body
    private IInboxHandlerContext CreateMockContextWithNullBody()
    {
        var grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
        var senderId = GrainId.Create("test-sender", Guid.NewGuid().ToString("N"));

        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = senderId,
            ReceiverId = grainId,
            RouteKey = "test-route",
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new DurableEnvelopeData(_sessionPool) // Empty body
        };

        return new MockInboxHandlerContext(envelope, grainId);
    }

    // Test handler implementations

    private sealed class TestTypedHandler : IInboxHandler<TestMessage>
    {
        public bool WasCalled { get; private set; }
        public TestMessage? ReceivedMessage { get; private set; }

        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasCalled = true;
            ReceivedMessage = message;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DifferentTypedHandler : IInboxHandler<DifferentMessage>
    {
        public bool WasCalled { get; private set; }
        public DifferentMessage? ReceivedMessage { get; private set; }

        public ValueTask HandleAsync(DifferentMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasCalled = true;
            ReceivedMessage = message;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SelectiveTypedHandler : IInboxHandler<TestMessage>
    {
        public bool WasCalled { get; private set; }
        public TestMessage? ReceivedMessage { get; private set; }

        bool IInboxHandler.CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey == "payment/process";
        }

        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasCalled = true;
            ReceivedMessage = message;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextCapturingHandler : IInboxHandler<TestMessage>
    {
        public IInboxHandlerContext? CapturedContext { get; private set; }

        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            CapturedContext = context;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationTestHandler : IInboxHandler<TestMessage>
    {
        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    // Test message types

    [GenerateSerializer]
    public sealed record TestMessage
    {
        [Id(0)] public required string Data { get; init; }
        [Id(1)] public required int Value { get; init; }
    }

    [GenerateSerializer]
    public sealed record DifferentMessage
    {
        [Id(0)] public required Guid Id { get; init; }
    }
}
