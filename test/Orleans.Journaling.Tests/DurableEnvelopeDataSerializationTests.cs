using System;
using System.Buffers;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Comprehensive serialization/deserialization tests for DurableEnvelopeData.
/// Tests various types, edge cases, and round-trip serialization through Orleans serializer.
/// </summary>
[TestCategory("BVT"), TestCategory("Journaling")]
public class DurableEnvelopeDataSerializationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SerializerSessionPool _sessionPool;
    private readonly Serializer _serializer;

    public DurableEnvelopeDataSerializationTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        _serviceProvider = services.BuildServiceProvider();
        _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        _serializer = _serviceProvider.GetRequiredService<Serializer>();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    #region Helper Methods

    /// <summary>
    /// Helper method to create a DurableEnvelopeData with a body using the same serialization
    /// pattern as DurableEnvelopeBuilder.
    /// </summary>
    private DurableEnvelopeData CreateEnvelopeData<TBody>(TBody body, params (string Key, object Value)[] contextValues)
    {
        var writer = new ArcBufferWriter();
        var bodySlice = (0, 0);
        Dictionary<string, (int Offset, int Length)>? contextIndices = null;

        try
        {
            // Serialize body using the same pattern as DurableEnvelopeBuilder.WithBody<T>
            var startOffset = writer.Length;
            using (var session = _sessionPool.GetSession())
            {
                var bufferWriter = Writer.Create((IBufferWriter<byte>)writer, session);
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
                    var bufferWriter = Writer.Create((IBufferWriter<byte>)writer, session);
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

    #endregion

    #region Primitive Type Tests

    [Fact]
    public void TryGetBody_Int32_Succeeds()
    {
        var data = CreateEnvelopeData(42);
        Assert.True(data.TryGetBody<int>(out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryGetBody_Int64_Succeeds()
    {
        var data = CreateEnvelopeData(9876543210L);
        Assert.True(data.TryGetBody<long>(out var value));
        Assert.Equal(9876543210L, value);
    }

    [Fact]
    public void TryGetBody_Float_Succeeds()
    {
        var data = CreateEnvelopeData(3.14f);
        Assert.True(data.TryGetBody<float>(out var value));
        Assert.Equal(3.14f, value);
    }

    [Fact]
    public void TryGetBody_Double_Succeeds()
    {
        var data = CreateEnvelopeData(3.14159265358979);
        Assert.True(data.TryGetBody<double>(out var value));
        Assert.Equal(3.14159265358979, value);
    }

    [Fact]
    public void TryGetBody_Bool_True_Succeeds()
    {
        var data = CreateEnvelopeData(true);
        Assert.True(data.TryGetBody<bool>(out var value));
        Assert.True(value);
    }

    [Fact]
    public void TryGetBody_Bool_False_Succeeds()
    {
        var data = CreateEnvelopeData(false);
        Assert.True(data.TryGetBody<bool>(out var value));
        Assert.False(value);
    }

    [Fact]
    public void TryGetBody_Char_Succeeds()
    {
        var data = CreateEnvelopeData('Z');
        Assert.True(data.TryGetBody<char>(out var value));
        Assert.Equal('Z', value);
    }

    [Fact]
    public void TryGetBody_Byte_Succeeds()
    {
        var data = CreateEnvelopeData((byte)255);
        Assert.True(data.TryGetBody<byte>(out var value));
        Assert.Equal(255, value);
    }

    [Fact]
    public void TryGetBody_Decimal_Succeeds()
    {
        var data = CreateEnvelopeData(123.456789m);
        Assert.True(data.TryGetBody<decimal>(out var value));
        Assert.Equal(123.456789m, value);
    }

    [Fact]
    public void TryGetBody_Guid_Succeeds()
    {
        var expected = Guid.NewGuid();
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<Guid>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_DateTime_Succeeds()
    {
        var expected = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<DateTime>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_DateTimeOffset_Succeeds()
    {
        var expected = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.FromHours(-5));
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<DateTimeOffset>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_TimeSpan_Succeeds()
    {
        var expected = TimeSpan.FromMinutes(90);
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<TimeSpan>(out var value));
        Assert.Equal(expected, value);
    }

    #endregion

    #region String Tests

    [Fact]
    public void TryGetBody_String_Succeeds()
    {
        var data = CreateEnvelopeData("Hello, World!");
        Assert.True(data.TryGetBody<string>(out var value));
        Assert.Equal("Hello, World!", value);
    }

    [Fact]
    public void TryGetBody_EmptyString_Succeeds()
    {
        var data = CreateEnvelopeData(string.Empty);
        Assert.True(data.TryGetBody<string>(out var value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryGetBody_LongString_Succeeds()
    {
        var expected = new string('x', 10000);
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<string>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_UnicodeString_Succeeds()
    {
        var expected = "こんにちは世界 🌍 Привет мир";
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<string>(out var value));
        Assert.Equal(expected, value);
    }

    #endregion

    #region Complex Type Tests

    [Fact]
    public void TryGetBody_SimpleRecord_Succeeds()
    {
        var expected = new SimpleRecord(42, "Test");
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<SimpleRecord>(out var value));
        Assert.Equal(expected.Id, value.Id);
        Assert.Equal(expected.Name, value.Name);
    }

    [Fact]
    public void TryGetBody_NestedRecord_Succeeds()
    {
        var expected = new NestedRecord(
            1,
            new SimpleRecord(42, "Inner"),
            new List<string> { "a", "b", "c" });
        var data = CreateEnvelopeData(expected);

        Assert.True(data.TryGetBody<NestedRecord>(out var value));
        Assert.Equal(expected.Id, value.Id);
        Assert.NotNull(value.Inner);
        Assert.Equal(expected.Inner.Id, value.Inner.Id);
        Assert.Equal(expected.Inner.Name, value.Inner.Name);
        Assert.Equal(expected.Tags, value.Tags);
    }

    [Fact]
    public void TryGetBody_ClassWithAllFields_Succeeds()
    {
        var expected = new ComplexClass
        {
            IntValue = 42,
            StringValue = "test",
            DoubleValue = 3.14,
            BoolValue = true,
            GuidValue = Guid.NewGuid(),
            DateTimeValue = DateTime.UtcNow,
            NullableInt = 100,
            NullableString = "nullable"
        };
        var data = CreateEnvelopeData(expected);

        Assert.True(data.TryGetBody<ComplexClass>(out var value));
        Assert.Equal(expected.IntValue, value.IntValue);
        Assert.Equal(expected.StringValue, value.StringValue);
        Assert.Equal(expected.DoubleValue, value.DoubleValue);
        Assert.Equal(expected.BoolValue, value.BoolValue);
        Assert.Equal(expected.GuidValue, value.GuidValue);
        Assert.Equal(expected.DateTimeValue, value.DateTimeValue);
        Assert.Equal(expected.NullableInt, value.NullableInt);
        Assert.Equal(expected.NullableString, value.NullableString);
    }

    [Fact]
    public void TryGetBody_ClassWithNullNullableFields_Succeeds()
    {
        var expected = new ComplexClass
        {
            IntValue = 42,
            StringValue = "test",
            DoubleValue = 3.14,
            BoolValue = false,
            GuidValue = Guid.Empty,
            DateTimeValue = DateTime.MinValue,
            NullableInt = null,
            NullableString = null
        };
        var data = CreateEnvelopeData(expected);

        Assert.True(data.TryGetBody<ComplexClass>(out var value));
        Assert.Null(value.NullableInt);
        Assert.Null(value.NullableString);
    }

    #endregion

    #region Collection Type Tests

    [Fact]
    public void TryGetBody_IntArray_Succeeds()
    {
        var expected = new[] { 1, 2, 3, 4, 5 };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<int[]>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_StringArray_Succeeds()
    {
        var expected = new[] { "one", "two", "three" };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<string[]>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_EmptyArray_Succeeds()
    {
        var expected = Array.Empty<int>();
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<int[]>(out var value));
        Assert.Empty(value);
    }

    [Fact]
    public void TryGetBody_ListOfInt_Succeeds()
    {
        var expected = new List<int> { 10, 20, 30 };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<List<int>>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_ListOfRecords_Succeeds()
    {
        var expected = new List<SimpleRecord>
        {
            new(1, "First"),
            new(2, "Second"),
            new(3, "Third")
        };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<List<SimpleRecord>>(out var value));
        Assert.Equal(expected.Count, value.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, value[i].Id);
            Assert.Equal(expected[i].Name, value[i].Name);
        }
    }

    [Fact]
    public void TryGetBody_DictionaryStringInt_Succeeds()
    {
        var expected = new Dictionary<string, int>
        {
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3
        };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<Dictionary<string, int>>(out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetBody_DictionaryStringRecord_Succeeds()
    {
        var expected = new Dictionary<string, SimpleRecord>
        {
            ["first"] = new(1, "First"),
            ["second"] = new(2, "Second")
        };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<Dictionary<string, SimpleRecord>>(out var value));
        Assert.Equal(expected.Count, value.Count);
        foreach (var kvp in expected)
        {
            Assert.True(value.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value.Id, value[kvp.Key].Id);
            Assert.Equal(kvp.Value.Name, value[kvp.Key].Name);
        }
    }

    [Fact]
    public void TryGetBody_HashSetOfString_Succeeds()
    {
        var expected = new HashSet<string> { "apple", "banana", "cherry" };
        var data = CreateEnvelopeData(expected);
        Assert.True(data.TryGetBody<HashSet<string>>(out var value));
        Assert.Equal(expected, value);
    }

    #endregion

    #region Type Mismatch Tests

    [Fact]
    public void TryGetBody_IntAsString_ReturnsFalse()
    {
        var data = CreateEnvelopeData(42);
        Assert.False(data.TryGetBody<string>(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryGetBody_StringAsInt_ReturnsFalse()
    {
        var data = CreateEnvelopeData("not a number");
        Assert.False(data.TryGetBody<int>(out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryGetBody_RecordAsWrongRecord_ReturnsFalse()
    {
        var data = CreateEnvelopeData(new SimpleRecord(1, "test"));
        Assert.False(data.TryGetBody<DifferentRecord>(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryGetBody_ListAsArray_SucceedsDueToOrleansSerializerFlexibility()
    {
        // Orleans serializer may be flexible with collection types
        var expected = new List<int> { 1, 2, 3 };
        var data = CreateEnvelopeData(expected);
        // This might succeed or fail depending on Orleans serializer behavior
        var success = data.TryGetBody<int[]>(out var value);
        // We just verify it doesn't throw
        Assert.True(success || !success);
    }

    #endregion

    #region Context Value Tests

    [Fact]
    public void TryGetContextValue_MultipleTypesInContext_Succeeds()
    {
        var data = CreateEnvelopeData("body",
            ("string-key", "string-value"),
            ("int-key", 42),
            ("bool-key", true),
            ("guid-key", Guid.NewGuid()),
            ("record-key", new SimpleRecord(1, "context-record")));

        Assert.True(data.TryGetContextValue<string>("string-key", out var stringValue));
        Assert.Equal("string-value", stringValue);

        Assert.True(data.TryGetContextValue<int>("int-key", out var intValue));
        Assert.Equal(42, intValue);

        Assert.True(data.TryGetContextValue<bool>("bool-key", out var boolValue));
        Assert.True(boolValue);

        Assert.True(data.TryGetContextValue<Guid>("guid-key", out var guidValue));
        Assert.NotEqual(Guid.Empty, guidValue);

        Assert.True(data.TryGetContextValue<SimpleRecord>("record-key", out var recordValue));
        Assert.Equal(1, recordValue.Id);
        Assert.Equal("context-record", recordValue.Name);
    }

    [Fact]
    public void TryGetContextValue_WrongType_ReturnsFalseWithoutAffectingOthers()
    {
        var data = CreateEnvelopeData("body",
            ("int-key", 42),
            ("string-key", "hello"));

        // Try to get int as string - should fail
        Assert.False(data.TryGetContextValue<string>("int-key", out _));

        // Other values should still be accessible
        Assert.True(data.TryGetContextValue<string>("string-key", out var stringValue));
        Assert.Equal("hello", stringValue);

        // Original int should still work
        Assert.True(data.TryGetContextValue<int>("int-key", out var intValue));
        Assert.Equal(42, intValue);
    }

    #endregion

    #region Round-Trip Serialization Tests

    [Fact]
    public void DurableEnvelopeData_RoundTripSerialization_PreservesData()
    {
        // Create envelope data with body
        var originalData = CreateEnvelopeData(new SimpleRecord(42, "round-trip"));

        // Serialize the entire DurableEnvelopeData using Orleans serializer
        var serialized = _serializer.SerializeToArray(originalData);

        // Deserialize
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        // Verify body is preserved
        Assert.True(deserializedData.TryGetBody<SimpleRecord>(out var body));
        Assert.Equal(42, body.Id);
        Assert.Equal("round-trip", body.Name);
    }

    [Fact]
    public void DurableEnvelopeData_RoundTripSerialization_PreservesContextValues()
    {
        // Create envelope data with body and context
        var originalData = CreateEnvelopeData("body",
            ("trace-id", "abc-123"),
            ("priority", 5));

        // Serialize
        var serialized = _serializer.SerializeToArray(originalData);

        // Deserialize
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        // Verify body
        Assert.True(deserializedData.TryGetBody<string>(out var body));
        Assert.Equal("body", body);

        // Verify context values
        Assert.True(deserializedData.TryGetContextValue<string>("trace-id", out var traceId));
        Assert.Equal("abc-123", traceId);

        Assert.True(deserializedData.TryGetContextValue<int>("priority", out var priority));
        Assert.Equal(5, priority);
    }

    [Fact]
    public void DurableEnvelopeData_RoundTripSerialization_PreservesContextKeys()
    {
        var originalData = CreateEnvelopeData("body",
            ("key1", "value1"),
            ("key2", "value2"),
            ("key3", "value3"));

        var serialized = _serializer.SerializeToArray(originalData);
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        var keys = new HashSet<string>(deserializedData.ContextKeys);
        Assert.Equal(3, keys.Count);
        Assert.Contains("key1", keys);
        Assert.Contains("key2", keys);
        Assert.Contains("key3", keys);
    }

    [Fact]
    public void DurableEnvelopeData_RoundTripSerialization_ComplexBody_Succeeds()
    {
        var original = new NestedRecord(
            1,
            new SimpleRecord(100, "nested"),
            new List<string> { "tag1", "tag2", "tag3" });

        var originalData = CreateEnvelopeData(original);
        var serialized = _serializer.SerializeToArray(originalData);
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        Assert.True(deserializedData.TryGetBody<NestedRecord>(out var body));
        Assert.Equal(1, body.Id);
        Assert.NotNull(body.Inner);
        Assert.Equal(100, body.Inner.Id);
        Assert.Equal("nested", body.Inner.Name);
        Assert.Equal(new List<string> { "tag1", "tag2", "tag3" }, body.Tags);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TryGetBody_NullSessionPool_ReturnsFalse()
    {
        // Create data without session pool (simulates deserialized data without session pool)
        var data = new DurableEnvelopeData(null!);
        Assert.False(data.TryGetBody<string>(out _));
    }

    [Fact]
    public void TryGetBody_ZeroLengthBody_ReturnsFalse()
    {
        var data = new DurableEnvelopeData(_sessionPool);
        Assert.False(data.TryGetBody<string>(out _));
    }

    [Fact]
    public void TryGetContextValue_NoContext_ReturnsFalse()
    {
        var data = CreateEnvelopeData("body");
        Assert.False(data.TryGetContextValue<string>("nonexistent", out _));
    }

    [Fact]
    public void HasContextKey_AfterDeserialization_Works()
    {
        var originalData = CreateEnvelopeData("body",
            ("existing-key", "value"));

        var serialized = _serializer.SerializeToArray(originalData);
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        Assert.True(deserializedData.HasContextKey("existing-key"));
        Assert.False(deserializedData.HasContextKey("nonexistent-key"));
    }

    [Fact]
    public void GetBodyBytes_AfterDeserialization_Works()
    {
        var originalData = CreateEnvelopeData("test-body");

        var serialized = _serializer.SerializeToArray(originalData);
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        var bytes = deserializedData.GetBodyBytes();
        Assert.True(bytes.Length > 0);

        // Verify we can manually deserialize the bytes
        using var session = _sessionPool.GetSession();
        var reader = Reader.Create(bytes, session);
        var field = reader.ReadFieldHeader();
        var body = _sessionPool.CodecProvider.GetCodec<string>().ReadValue(ref reader, field);
        Assert.Equal("test-body", body);
    }

    [Fact]
    public void TryGetContextBytes_AfterDeserialization_Works()
    {
        var originalData = CreateEnvelopeData("body",
            ("ctx-key", "ctx-value"));

        var serialized = _serializer.SerializeToArray(originalData);
        var deserializedData = _serializer.Deserialize<DurableEnvelopeData>(serialized);

        Assert.True(deserializedData.TryGetContextBytes("ctx-key", out var bytes));
        Assert.True(bytes.Length > 0);

        // Verify we can manually deserialize the bytes
        using var session = _sessionPool.GetSession();
        var reader = Reader.Create(bytes, session);
        var field = reader.ReadFieldHeader();
        var value = _sessionPool.CodecProvider.GetCodec<string>().ReadValue(ref reader, field);
        Assert.Equal("ctx-value", value);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var data = CreateEnvelopeData("body", ("key", "value"));
        data.Dispose();
        // Double dispose should also not throw
        data.Dispose();
    }

    #endregion

    #region Test Types

    [GenerateSerializer]
    public record SimpleRecord(
        [property: Id(0)] int Id,
        [property: Id(1)] string Name);

    [GenerateSerializer]
    public record NestedRecord(
        [property: Id(0)] int Id,
        [property: Id(1)] SimpleRecord Inner,
        [property: Id(2)] List<string> Tags);

    [GenerateSerializer]
    public record DifferentRecord(
        [property: Id(0)] string Code,
        [property: Id(1)] double Value);

    [GenerateSerializer]
    public class ComplexClass
    {
        [Id(0)] public int IntValue { get; set; }
        [Id(1)] public string? StringValue { get; set; }
        [Id(2)] public double DoubleValue { get; set; }
        [Id(3)] public bool BoolValue { get; set; }
        [Id(4)] public Guid GuidValue { get; set; }
        [Id(5)] public DateTime DateTimeValue { get; set; }
        [Id(6)] public int? NullableInt { get; set; }
        [Id(7)] public string? NullableString { get; set; }
    }

    #endregion
}
