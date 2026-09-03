using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Integration tests for deserialization failure handling in durable messaging.
/// Tests verify that the system gracefully handles type mismatches, missing types,
/// and other deserialization errors without crashing grains or losing messages.
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling")]
public class DeserializationFailureTests : IClassFixture<DeserializationFailureTests.Fixture>
{
    private readonly Fixture _fixture;

    public DeserializationFailureTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests that TryGetBody returns false on type mismatch during message handling.
    /// Verifies that the grain can continue operating and doesn't crash.
    /// </summary>
    [Fact]
    public async Task Handler_WithTypeMismatch_ContinuesOperationGracefully()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<ISenderGrain>(Guid.NewGuid());
        var typeMismatchGrain = _fixture.Client.GetGrain<ITypeMismatchHandlerGrain>(Guid.NewGuid());

        // Act - Send message with string body, but handler expects int
        await senderGrain.SendTypeMismatchMessage(typeMismatchGrain.GetGrainId(), "this is a string, not an int");

        // Wait for type mismatch to be detected
        await TestHelpers.WaitUntilAsync(
            async () => await typeMismatchGrain.GetTypeMismatchDetected(),
            message: "Type mismatch was not detected");

        // Assert - Grain should have detected type mismatch and handled it gracefully
        var handled = await typeMismatchGrain.GetHandledSuccessfully();
        var typeMismatchDetected = await typeMismatchGrain.GetTypeMismatchDetected();

        Assert.False(handled, "Message should not have been handled successfully due to type mismatch");
        Assert.True(typeMismatchDetected, "Handler should have detected the type mismatch");

        // Verify grain is still responsive after type mismatch
        var isAlive = await typeMismatchGrain.Ping();
        Assert.True(isAlive, "Grain should still be alive after type mismatch");
    }

    /// <summary>
    /// Tests that TryGetContextValue returns false on type mismatch for context values.
    /// Verifies that context value deserialization failures don't affect body deserialization.
    /// </summary>
    [Fact]
    public async Task Handler_WithContextTypeMismatch_AccessesBodySuccessfully()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<ISenderGrain>(Guid.NewGuid());
        var contextMismatchGrain = _fixture.Client.GetGrain<IContextMismatchHandlerGrain>(Guid.NewGuid());

        // Act - Send message with string context value, but handler expects int
        await senderGrain.SendMessageWithBadContext(
            contextMismatchGrain.GetGrainId(),
            new SimpleMessage { Value = "valid body" },
            contextKey: "userId",
            contextValue: "string-not-int");

        // Wait for message to be processed
        await TestHelpers.WaitUntilAsync(
            async () => await contextMismatchGrain.GetBodyReceived(),
            message: "Body was not received");

        // Assert - Body should be accessible despite context type mismatch
        var bodyReceived = await contextMismatchGrain.GetBodyReceived();
        var contextMismatchDetected = await contextMismatchGrain.GetContextMismatchDetected();

        Assert.True(bodyReceived, "Body should be accessible despite context type mismatch");
        Assert.True(contextMismatchDetected, "Handler should have detected context type mismatch");
        Assert.Equal("valid body", await contextMismatchGrain.GetReceivedValue());
    }

    /// <summary>
    /// Tests grain recovery when body type is unavailable at deserialization time.
    /// Simulates a scenario where message was sent with type A, but grain expects type B.
    /// </summary>
    [Fact]
    public async Task Handler_WithUnavailableType_SkipsMessageGracefully()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<ISenderGrain>(Guid.NewGuid());
        var unavailableTypeGrain = _fixture.Client.GetGrain<IUnavailableTypeHandlerGrain>(Guid.NewGuid());

        // Act - Send ComplexMessage that handler tries to deserialize as SimpleMessage first (will fail)
        // Then handler tries ComplexMessage (will succeed)
        await senderGrain.SendComplexMessage(unavailableTypeGrain.GetGrainId(), new ComplexMessage
        {
            Id = 123,
            Data = "test",
            Nested = new NestedData { Value = 456 }
        });

        // Wait for first message to be processed
        await TestHelpers.WaitUntilAsync(
            async () => await unavailableTypeGrain.GetSuccessfulMessageCount() >= 1,
            message: "First message was not processed");

        // Send a valid SimpleMessage afterwards
        await senderGrain.SendSimpleMessage(unavailableTypeGrain.GetGrainId(), new SimpleMessage { Value = "valid" });

        // Wait for second message to be processed
        await TestHelpers.WaitUntilAsync(
            async () => await unavailableTypeGrain.GetSuccessfulMessageCount() >= 2,
            message: "Second message was not processed");

        // Assert - Both messages should have been processed successfully
        // First as ComplexMessage (after SimpleMessage attempt failed), second as SimpleMessage
        var successCount = await unavailableTypeGrain.GetSuccessfulMessageCount();

        Assert.Equal(2, successCount);
    }

    /// <summary>
    /// Tests handler that uses fallback logic when body deserialization fails.
    /// Verifies that handlers can implement graceful degradation strategies.
    /// </summary>
    [Fact]
    public async Task Handler_WithDeserializationFailure_UsesFallbackLogic()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<ISenderGrain>(Guid.NewGuid());
        var fallbackGrain = _fixture.Client.GetGrain<IFallbackHandlerGrain>(Guid.NewGuid());

        // Act - Send message with wrong type
        await senderGrain.SendTypeMismatchMessage(fallbackGrain.GetGrainId(), "wrong type");

        // Wait for fallback to be used
        await TestHelpers.WaitUntilAsync(
            async () => await fallbackGrain.GetUsedFallback(),
            message: "Fallback was not used");

        // Assert - Fallback handler should have used raw bytes
        var usedFallback = await fallbackGrain.GetUsedFallback();
        var rawBytesReceived = await fallbackGrain.GetRawBytesReceived();

        Assert.True(usedFallback, "Handler should have used fallback logic");
        Assert.True(rawBytesReceived, "Handler should have accessed raw bytes");
    }

    /// <summary>
    /// Tests that multiple messages can be processed where some fail and some succeed.
    /// Verifies that one bad message doesn't prevent processing of subsequent messages.
    /// </summary>
    [Fact]
    public async Task Handler_WithMixedMessages_ProcessesValidOnesOnly()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<ISenderGrain>(Guid.NewGuid());
        var mixedGrain = _fixture.Client.GetGrain<IMixedMessageHandlerGrain>(Guid.NewGuid());

        // Act - Send mix of valid and invalid messages
        await senderGrain.SendSimpleMessage(mixedGrain.GetGrainId(), new SimpleMessage { Value = "valid1" });
        await senderGrain.SendTypeMismatchMessage(mixedGrain.GetGrainId(), 12345); // Wrong type
        await senderGrain.SendSimpleMessage(mixedGrain.GetGrainId(), new SimpleMessage { Value = "valid2" });
        await senderGrain.SendTypeMismatchMessage(mixedGrain.GetGrainId(), true); // Wrong type
        await senderGrain.SendSimpleMessage(mixedGrain.GetGrainId(), new SimpleMessage { Value = "valid3" });

        // Wait for all 5 messages to be processed (3 valid + 2 invalid)
        await TestHelpers.WaitUntilAsync(
            async () =>
            {
                var valid = await mixedGrain.GetValidMessageCount();
                var invalid = await mixedGrain.GetInvalidMessageCount();
                return valid + invalid >= 5;
            },
            message: "Not all messages were processed");

        // Assert - Only valid messages should be processed
        var validCount = await mixedGrain.GetValidMessageCount();
        var invalidCount = await mixedGrain.GetInvalidMessageCount();
        var processedValues = await mixedGrain.GetProcessedValues();

        Assert.Equal(3, validCount);
        Assert.Equal(2, invalidCount);
        Assert.Equal(3, processedValues.Count);
        Assert.Contains("valid1", processedValues);
        Assert.Contains("valid2", processedValues);
        Assert.Contains("valid3", processedValues);
    }

    /// <summary>
    /// Tests grain reactivation after deserialization failure.
    /// Verifies that grain state is preserved and can continue processing after deactivation.
    /// </summary>
    [Fact]
    public async Task Handler_AfterDeserializationFailure_SurvivesDeactivation()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<ISenderGrain>(Guid.NewGuid());
        var survivorGrain = _fixture.Client.GetGrain<ISurvivorGrain>(Guid.NewGuid());

        // Act - Send bad message, then deactivate, then send good message
        await senderGrain.SendTypeMismatchMessage(survivorGrain.GetGrainId(), 999); // Bad message

        // Wait briefly for bad message to be processed (or skipped)
        await TestHelpers.TryWaitUntilAsync(
            async () => await survivorGrain.GetLastReceivedValue() is not null,
            timeout: TimeSpan.FromMilliseconds(300));

        var activationBefore = await survivorGrain.GetActivationId();
        await survivorGrain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Wait for grain to be reactivated with new activation id
        await TestHelpers.WaitUntilAsync(
            async () => await survivorGrain.GetActivationId() != activationBefore,
            message: "Grain was not reactivated");

        // Send valid message to reactivate
        await senderGrain.SendSimpleMessage(survivorGrain.GetGrainId(), new SimpleMessage { Value = "after-reactivation" });

        // Wait for valid message to be processed
        await TestHelpers.WaitUntilAsync(
            async () => await survivorGrain.GetLastReceivedValue() == "after-reactivation",
            message: "Valid message was not received after reactivation");

        var activationAfter = await survivorGrain.GetActivationId();

        // Assert - Grain should have survived and reactivated
        Assert.NotEqual(activationBefore, activationAfter);
        var receivedAfterReactivation = await survivorGrain.GetLastReceivedValue();
        Assert.Equal("after-reactivation", receivedAfterReactivation);
    }

    /// <summary>
    /// Test fixture that configures the cluster with durable messaging.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromDays(7);
                    opts.EnableLongPolling = false;
                });
            });
        }
    }
}

// ============================================================================
// Test Message Types
// ============================================================================

[GenerateSerializer]
public record SimpleMessage
{
    [Id(0)] public required string Value { get; init; }
}

[GenerateSerializer]
public record ComplexMessage
{
    [Id(0)] public int Id { get; init; }
    [Id(1)] public required string Data { get; init; }
    [Id(2)] public NestedData? Nested { get; init; }
}

[GenerateSerializer]
public record NestedData
{
    [Id(0)] public int Value { get; init; }
}

// ============================================================================
// Test Grain Interfaces
// ============================================================================

public interface ISenderGrain : IGrainWithGuidKey
{
    Task SendSimpleMessage(GrainId target, SimpleMessage message);
    Task SendComplexMessage(GrainId target, ComplexMessage message);
    Task SendTypeMismatchMessage(GrainId target, object wrongTypeMessage);
    Task SendMessageWithBadContext(GrainId target, SimpleMessage message, string contextKey, object contextValue);
}

public interface ITypeMismatchHandlerGrain : IGrainWithGuidKey
{
    Task<bool> GetHandledSuccessfully();
    Task<bool> GetTypeMismatchDetected();
    Task<bool> Ping();
}

public interface IContextMismatchHandlerGrain : IGrainWithGuidKey
{
    Task<bool> GetBodyReceived();
    Task<bool> GetContextMismatchDetected();
    Task<string?> GetReceivedValue();
}

public interface IUnavailableTypeHandlerGrain : IGrainWithGuidKey
{
    Task<int> GetFailedDeserializationCount();
    Task<int> GetSuccessfulMessageCount();
}

public interface IFallbackHandlerGrain : IGrainWithGuidKey
{
    Task<bool> GetUsedFallback();
    Task<bool> GetRawBytesReceived();
}

public interface IMixedMessageHandlerGrain : IGrainWithGuidKey
{
    Task<int> GetValidMessageCount();
    Task<int> GetInvalidMessageCount();
    Task<List<string>> GetProcessedValues();
}

public interface ISurvivorGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task<string?> GetLastReceivedValue();
}

// ============================================================================
// Test Grain Implementations
// ============================================================================

/// <summary>
/// Sender grain that sends various message types to test deserialization failures.
/// </summary>
public class SenderGrain : DurableGrain, ISenderGrain
{
    private readonly IDurableOutbox _outbox;

    public SenderGrain(IDurableOutbox outbox)
    {
        _outbox = outbox;
    }

    public async Task SendSimpleMessage(GrainId target, SimpleMessage message)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(target, "handle")
            .WithBody(message)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public async Task SendComplexMessage(GrainId target, ComplexMessage message)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(target, "handle")
            .WithBody(message)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public async Task SendTypeMismatchMessage(GrainId target, object wrongTypeMessage)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(target, "handle")
            .WithBody(wrongTypeMessage)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public async Task SendMessageWithBadContext(GrainId target, SimpleMessage message, string contextKey, object contextValue)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(target, "handle")
            .WithBody(message)
            .WithContextValue(contextKey, contextValue)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }
}

/// <summary>
/// Grain that expects int but receives string, detecting type mismatch.
/// </summary>
public class TypeMismatchHandlerGrain : DurableGrain, ITypeMismatchHandlerGrain
{
    private readonly IDurableInbox _inbox;
    private bool _handledSuccessfully;
    private bool _typeMismatchDetected;

    public TypeMismatchHandlerGrain(IDurableInbox inbox)
    {
        _inbox = inbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("handle", new TypeMismatchHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<bool> GetHandledSuccessfully() => Task.FromResult(_handledSuccessfully);
    public Task<bool> GetTypeMismatchDetected() => Task.FromResult(_typeMismatchDetected);
    public Task<bool> Ping() => Task.FromResult(true);

    private class TypeMismatchHandler : IInboxHandler
    {
        private readonly TypeMismatchHandlerGrain _grain;

        public TypeMismatchHandler(TypeMismatchHandlerGrain grain)
        {
            _grain = grain;
        }

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Try to get as int (handler expects int)
            if (context.Envelope.Data.TryGetBody<int>(out var intValue))
            {
                _grain._handledSuccessfully = true;
            }
            else
            {
                // Type mismatch detected - this is expected
                _grain._typeMismatchDetected = true;
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Grain that handles context value type mismatch gracefully.
/// </summary>
public class ContextMismatchHandlerGrain : DurableGrain, IContextMismatchHandlerGrain
{
    private readonly IDurableInbox _inbox;
    private bool _bodyReceived;
    private bool _contextMismatchDetected;
    private string? _receivedValue;

    public ContextMismatchHandlerGrain(IDurableInbox inbox)
    {
        _inbox = inbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("handle", new ContextMismatchHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<bool> GetBodyReceived() => Task.FromResult(_bodyReceived);
    public Task<bool> GetContextMismatchDetected() => Task.FromResult(_contextMismatchDetected);
    public Task<string?> GetReceivedValue() => Task.FromResult(_receivedValue);

    private class ContextMismatchHandler : IInboxHandler<SimpleMessage>
    {
        private readonly ContextMismatchHandlerGrain _grain;

        public ContextMismatchHandler(ContextMismatchHandlerGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(SimpleMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Body should deserialize successfully
            _grain._bodyReceived = true;
            _grain._receivedValue = message.Value;

            // Try to get context value as int (but it's actually a string)
            if (!context.Envelope.Data.TryGetContextValue<int>("userId", out var userId))
            {
                _grain._contextMismatchDetected = true;
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Grain that handles unavailable type by skipping bad messages.
/// </summary>
public class UnavailableTypeHandlerGrain : DurableGrain, IUnavailableTypeHandlerGrain
{
    private readonly IDurableInbox _inbox;
    private int _failedDeserializationCount;
    private int _successfulMessageCount;

    public UnavailableTypeHandlerGrain(IDurableInbox inbox)
    {
        _inbox = inbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("handle", new UnavailableTypeHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetFailedDeserializationCount() => Task.FromResult(_failedDeserializationCount);
    public Task<int> GetSuccessfulMessageCount() => Task.FromResult(_successfulMessageCount);

    private class UnavailableTypeHandler : IInboxHandler
    {
        private readonly UnavailableTypeHandlerGrain _grain;

        public UnavailableTypeHandler(UnavailableTypeHandlerGrain grain)
        {
            _grain = grain;
        }

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Try SimpleMessage first
            if (context.Envelope.Data.TryGetBody<SimpleMessage>(out var simpleMsg))
            {
                _grain._successfulMessageCount++;
            }
            // Try ComplexMessage (which may fail if type changed)
            else if (context.Envelope.Data.TryGetBody<ComplexMessage>(out var complexMsg))
            {
                _grain._successfulMessageCount++;
            }
            else
            {
                // Can't deserialize - count as failure and skip
                _grain._failedDeserializationCount++;
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Grain that uses fallback logic when deserialization fails.
/// </summary>
public class FallbackHandlerGrain : DurableGrain, IFallbackHandlerGrain
{
    private readonly IDurableInbox _inbox;
    private bool _usedFallback;
    private bool _rawBytesReceived;

    public FallbackHandlerGrain(IDurableInbox inbox)
    {
        _inbox = inbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("handle", new FallbackHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<bool> GetUsedFallback() => Task.FromResult(_usedFallback);
    public Task<bool> GetRawBytesReceived() => Task.FromResult(_rawBytesReceived);

    private class FallbackHandler : IInboxHandler
    {
        private readonly FallbackHandlerGrain _grain;

        public FallbackHandler(FallbackHandlerGrain grain)
        {
            _grain = grain;
        }

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Try to deserialize as expected type
            if (context.Envelope.Data.TryGetBody<int>(out var value))
            {
                // Success - process normally
            }
            else
            {
                // Fallback: use raw bytes
                _grain._usedFallback = true;
                var rawBytes = context.Envelope.Data.GetBodyBytes();
                _grain._rawBytesReceived = rawBytes.Length > 0;
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Grain that processes mixed valid and invalid messages.
/// </summary>
public class MixedMessageHandlerGrain : DurableGrain, IMixedMessageHandlerGrain
{
    private readonly IDurableInbox _inbox;
    private int _validMessageCount;
    private int _invalidMessageCount;
    private readonly List<string> _processedValues = new();

    public MixedMessageHandlerGrain(IDurableInbox inbox)
    {
        _inbox = inbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("handle", new MixedMessageHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetValidMessageCount() => Task.FromResult(_validMessageCount);
    public Task<int> GetInvalidMessageCount() => Task.FromResult(_invalidMessageCount);
    public Task<List<string>> GetProcessedValues() => Task.FromResult(new List<string>(_processedValues));

    private class MixedMessageHandler : IInboxHandler
    {
        private readonly MixedMessageHandlerGrain _grain;

        public MixedMessageHandler(MixedMessageHandlerGrain grain)
        {
            _grain = grain;
        }

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            if (context.Envelope.Data.TryGetBody<SimpleMessage>(out var message) && message is not null)
            {
                _grain._validMessageCount++;
                _grain._processedValues.Add(message.Value);
            }
            else
            {
                _grain._invalidMessageCount++;
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Grain that survives deserialization failures and reactivation.
/// </summary>
public class SurvivorGrain : DurableGrain, ISurvivorGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly IDurableInbox _inbox;
    private readonly IDurableValue<string?> _lastReceivedValue;

    public SurvivorGrain(
        IDurableInbox inbox,
        [FromKeyedServices("lastReceivedValue")] IDurableValue<string?> lastReceivedValue)
    {
        _inbox = inbox;
        _lastReceivedValue = lastReceivedValue;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("handle", new SurvivorHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
    public Task<string?> GetLastReceivedValue() => Task.FromResult(_lastReceivedValue.Value);

    private class SurvivorHandler : IInboxHandler
    {
        private readonly SurvivorGrain _grain;

        public SurvivorHandler(SurvivorGrain grain)
        {
            _grain = grain;
        }

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            if (context.Envelope.Data.TryGetBody<SimpleMessage>(out var message) && message is not null)
            {
                _grain._lastReceivedValue.Value = message.Value;
                await _grain.WriteStateAsync();
            }
            // Silently ignore messages that can't be deserialized
        }
    }
}
