using System;
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for DurableEnvelopeData, which uses the MigrationContext pattern for deferred serialization
/// of message body and request context with per-key access.
/// </summary>
[TestCategory("BVT")]
public class DurableEnvelopeDataTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SerializerSessionPool _sessionPool;

    public DurableEnvelopeDataTests()
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
    /// Helper method to create a DurableEnvelopeData with a body and context values.
    /// </summary>
    private DurableEnvelopeData CreateEnvelopeData<TBody>(TBody body, params (string Key, object Value)[] contextValues)
    {
        var writer = new ArcBufferWriter();
        var bodySlice = (0, 0);
        Dictionary<string, (int Offset, int Length)>? contextIndices = null;

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

            // Serialize context values
            if (contextValues.Length > 0)
            {
                contextIndices = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
                foreach (var (key, value) in contextValues)
                {
                    startOffset = writer.Length;
                    using var session = _sessionPool.GetSession();
                    var bufferWriter = Writer.Create(writer, session);
                    var valueType = value.GetType();
                    var codec = _sessionPool.CodecProvider.GetCodec(valueType);
                    codec.WriteField(ref bufferWriter, 0, valueType, value);
                    bufferWriter.Commit();
                    contextIndices[key] = (startOffset, writer.Length - startOffset);
                }
            }

            // Create the buffer slice
            var buffer = writer.ConsumeSlice(writer.Length);

            // Use reflection to set internal fields (for testing purposes)
            var data = new DurableEnvelopeData(_sessionPool);
            var bufferField = typeof(DurableEnvelopeData).GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var bodySliceField = typeof(DurableEnvelopeData).GetField("_bodySlice", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var contextIndicesField = typeof(DurableEnvelopeData).GetField("_contextIndices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            bufferField!.SetValue(data, buffer);
            bodySliceField!.SetValue(data, bodySlice);
            contextIndicesField!.SetValue(data, contextIndices);

            return data;
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void TryGetBody_ValidType_ReturnsTrue()
    {
        // Arrange
        var expectedBody = "test-message";
        var data = CreateEnvelopeData(expectedBody);

        // Act
        var success = data.TryGetBody<string>(out var actualBody);

        // Assert
        Assert.True(success);
        Assert.Equal(expectedBody, actualBody);
    }

    [Fact]
    public void TryGetBody_TypeMismatch_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("test-message");

        // Act
        var success = data.TryGetBody<int>(out var value);

        // Assert
        Assert.False(success);
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryGetBody_ComplexType_ReturnsTrue()
    {
        // Arrange
        var expectedBody = new TestMessage { Id = 123, Name = "Test" };
        var data = CreateEnvelopeData(expectedBody);

        // Act
        var success = data.TryGetBody<TestMessage>(out var actualBody);

        // Assert
        Assert.True(success);
        Assert.NotNull(actualBody);
        Assert.Equal(expectedBody.Id, actualBody.Id);
        Assert.Equal(expectedBody.Name, actualBody.Name);
    }

    [Fact]
    public void TryGetContextValue_ExistingKey_ReturnsTrue()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var success = data.TryGetContextValue<string>("key1", out var value);

        // Assert
        Assert.True(success);
        Assert.Equal("value1", value);
    }

    [Fact]
    public void TryGetContextValue_MissingKey_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var success = data.TryGetContextValue<string>("key2", out var value);

        // Assert
        Assert.False(success);
        Assert.Null(value);
    }

    [Fact]
    public void TryGetContextValue_TypeMismatch_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var success = data.TryGetContextValue<int>("key1", out var value);

        // Assert
        Assert.False(success);
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryGetContextValue_MultipleKeys_ReturnsIndependently()
    {
        // Arrange
        var data = CreateEnvelopeData("body",
            ("key1", "value1"),
            ("key2", 42),
            ("key3", true));

        // Act & Assert
        Assert.True(data.TryGetContextValue<string>("key1", out var value1));
        Assert.Equal("value1", value1);

        Assert.True(data.TryGetContextValue<int>("key2", out var value2));
        Assert.Equal(42, value2);

        Assert.True(data.TryGetContextValue<bool>("key3", out var value3));
        Assert.True(value3);
    }

    [Fact]
    public void TryGetContextValue_PartialFailureIsolation_OtherValuesAccessible()
    {
        // Arrange
        var data = CreateEnvelopeData("body",
            ("key1", "value1"),
            ("key2", 42),
            ("key3", "value3"));

        // Act - Try to get key2 as wrong type (should fail)
        var failedAccess = data.TryGetContextValue<string>("key2", out var wrongValue);

        // Assert - Failure is isolated, other keys still accessible
        Assert.False(failedAccess);
        Assert.Null(wrongValue);

        Assert.True(data.TryGetContextValue<string>("key1", out var value1));
        Assert.Equal("value1", value1);

        Assert.True(data.TryGetContextValue<string>("key3", out var value3));
        Assert.Equal("value3", value3);
    }

    [Fact]
    public void HasContextKey_ExistingKey_ReturnsTrue()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var exists = data.HasContextKey("key1");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void HasContextKey_MissingKey_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var exists = data.HasContextKey("key2");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void HasContextKey_NoContext_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("body");

        // Act
        var exists = data.HasContextKey("key1");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void ContextKeys_NoContext_ReturnsEmpty()
    {
        // Arrange
        var data = CreateEnvelopeData("body");

        // Act
        var keys = data.ContextKeys;

        // Assert
        Assert.Empty(keys);
    }

    [Fact]
    public void ContextKeys_WithContext_ReturnsAllKeys()
    {
        // Arrange
        var data = CreateEnvelopeData("body",
            ("key1", "value1"),
            ("key2", 42),
            ("key3", true));

        // Act
        var keys = new HashSet<string>(data.ContextKeys);

        // Assert
        Assert.Equal(3, keys.Count);
        Assert.Contains("key1", keys);
        Assert.Contains("key2", keys);
        Assert.Contains("key3", keys);
    }

    [Fact]
    public void GetBodyBytes_ReturnsRawBytes()
    {
        // Arrange
        var expectedBody = "test-message";
        var data = CreateEnvelopeData(expectedBody);

        // Act
        var bytes = data.GetBodyBytes();

        // Assert
        Assert.True(bytes.Length > 0);

        // Verify we can deserialize the bytes manually
        using var session = _sessionPool.GetSession();
        var reader = Reader.Create(bytes, session);
        var field = reader.ReadFieldHeader();
        var actualBody = _sessionPool.CodecProvider.GetCodec<string>().ReadValue(ref reader, field);
        Assert.Equal(expectedBody, actualBody);
    }

    [Fact]
    public void TryGetContextBytes_ExistingKey_ReturnsTrue()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var success = data.TryGetContextBytes("key1", out var bytes);

        // Assert
        Assert.True(success);
        Assert.True(bytes.Length > 0);

        // Verify we can deserialize the bytes manually
        using var session = _sessionPool.GetSession();
        var reader = Reader.Create(bytes, session);
        var field = reader.ReadFieldHeader();
        var actualValue = _sessionPool.CodecProvider.GetCodec<string>().ReadValue(ref reader, field);
        Assert.Equal("value1", actualValue);
    }

    [Fact]
    public void TryGetContextBytes_MissingKey_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        var success = data.TryGetContextBytes("key2", out var bytes);

        // Assert
        Assert.False(success);
        Assert.True(bytes.IsEmpty);
    }

    [Fact]
    public void TryGetContextBytes_NoContext_ReturnsFalse()
    {
        // Arrange
        var data = CreateEnvelopeData("body");

        // Act
        var success = data.TryGetContextBytes("key1", out var bytes);

        // Assert
        Assert.False(success);
        Assert.True(bytes.IsEmpty);
    }

    [Fact]
    public void TryGetBody_EmptyBody_ReturnsFalse()
    {
        // Arrange - Create an envelope data with no actual body serialized
        var data = new DurableEnvelopeData(_sessionPool);

        // Act
        var success = data.TryGetBody<string>(out var value);

        // Assert
        Assert.False(success);
        Assert.Null(value);
    }

    [Fact]
    public void Dispose_DisposesBuffer()
    {
        // Arrange
        var data = CreateEnvelopeData("body", ("key1", "value1"));

        // Act
        data.Dispose();

        // Assert - After dispose, accessing data should fail gracefully
        // The buffer's underlying pages should have been released
        // We can't easily verify this without access to internals,
        // but we can at least verify the Dispose doesn't throw
        Assert.True(true); // Dispose succeeded without throwing
    }

    /// <summary>
    /// Test helper class for complex body serialization tests.
    /// </summary>
    [GenerateSerializer]
    public class TestMessage
    {
        [Id(0)]
        public int Id { get; set; }

        [Id(1)]
        public string? Name { get; set; }
    }
}
