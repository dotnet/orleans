using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Integration tests for error propagation in durable messaging.
/// Tests verify that errors are properly sent back to requesters via ReplyTo address,
/// with correct error codes, retriability flags, and exception details.
/// </summary>
[TestCategory("Functional"), TestCategory("Journaling")]
public class DurableErrorPropagationTests : IClassFixture<DurableErrorPropagationTests.Fixture>
{
    private readonly Fixture _fixture;

    public DurableErrorPropagationTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests that validation errors are sent back to ReplyTo address with correct error code.
    /// Verifies SendError(string errorCode, string message, bool isRetriable) method.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_ValidationError_SendsErrorResponseToReplyTo()
    {
        // Arrange
        var requesterGrain = _fixture.Client.GetGrain<IErrorRequesterGrain>(Guid.NewGuid());
        var processorGrain = _fixture.Client.GetGrain<IErrorProcessorGrain>(Guid.NewGuid());

        // Act - Send request that will trigger validation error
        await requesterGrain.SendRequest(processorGrain.GetGrainId(), "validation", new ErrorTestRequest { Data = "" });

        // Wait for error response to be received
        var errorResponse = await TestHelpers.WaitForNullableValueAsync(
            async () => await requesterGrain.GetReceivedError(),
            message: "Error response was not received");

        // Assert
        Assert.NotNull(errorResponse);
        Assert.Equal(StandardErrorCodes.ValidationFailed, errorResponse!.Value.ErrorCode);
        Assert.Contains("Data cannot be empty", errorResponse!.Value.Message);
        Assert.False(errorResponse!.Value.IsRetriable);
    }

    /// <summary>
    /// Tests that transient errors are sent back with IsRetriable = true.
    /// Verifies that error responses correctly indicate retriability.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_TransientError_SendsRetriableErrorResponse()
    {
        // Arrange
        var requesterGrain = _fixture.Client.GetGrain<IErrorRequesterGrain>(Guid.NewGuid());
        var processorGrain = _fixture.Client.GetGrain<IErrorProcessorGrain>(Guid.NewGuid());

        // Act - Send request that will trigger transient error
        await requesterGrain.SendRequest(processorGrain.GetGrainId(), "transient", new ErrorTestRequest { Data = "test" });

        // Wait for error response to be received
        var errorResponse = await TestHelpers.WaitForNullableValueAsync(
            async () => await requesterGrain.GetReceivedError(),
            message: "Transient error response was not received");

        // Assert
        Assert.NotNull(errorResponse);
        Assert.Equal(StandardErrorCodes.TransientError, errorResponse!.Value.ErrorCode);
        Assert.Contains("temporary failure", errorResponse!.Value.Message);
        Assert.True(errorResponse!.Value.IsRetriable);
    }

    /// <summary>
    /// Tests that handler exceptions are converted to error responses using SendError(Exception).
    /// Verifies exception details are included in error response.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_HandlerException_SendsErrorWithExceptionDetails()
    {
        // Arrange
        var requesterGrain = _fixture.Client.GetGrain<IErrorRequesterGrain>(Guid.NewGuid());
        var processorGrain = _fixture.Client.GetGrain<IErrorProcessorGrain>(Guid.NewGuid());

        // Act - Send request that will throw exception
        await requesterGrain.SendRequest(processorGrain.GetGrainId(), "exception", new ErrorTestRequest { Data = "test" });

        // Wait for error response to be received
        var errorResponse = await TestHelpers.WaitForNullableValueAsync(
            async () => await requesterGrain.GetReceivedError(),
            message: "Exception error response was not received");

        // Assert
        Assert.NotNull(errorResponse);
        Assert.Equal("INVALID_OPERATION", errorResponse!.Value.ErrorCode); // Exception type converted to error code
        Assert.Contains("Handler exception test", errorResponse!.Value.Message);
        Assert.NotNull(errorResponse!.Value.ExceptionDetails);
        Assert.Contains("InvalidOperationException", errorResponse!.Value.ExceptionDetails);
        Assert.False(errorResponse!.Value.IsRetriable);
    }

    /// <summary>
    /// Tests that permanent errors (unauthorized) are sent with IsRetriable = false.
    /// Verifies custom error codes work correctly.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_UnauthorizedError_SendsNonRetriableErrorResponse()
    {
        // Arrange
        var requesterGrain = _fixture.Client.GetGrain<IErrorRequesterGrain>(Guid.NewGuid());
        var processorGrain = _fixture.Client.GetGrain<IErrorProcessorGrain>(Guid.NewGuid());

        // Act - Send request that will trigger unauthorized error
        await requesterGrain.SendRequest(processorGrain.GetGrainId(), "unauthorized", new ErrorTestRequest { Data = "test" });

        // Wait for error response to be received
        var errorResponse = await TestHelpers.WaitForNullableValueAsync(
            async () => await requesterGrain.GetReceivedError(),
            message: "Unauthorized error response was not received");

        // Assert
        Assert.NotNull(errorResponse);
        Assert.Equal(StandardErrorCodes.Unauthorized, errorResponse!.Value.ErrorCode);
        Assert.Contains("not authorized", errorResponse!.Value.Message);
        Assert.False(errorResponse!.Value.IsRetriable);
    }

    /// <summary>
    /// Tests that errors preserve correlation keys from original request.
    /// Verifies hierarchical correlation is maintained in error responses.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_WithCorrelationKey_PreservesCorrelationInErrorResponse()
    {
        // Arrange
        var requesterGrain = _fixture.Client.GetGrain<IErrorRequesterGrain>(Guid.NewGuid());
        var processorGrain = _fixture.Client.GetGrain<IErrorProcessorGrain>(Guid.NewGuid());
        var correlationKey = HierarchicalKey.Create("error-test-123");

        // Act - Send request with correlation key that will trigger error
        await requesterGrain.SendRequestWithCorrelation(processorGrain.GetGrainId(), "validation", new ErrorTestRequest { Data = "" }, correlationKey);

        // Wait for error response to be received
        await TestHelpers.WaitUntilAsync(
            async () => (await requesterGrain.GetReceivedError()) != null,
            message: "Correlation error response was not received");

        // Assert
        var errorResponse = await requesterGrain.GetReceivedError();
        var receivedCorrelation = await requesterGrain.GetReceivedCorrelationKey();
        Assert.NotNull(errorResponse);
        Assert.Equal(correlationKey, receivedCorrelation);
    }

    /// <summary>
    /// Tests that SendError with no ReplyTo address does not throw (safe no-op).
    /// Verifies one-way messages don't cause errors.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_NoReplyTo_DoesNotThrow()
    {
        // Arrange
        var processorGrain = _fixture.Client.GetGrain<IErrorProcessorGrain>(Guid.NewGuid());

        // Act & Assert - Should not throw even though validation fails
        await processorGrain.ProcessOneWayMessage(new ErrorTestRequest { Data = "" });

        // Wait for the message to be processed
        await TestHelpers.WaitUntilAsync(
            async () => (await processorGrain.GetProcessedCount()) >= 1,
            message: "One-way message was not processed");

        // Verify the message was processed (grain should still be alive and responsive)
        var count = await processorGrain.GetProcessedCount();
        Assert.Equal(1, count);
    }

    public class Fixture : IntegrationTestFixture
    {
    }
}

// ============================================================================
// Test Message Types (scoped to this test file to avoid conflicts)
// ============================================================================

[GenerateSerializer]
public record ErrorTestRequest
{
    [Id(0)] public required string Data { get; init; }
}

[GenerateSerializer]
public record ErrorTestResponse
{
    [Id(0)] public required string Result { get; init; }
}

// ============================================================================
// Grain Interfaces
// ============================================================================

public interface IErrorRequesterGrain : IGrainWithGuidKey
{
    Task SendRequest(GrainId processorId, string errorType, ErrorTestRequest request);
    Task SendRequestWithCorrelation(GrainId processorId, string errorType, ErrorTestRequest request, HierarchicalKey correlationKey);
    Task<DurableErrorResponse?> GetReceivedError();
    Task<HierarchicalKey?> GetReceivedCorrelationKey();
}

public interface IErrorProcessorGrain : IGrainWithGuidKey
{
    Task ProcessOneWayMessage(ErrorTestRequest request);
    Task<int> GetProcessedCount();
}

// ============================================================================
// Grain Implementations
// ============================================================================

/// <summary>
/// Requester grain that sends requests and captures error responses.
/// </summary>
public class ErrorRequesterGrain : DurableGrain, IErrorRequesterGrain
{
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private DurableErrorResponse? _receivedError;
    private HierarchicalKey? _receivedCorrelationKey;

    public ErrorRequesterGrain(IDurableInbox inbox, IDurableOutbox outbox)
    {
        _inbox = inbox;
        _outbox = outbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register handler for error responses (route "test/reply" for errors)
        _inbox.RegisterHandler("test/reply", new ErrorResponseHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SendRequest(GrainId processorId, string errorType, ErrorTestRequest request)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(processorId, $"test/{errorType}")
            .WithBody(request)
            .WithReplyTo(this.GetGrainId())
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public async Task SendRequestWithCorrelation(GrainId processorId, string errorType, ErrorTestRequest request, HierarchicalKey correlationKey)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(processorId, $"test/{errorType}")
            .WithBody(request)
            .WithReplyTo(this.GetGrainId())
            .WithCorrelationKey(correlationKey)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public Task<DurableErrorResponse?> GetReceivedError() => Task.FromResult(_receivedError);
    public Task<HierarchicalKey?> GetReceivedCorrelationKey() => Task.FromResult(_receivedCorrelationKey);

    private class ErrorResponseHandler : IInboxHandler<DurableErrorResponse>
    {
        private readonly ErrorRequesterGrain _grain;

        public ErrorResponseHandler(ErrorRequesterGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(DurableErrorResponse message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._receivedError = message;
            _grain._receivedCorrelationKey = context.Envelope.CorrelationKey;
            await _grain.WriteStateAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Processor grain that triggers various error scenarios.
/// </summary>
public class ErrorProcessorGrain : DurableGrain, IErrorProcessorGrain
{
    private readonly IDurableInbox _inbox;
    private int _processedCount;

    public ErrorProcessorGrain(IDurableInbox inbox)
    {
        _inbox = inbox;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register prefix handler for all "test/" routes
        _inbox.RegisterHandler(new TestPrefixHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task ProcessOneWayMessage(ErrorTestRequest request)
    {
        // Manually send a one-way message (no ReplyTo)
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(this.GetGrainId(), "test/validation")
            .WithBody(request)
            // No WithReplyTo - this is a one-way message
            .Build();

        // Deliver directly to self via inbox extension
        var inboxExtension = this.AsReference<IDurableInboxExtension>();
        using (RequestContext.AllowCallChainReentrancy())
        {
            await inboxExtension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);
        }
    }

    public Task<int> GetProcessedCount() => Task.FromResult(_processedCount);

    private class TestPrefixHandler : RoutePrefixHandler
    {
        private readonly ErrorProcessorGrain _grain;

        public TestPrefixHandler(ErrorProcessorGrain grain) : base("test/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._processedCount++;

            // Deserialize request
            if (!context.Envelope.Data.TryGetBody<ErrorTestRequest>(out var request) || request is null)
            {
                context.SendError(StandardErrorCodes.DeserializationFailed, "Failed to deserialize request", isRetriable: false);
                await _grain.WriteStateAsync(cancellationToken);
                return;
            }

            // Get operation from route suffix (e.g., "test/validation" → "validation")
            var operation = GetRouteSuffix(context.Envelope.RouteKey);

            switch (operation)
            {
                case "validation":
                    // Validation error - not retriable
                    if (string.IsNullOrEmpty(request.Data))
                    {
                        context.SendError(StandardErrorCodes.ValidationFailed, "Data cannot be empty", isRetriable: false);
                    }
                    else
                    {
                        // Success case - send response
                        if (context.Envelope.ReplyTo is { } replyTo)
                        {
                            var responseBuilder = context.CreateEnvelope()
                                .To(replyTo, "test/reply")
                                .WithBody(new ErrorTestResponse { Result = $"Validated: {request.Data}" });

                            if (context.Envelope.CorrelationKey is { } correlationKey)
                            {
                                responseBuilder.WithCorrelationKey(correlationKey);
                            }

                            context.Send(responseBuilder.Build());
                        }
                    }
                    break;

                case "transient":
                    // Transient error - retriable
                    context.SendError(StandardErrorCodes.TransientError, "Service experiencing temporary failure", isRetriable: true);
                    break;

                case "exception":
                    // Handler exception - use exception-based SendError
                    try
                    {
                        throw new InvalidOperationException("Handler exception test");
                    }
                    catch (Exception ex)
                    {
                        context.SendError(ex, isRetriable: false);
                    }
                    break;

                case "unauthorized":
                    // Permanent error - not retriable
                    context.SendError(StandardErrorCodes.Unauthorized, "User not authorized to perform this operation", isRetriable: false);
                    break;

                default:
                    context.SendError(StandardErrorCodes.HandlerNotFound, $"Unknown operation: {operation}", isRetriable: false);
                    break;
            }

            await _grain.WriteStateAsync(cancellationToken);
        }
    }
}
