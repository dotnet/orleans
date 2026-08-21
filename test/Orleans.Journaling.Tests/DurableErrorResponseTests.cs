using System;
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for DurableErrorResponse, the standard error response message for durable inbox/outbox.
/// Tests verify serialization round-trip, property handling, and null handling.
/// </summary>
[TestCategory("BVT")]
public class DurableErrorResponseTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SerializerSessionPool _sessionPool;
    private readonly CodecProvider _codecProvider;

    public DurableErrorResponseTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        _serviceProvider = services.BuildServiceProvider();
        _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = _serviceProvider.GetRequiredService<CodecProvider>();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    /// <summary>
    /// Helper method to serialize and deserialize an error response.
    /// </summary>
    private DurableErrorResponse RoundTrip(DurableErrorResponse original)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var session = _sessionPool.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            var codec = _codecProvider.GetCodec<DurableErrorResponse>();
            codec.WriteField(ref writer, 0, typeof(DurableErrorResponse), original);
            writer.Commit();
        }

        DurableErrorResponse deserialized;
        using (var session = _sessionPool.GetSession())
        {
            var reader = Reader.Create(buffer.WrittenMemory, session);
            var field = reader.ReadFieldHeader();
            var codec = _codecProvider.GetCodec<DurableErrorResponse>();
            deserialized = codec.ReadValue(ref reader, field);
        }

        return deserialized;
    }

    [Fact]
    public void DurableErrorResponse_WithAllProperties_SerializesAndDeserializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = "TEST_ERROR",
            Message = "This is a test error message",
            ExceptionDetails = "System.InvalidOperationException: Test exception\n   at TestClass.TestMethod()",
            IsRetriable = true
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(original.ErrorCode, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.Equal(original.IsRetriable, result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithNullExceptionDetails_SerializesAndDeserializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = "VALIDATION_ERROR",
            Message = "Invalid input",
            ExceptionDetails = null,
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(original.ErrorCode, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Null(result.ExceptionDetails);
        Assert.Equal(original.IsRetriable, result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithStandardErrorCodes_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.HandlerNotFound,
            Message = "No handler registered for route",
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.HandlerNotFound, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.IsRetriable, result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithEmptyStrings_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = string.Empty,
            Message = string.Empty,
            ExceptionDetails = string.Empty,
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(string.Empty, result.ErrorCode);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal(string.Empty, result.ExceptionDetails);
        Assert.Equal(original.IsRetriable, result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithLongStrings_SerializesCorrectly()
    {
        // Arrange
        var longString = new string('x', 10000);
        var original = new DurableErrorResponse
        {
            ErrorCode = "LONG_STRING_TEST",
            Message = longString,
            ExceptionDetails = longString,
            IsRetriable = true
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(original.ErrorCode, result.ErrorCode);
        Assert.Equal(longString, result.Message);
        Assert.Equal(longString, result.ExceptionDetails);
        Assert.Equal(original.IsRetriable, result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithSpecialCharacters_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = "SPECIAL_CHARS",
            Message = "Error with special chars: \n\r\t\\ \"quotes\" 中文",
            ExceptionDetails = "Exception: \n  at Method(String arg = \"test\")",
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(original.ErrorCode, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.Equal(original.IsRetriable, result.IsRetriable);
    }

    [Fact]
    public void StandardErrorCodes_HasExpectedValues()
    {
        // Assert all standard error codes are defined and have expected values
        Assert.Equal("HANDLER_NOT_FOUND", StandardErrorCodes.HandlerNotFound);
        Assert.Equal("DESERIALIZATION_FAILED", StandardErrorCodes.DeserializationFailed);
        Assert.Equal("HANDLER_EXCEPTION", StandardErrorCodes.HandlerException);
        Assert.Equal("CANCELLED", StandardErrorCodes.Cancelled);
        Assert.Equal("TIMEOUT", StandardErrorCodes.Timeout);
        Assert.Equal("TRANSIENT_ERROR", StandardErrorCodes.TransientError);
        Assert.Equal("VALIDATION_FAILED", StandardErrorCodes.ValidationFailed);
        Assert.Equal("UNAUTHORIZED", StandardErrorCodes.Unauthorized);
    }

    [Fact]
    public void DurableErrorResponse_IsRetriableDefaultValue_IsFalse()
    {
        // Arrange & Act
        var response = new DurableErrorResponse
        {
            ErrorCode = "TEST",
            Message = "Test"
        };

        // Assert
        Assert.False(response.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithDeserializationFailedCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.DeserializationFailed,
            Message = "Failed to deserialize message body into expected type",
            ExceptionDetails = "InvalidCastException: Unable to cast object of type 'OrderRequest' to type 'PaymentRequest'",
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.DeserializationFailed, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.False(result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithHandlerExceptionCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.HandlerException,
            Message = "Unhandled exception in message handler",
            ExceptionDetails = "NullReferenceException: Object reference not set to an instance of an object\n   at OrderHandler.HandleAsync()",
            IsRetriable = true
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.HandlerException, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.True(result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithCancelledCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.Cancelled,
            Message = "Operation was cancelled",
            ExceptionDetails = "OperationCanceledException: The operation was canceled.",
            IsRetriable = true
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.Cancelled, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.True(result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithTimeoutCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.Timeout,
            Message = "Operation exceeded timeout threshold",
            ExceptionDetails = "TimeoutException: The operation has timed out after 30 seconds",
            IsRetriable = true
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.Timeout, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.True(result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithTransientErrorCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.TransientError,
            Message = "Temporary network connectivity issue",
            ExceptionDetails = "HttpRequestException: No such host is known",
            IsRetriable = true
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.TransientError, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(original.ExceptionDetails, result.ExceptionDetails);
        Assert.True(result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithValidationFailedCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.ValidationFailed,
            Message = "Message validation failed: Amount must be greater than zero",
            ExceptionDetails = null,
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Null(result.ExceptionDetails);
        Assert.False(result.IsRetriable);
    }

    [Fact]
    public void DurableErrorResponse_WithUnauthorizedCode_SerializesCorrectly()
    {
        // Arrange
        var original = new DurableErrorResponse
        {
            ErrorCode = StandardErrorCodes.Unauthorized,
            Message = "Insufficient permissions to perform this operation",
            ExceptionDetails = null,
            IsRetriable = false
        };

        // Act
        var result = RoundTrip(original);

        // Assert
        Assert.Equal(StandardErrorCodes.Unauthorized, result.ErrorCode);
        Assert.Equal(original.Message, result.Message);
        Assert.Null(result.ExceptionDetails);
        Assert.False(result.IsRetriable);
    }
}
