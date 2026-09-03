using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.JsonConverters;
using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class StreamingJsonConverterTests
{
    [Theory]
    [InlineData("[1,42,7]", typeof(EventSequenceToken), 42L, 7)]
    [InlineData("[2,43,9]", typeof(EventSequenceTokenV2), 43L, 9)]
    [InlineData("[1,44]", typeof(EventSequenceToken), 44L, 0)]
    [InlineData("[2,45]", typeof(EventSequenceTokenV2), 45L, 0)]
    public void EventSequenceToken_ReadBaseType_ReturnsExactConcreteToken(
        string json,
        Type expectedType,
        long expectedSequenceNumber,
        int expectedEventIndex)
    {
        var token = DeserializeToken(json, typeof(StreamSequenceToken));

        Assert.Equal(expectedType, token.GetType());
        Assert.Equal(expectedSequenceNumber, token.SequenceNumber);
        Assert.Equal(expectedEventIndex, token.EventIndex);
    }

    [Theory]
    [InlineData("[1,10,2]", typeof(EventSequenceToken), 10L, 2)]
    [InlineData("[2,11,3]", typeof(EventSequenceTokenV2), 11L, 3)]
    public void EventSequenceToken_ReadMatchingConcreteType_ReturnsRequestedType(
        string json,
        Type requestedType,
        long expectedSequenceNumber,
        int expectedEventIndex)
    {
        var token = DeserializeToken(json, requestedType);

        Assert.Equal(requestedType, token.GetType());
        Assert.Equal(expectedSequenceNumber, token.SequenceNumber);
        Assert.Equal(expectedEventIndex, token.EventIndex);
    }

    [Theory]
    [InlineData("[2,42,7]", typeof(EventSequenceToken), typeof(EventSequenceTokenV2))]
    [InlineData("[1,42,7]", typeof(EventSequenceTokenV2), typeof(EventSequenceToken))]
    public void EventSequenceToken_ReadMismatchedConcreteType_ThrowsJsonException(
        string json,
        Type requestedType,
        Type payloadType)
    {
        var exception = Assert.Throws<JsonException>(() => DeserializeToken(json, requestedType));

        Assert.Contains($"Cannot deserialize {payloadType} as {requestedType}.", exception.Message, StringComparison.Ordinal);
        Assert.Equal("$", exception.Path);
    }

    [Theory]
    [InlineData("Non-array", "{}", true)]
    [InlineData("Empty array", "[]", true)]
    [InlineData("Nonnumeric discriminator", "[\"v1\",42]", true)]
    [InlineData("Missing sequence number", "[1]", true)]
    [InlineData("Nonnumeric sequence number", "[1,\"42\"]", true)]
    [InlineData("Nonnumeric event index", "[1,42,\"7\"]", true)]
    [InlineData("Missing closing array token", "[1,42,7", false)]
    [InlineData("Trailing item", "[1,42,7,99]", true)]
    public void EventSequenceToken_ReadMalformedPayload_ThrowsJsonException(
        string _,
        string json,
        bool converterReportsTokenType)
    {
        var exception = Assert.Throws<JsonException>(() => DeserializeToken(json, typeof(StreamSequenceToken)));

        Assert.Equal("$", exception.Path);
        if (converterReportsTokenType)
        {
            Assert.StartsWith(
                "Could not deserialize StreamSequenceToken.",
                exception.Message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EventSequenceToken_ReadUnsupportedDiscriminator_ReportsValue()
    {
        var exception = Assert.Throws<JsonException>(
            () => DeserializeToken("[99,42,7]", typeof(StreamSequenceToken)));

        Assert.Contains("Unsupported StreamSequenceToken type: 99", exception.Message, StringComparison.Ordinal);
        Assert.Equal("$", exception.Path);
    }

    [Fact]
    public void EventSequenceToken_ReadIncompleteSequence_ThrowsConverterError()
    {
        var converter = new EventSequenceTokenJsonConverter();
        var options = new JsonSerializerOptions();

        var exception = Assert.Throws<JsonException>(() => ReadIncompleteSequenceToken(converter, options));

        Assert.Equal("Could not deserialize StreamSequenceToken.", exception.Message);
    }

    private static StreamSequenceToken DeserializeToken(string json, Type requestedType)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new EventSequenceTokenJsonConverter());

        var result = JsonSerializer.Deserialize(json, requestedType, options);
        return Assert.IsAssignableFrom<StreamSequenceToken>(result);
    }

    private static StreamSequenceToken? ReadIncompleteSequenceToken(
        EventSequenceTokenJsonConverter converter,
        JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader("[1,42,"u8, isFinalBlock: false, state: default);
        reader.Read();
        return converter.Read(ref reader, typeof(StreamSequenceToken), options);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AsyncStream_ReadGenericStream_ReconstructsExactIdentity(bool isRewindable)
    {
        var provider = new FakeInternalStreamProvider(isRewindable);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStreamProvider>("fake", provider);
        using var serviceProvider = services.BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var expectedStreamId = StreamId.Create("orders", "order-42");
        var json = CreateStreamJson("fake", expectedStreamId, options);

        var stream = Assert.IsType<StreamImpl<int>>(
            JsonSerializer.Deserialize<IAsyncStream<int>>(json, options));

        Assert.Equal("fake", stream.ProviderName);
        Assert.Equal(new QualifiedStreamId("fake", expectedStreamId), stream.InternalStreamId);
        Assert.Equal(expectedStreamId, stream.StreamId);
        Assert.Equal("orders", stream.StreamId.GetNamespace());
        Assert.Equal("order-42", stream.StreamId.GetKeyAsString());
        Assert.Equal(isRewindable, stream.IsRewindable);
        Assert.Equal(0, provider.GetStreamCallCount);
        Assert.Equal(0, provider.ProducerInterfaceCallCount);
        Assert.Equal(0, provider.ConsumerInterfaceCallCount);
        Assert.Equal(0, provider.TotalOperationCount);
    }

    [Theory]
    [InlineData("Non-array", "{}", "Could not deserialize IAsyncStream.")]
    [InlineData("Empty array", "[]", "Could not deserialize IAsyncStream.")]
    [InlineData("Provider is not a string", "[123,<sid>]", "Could not deserialize IAsyncStream.")]
    [InlineData("Empty provider", "[\"\",<sid>]", "Could not deserialize IAsyncStream.")]
    [InlineData("Whitespace provider", "[\"   \",<sid>]", "Could not deserialize IAsyncStream.")]
    [InlineData("Missing stream ID", "[\"fake\"]", null)]
    [InlineData("Default/empty stream ID", "[\"fake\",[null,\"\"]]", "Could not deserialize StreamId.")]
    [InlineData("Missing closing array token", "[\"fake\",<sid>", null)]
    [InlineData("Trailing value", "[\"fake\",<sid>,true]", "Could not deserialize IAsyncStream.")]
    public void AsyncStream_ReadMalformedPayload_ThrowsJsonException(
        string _,
        string jsonTemplate,
        string? expectedMessage)
    {
        var provider = new FakeInternalStreamProvider(isRewindable: true);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStreamProvider>("fake", provider);
        using var serviceProvider = services.BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var streamIdJson = JsonSerializer.Serialize(StreamId.Create("orders", "order-42"), options);
        var json = jsonTemplate.Replace("<sid>", streamIdJson, StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<IAsyncStream<int>>(json, options));

        if (expectedMessage is not null)
        {
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal("$", exception.Path);
        Assert.Equal(0, provider.GetStreamCallCount);
        Assert.Equal(0, provider.ProducerInterfaceCallCount);
        Assert.Equal(0, provider.ConsumerInterfaceCallCount);
        Assert.Equal(0, provider.TotalOperationCount);
    }

    [Fact]
    public void AsyncStream_ReadNonGenericInterface_ThrowsJsonException()
    {
        var provider = new FakeInternalStreamProvider(isRewindable: true);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStreamProvider>("fake", provider);
        using var serviceProvider = services.BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var json = CreateStreamJson("fake", StreamId.Create("orders", "order-42"), options);

        var exception = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(json, typeof(IAsyncStream), options));

        Assert.Contains(
            "Cannot deserialize a stream reference as non-generic type Orleans.Streams.IAsyncStream.",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal("$", exception.Path);
        Assert.Equal(0, provider.GetStreamCallCount);
        Assert.Equal(0, provider.ProducerInterfaceCallCount);
        Assert.Equal(0, provider.ConsumerInterfaceCallCount);
        Assert.Equal(0, provider.TotalOperationCount);
    }

    [Fact]
    public void AsyncStream_ReadProviderWithoutInternalContract_ReportsProvider()
    {
        var provider = new ExternalStreamProvider();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStreamProvider>("external", provider);
        using var serviceProvider = services.BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var json = CreateStreamJson("external", StreamId.Create("orders", "order-42"), options);

        var exception = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<IAsyncStream<int>>(json, options));

        Assert.Contains(
            "Stream provider 'external' does not support internal stream references.",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal("$", exception.Path);
        Assert.Equal(0, provider.GetStreamCallCount);
    }

    [Fact]
    public void AsyncStream_ReadMissingKeyedProvider_PropagatesCurrentDiFailure()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var json = CreateStreamJson("missing", StreamId.Create("orders", "order-42"), options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.Deserialize<IAsyncStream<int>>(json, options));

        Assert.Contains(typeof(IStreamProvider).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncStream_ReadProviderActivationFailure_PropagatesSameException()
    {
        var expectedException = new InvalidOperationException("Provider activation failed.");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStreamProvider>(
            "throwing",
            (_, _) => throw expectedException);
        using var serviceProvider = services.BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var json = CreateStreamJson("throwing", StreamId.Create("orders", "order-42"), options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.Deserialize<IAsyncStream<int>>(json, options));

        Assert.Same(expectedException, exception);
    }

    [Fact]
    public void AsyncStream_ReadProviderWithoutFollowingValue_ThrowsConverterError()
    {
        var runtimeClient = Substitute.For<IRuntimeClient>();
        var converter = new AsyncStreamConverter(runtimeClient);
        var options = new JsonSerializerOptions();

        var exception = Assert.Throws<JsonException>(() => ReadIncompleteAsyncStream(converter, options));

        Assert.Equal("Could not deserialize IAsyncStream.", exception.Message);
    }

    private static IAsyncStream? ReadIncompleteAsyncStream(
        AsyncStreamConverter converter,
        JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader("[\"fake\""u8, isFinalBlock: false, state: default);
        reader.Read();
        return converter.Read(ref reader, typeof(IAsyncStream<int>), options);
    }

    private static JsonSerializerOptions CreateStreamOptions(IServiceProvider serviceProvider)
    {
        var runtimeClient = Substitute.For<IRuntimeClient>();
        runtimeClient.ServiceProvider.Returns(serviceProvider);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new AsyncStreamConverter(runtimeClient));
        return options;
    }

    private static string CreateStreamJson(
        string providerName,
        StreamId streamId,
        JsonSerializerOptions options)
    {
        var providerNameJson = JsonSerializer.Serialize(providerName, options);
        var streamIdJson = JsonSerializer.Serialize(streamId, options);
        return $"[{providerNameJson},{streamIdJson}]";
    }

    private sealed class FakeInternalStreamProvider(bool isRewindable)
        : IStreamProvider, IInternalStreamProvider
    {
        public string Name => "fake";

        public bool IsRewindable { get; } = isRewindable;

        public int GetStreamCallCount { get; private set; }

        public int ProducerInterfaceCallCount { get; private set; }

        public int ConsumerInterfaceCallCount { get; private set; }

        public int TotalOperationCount
            => GetStreamCallCount + ProducerInterfaceCallCount + ConsumerInterfaceCallCount;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            GetStreamCallCount++;
            throw new InvalidOperationException("Stream operations are not expected during deserialization.");
        }

        public IInternalAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> streamId)
        {
            ProducerInterfaceCallCount++;
            throw new InvalidOperationException("Producer operations are not expected during deserialization.");
        }

        public IInternalAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            ConsumerInterfaceCallCount++;
            throw new InvalidOperationException("Consumer operations are not expected during deserialization.");
        }
    }

    private sealed class ExternalStreamProvider : IStreamProvider
    {
        public string Name => "external";

        public bool IsRewindable => true;

        public int GetStreamCallCount { get; private set; }

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            GetStreamCallCount++;
            throw new InvalidOperationException("Stream operations are not expected during deserialization.");
        }
    }

    #region QualifiedStreamId JSON identity contracts (InternalStreamId.cs)

    [Theory]
    [InlineData("orders", "order-42", "fake")]
    [InlineData(null, "order-1", "fake")]
    [InlineData("名前空間", "キー-🔑", "プロバイダ")]
    [InlineData("a:b", "c:d", "provider:name")]
    public void QualifiedStreamId_WriteThenRead_RoundTripsExactIdentity(string? ns, string key, string providerName)
    {
        var streamId = StreamId.Create(ns, key);
        var original = new QualifiedStreamId(providerName, streamId);

        var json = JsonSerializer.Serialize(original);
        var expectedJson = $"[{JsonSerializer.Serialize(providerName)},{JsonSerializer.Serialize(streamId)}]";
        Assert.Equal(expectedJson, json);

        var roundTripped = JsonSerializer.Deserialize<QualifiedStreamId>(json);
        Assert.Equal(original, roundTripped);
        Assert.Equal(providerName, roundTripped.ProviderName);
        Assert.Equal(streamId, roundTripped.StreamId);
        Assert.Equal(ns, roundTripped.StreamId.GetNamespace());
        Assert.Equal(key, roundTripped.StreamId.GetKeyAsString());
    }

    [Fact]
    public void QualifiedStreamId_Write_DefaultValue_ThrowsJsonException()
    {
        var converter = new QualifiedStreamIdJsonConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Assert.Throws<JsonException>(() => converter.Write(writer, default, new JsonSerializerOptions()));

        Assert.Equal("Could not serialize QualifiedStreamId.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void QualifiedStreamId_Write_EmptyOrWhitespaceProviderName_ThrowsJsonException(string providerName)
    {
        var converter = new QualifiedStreamIdJsonConverter();
        var value = new QualifiedStreamId(providerName, StreamId.Create("orders", "order-42"));
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Assert.Throws<JsonException>(() => converter.Write(writer, value, new JsonSerializerOptions()));

        Assert.Equal("Could not serialize QualifiedStreamId.", exception.Message);
    }

    [Theory]
    [InlineData("Non-array", "{}", "Could not deserialize QualifiedStreamId.")]
    [InlineData("Empty array", "[]", "Could not deserialize QualifiedStreamId.")]
    [InlineData("Provider is not a string", "[123,[\"orders\",\"order-42\"]]", "Could not deserialize QualifiedStreamId.")]
    [InlineData("Empty provider", "[\"\",[\"orders\",\"order-42\"]]", "Could not deserialize QualifiedStreamId.")]
    [InlineData("Whitespace provider", "[\"   \",[\"orders\",\"order-42\"]]", "Could not deserialize QualifiedStreamId.")]
    [InlineData("Missing stream id element", "[\"fake\"]", null)]
    [InlineData("Default/empty embedded StreamId", "[\"fake\",[null,\"\"]]", "Could not deserialize StreamId.")]
    [InlineData("Missing closing array token", "[\"fake\",[\"orders\",\"order-42\"]", null)]
    [InlineData("Trailing value", "[\"fake\",[\"orders\",\"order-42\"],true]", "Could not deserialize QualifiedStreamId.")]
    public void QualifiedStreamId_Read_MalformedPayload_ThrowsJsonException(string _, string json, string? expectedMessage)
    {
        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<QualifiedStreamId>(json));

        if (expectedMessage is not null)
        {
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QualifiedStreamId_ReadProviderWithoutFollowingValue_ThrowsConverterError()
    {
        var converter = new QualifiedStreamIdJsonConverter();
        var options = new JsonSerializerOptions();

        var exception = Assert.Throws<JsonException>(() => ReadIncompleteQualifiedStreamId(converter, options));

        Assert.Equal("Could not deserialize QualifiedStreamId.", exception.Message);
    }

    private static QualifiedStreamId ReadIncompleteQualifiedStreamId(
        QualifiedStreamIdJsonConverter converter,
        JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader("[\"fake\""u8, isFinalBlock: false, state: default);
        reader.Read();
        return converter.Read(ref reader, typeof(QualifiedStreamId), options);
    }

    [Theory]
    [InlineData("orders", "order-42", "fake", "4:fake6:ordersorder-42")]
    [InlineData(null, "order-1", "fake", "4:fake0:order-1")]
    [InlineData("名前空間", "キー-🔑", "プロバイダ", "5:プロバイダ4:名前空間キー-🔑")]
    [InlineData("a:b", "c:d", "provider:name", "13:provider:name3:a:bc:d")]
    public void QualifiedStreamId_ReadWriteAsPropertyName_RoundTripsViaDictionaryWithStableFormat(
        string? ns,
        string key,
        string providerName,
        string expectedPropertyName)
    {
        var streamId = StreamId.Create(ns, key);
        var value = new QualifiedStreamId(providerName, streamId);
        var dictionary = new Dictionary<QualifiedStreamId, int> { [value] = 42 };

        var json = JsonSerializer.Serialize(dictionary);

        var rawDictionary = JsonSerializer.Deserialize<Dictionary<string, int>>(json)!;
        var actualPropertyName = Assert.Single(rawDictionary.Keys);
        Assert.Equal(expectedPropertyName, actualPropertyName);
        Assert.Equal(42, rawDictionary[actualPropertyName]);

        var roundTripped = JsonSerializer.Deserialize<Dictionary<QualifiedStreamId, int>>(json)!;
        var entry = Assert.Single(roundTripped);
        Assert.Equal(value, entry.Key);
        Assert.Equal(42, entry.Value);
    }

    [Fact]
    public void QualifiedStreamId_WriteAsPropertyName_DefaultValue_ThrowsJsonException()
    {
        var converter = new QualifiedStreamIdJsonConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Assert.Throws<JsonException>(() => converter.WriteAsPropertyName(writer, default, new JsonSerializerOptions()));

        Assert.Equal("Could not serialize QualifiedStreamId.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void QualifiedStreamId_WriteAsPropertyName_EmptyOrWhitespaceProviderName_ThrowsJsonException(string providerName)
    {
        var converter = new QualifiedStreamIdJsonConverter();
        var value = new QualifiedStreamId(providerName, StreamId.Create("orders", "order-42"));
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Assert.Throws<JsonException>(() => converter.WriteAsPropertyName(writer, value, new JsonSerializerOptions()));

        Assert.Equal("Could not serialize QualifiedStreamId.", exception.Message);
    }

    [Theory]
    [InlineData("Missing colon", "nocolon", "Failed to parse QualifiedStreamId from property name.")]
    [InlineData("Non-numeric length prefix", "ab:rest", "Failed to parse QualifiedStreamId from property name.")]
    [InlineData("Length exceeds remaining text", "50:short", "Failed to parse QualifiedStreamId from property name.")]
    [InlineData("Zero-length provider name", "0:rest", "Failed to parse QualifiedStreamId from property name.")]
    [InlineData("Whitespace-only provider name", "3:   6:ordersorder-42", "Failed to parse QualifiedStreamId from property name.")]
    [InlineData("Provider consumes remaining text", "4:fake", "Failed to parse StreamId from property name.")]
    [InlineData("Malformed inner StreamId segment", "4:fakeNOCOLON", "Failed to parse StreamId from property name.")]
    public void QualifiedStreamId_ReadAsPropertyName_MalformedPropertyName_ThrowsJsonException(string _, string propertyName, string expectedMessage)
    {
        var json = $"{{{JsonSerializer.Serialize(propertyName)}:1}}";

        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Dictionary<QualifiedStreamId, int>>(json));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void QualifiedStreamId_CompareTo_DifferentStreamId_OrdersByStreamId()
    {
        var a = new QualifiedStreamId("fake", StreamId.Create("orders", "a"));
        var b = new QualifiedStreamId("fake", StreamId.Create("orders", "b"));

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
    }

    [Fact]
    public void QualifiedStreamId_CompareTo_SameStreamId_OrdersByProviderNameOrdinal()
    {
        var streamId = StreamId.Create("orders", "order-42");
        var a = new QualifiedStreamId("alpha", streamId);
        var b = new QualifiedStreamId("beta", streamId);

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(0, a.CompareTo(new QualifiedStreamId("alpha", streamId)));
    }

    [Fact]
    public void QualifiedStreamId_InequalityOperator_MatchesEquals()
    {
        var streamId = StreamId.Create("orders", "order-42");
        var a = new QualifiedStreamId("fake", streamId);
        var b = new QualifiedStreamId("fake", streamId);
        var c = new QualifiedStreamId("other", streamId);

        Assert.False(a != b);
        Assert.True(a != c);
    }

    [Fact]
    public void QualifiedStreamId_ExplicitIFormattableToString_MatchesToString()
    {
        var value = new QualifiedStreamId("fake", StreamId.Create("orders", "order-42"));
        var formattable = (IFormattable)value;

        Assert.Equal("fake/orders/order-42", value.ToString());
        Assert.Equal("fake/orders/order-42", formattable.ToString(null, null));
        Assert.Equal("fake/orders/order-42", formattable.ToString("ignored-format", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(1, false)]
    public void QualifiedStreamId_TryFormat_RespectsDestinationBufferSize(int bufferSize, bool expectedResult)
    {
        var value = new QualifiedStreamId("fake", StreamId.Create("orders", "order-42"));
        var destination = new char[bufferSize];
        var formattable = (ISpanFormattable)value;

        var result = formattable.TryFormat(destination, out var charsWritten, default, null);

        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal("fake/orders/order-42", new string(destination, 0, charsWritten));
        }
        else
        {
            Assert.Equal(0, charsWritten);
        }
    }

    #endregion

    #region StreamId JSON identity contracts (StreamId.cs)

    [Theory]
    [InlineData("orders", "order-42")]
    [InlineData(null, "order-1")]
    [InlineData("名前空間", "キー-🔑")]
    public void StreamId_WriteThenRead_RoundTripsExactIdentity(string? ns, string key)
    {
        var original = StreamId.Create(ns, key);

        var json = JsonSerializer.Serialize(original);
        var expectedNamespaceJson = ns is null ? "null" : JsonSerializer.Serialize(ns);
        Assert.Equal($"[{expectedNamespaceJson},{JsonSerializer.Serialize(key)}]", json);

        var roundTripped = JsonSerializer.Deserialize<StreamId>(json);
        Assert.Equal(original, roundTripped);
        Assert.Equal(ns, roundTripped.GetNamespace());
        Assert.Equal(key, roundTripped.GetKeyAsString());
    }

    [Fact]
    public void StreamId_Write_DefaultValue_ThrowsJsonException()
    {
        var converter = new StreamIdJsonConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Assert.Throws<JsonException>(() => converter.Write(writer, default, new JsonSerializerOptions()));

        Assert.Equal("Could not serialize StreamId.", exception.Message);
    }

    [Fact]
    public void StreamId_WriteAsPropertyName_DefaultValue_ThrowsJsonException()
    {
        var converter = new StreamIdJsonConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var exception = Assert.Throws<JsonException>(
            () => converter.WriteAsPropertyName(writer, default, new JsonSerializerOptions()));

        Assert.Equal("Could not serialize StreamId.", exception.Message);
    }

    [Theory]
    [InlineData("Non-array", "{}", true)]
    [InlineData("Namespace token wrong type", "[123,\"key\"]", true)]
    [InlineData("Missing key token", "[\"ns\"]", true)]
    [InlineData("Key token wrong type", "[\"ns\",123]", true)]
    [InlineData("Missing closing array token", "[\"ns\",\"key\"", false)]
    [InlineData("Null key", "[\"ns\",null]", true)]
    [InlineData("Trailing value", "[\"ns\",\"key\",true]", true)]
    public void StreamId_Read_MalformedPayload_ThrowsJsonException(string _, string json, bool converterReportsMessage)
    {
        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StreamId>(json));

        if (converterReportsMessage)
        {
            Assert.Equal("Could not deserialize StreamId.", exception.Message);
        }
    }

    [Theory]
    [InlineData("orders", "order-42")]
    [InlineData(null, "order-1")]
    [InlineData("名前空間", "キー-🔑")]
    [InlineData("a:b", "c:d")]
    public void StreamId_ReadWriteAsPropertyName_RoundTripsViaDictionaryWithStableFormat(string? ns, string key)
    {
        var value = StreamId.Create(ns, key);
        var dictionary = new Dictionary<StreamId, int> { [value] = 7 };

        var json = JsonSerializer.Serialize(dictionary);

        var rawDictionary = JsonSerializer.Deserialize<Dictionary<string, int>>(json)!;
        var actualPropertyName = Assert.Single(rawDictionary.Keys);
        Assert.Equal(StreamIdJsonConverter.FormatPropertyName(value), actualPropertyName);
        Assert.Equal(7, rawDictionary[actualPropertyName]);

        var roundTripped = JsonSerializer.Deserialize<Dictionary<StreamId, int>>(json)!;
        var entry = Assert.Single(roundTripped);
        Assert.Equal(value, entry.Key);
        Assert.Equal(7, entry.Value);
    }

    [Theory]
    [InlineData("orders", "order-42", "6:ordersorder-42")]
    [InlineData(null, "order-1", "0:order-1")]
    [InlineData("名前空間", "キー-🔑", "4:名前空間キー-🔑")]
    [InlineData("a:b", "c:d", "3:a:bc:d")]
    public void StreamId_FormatPropertyName_ProducesStableLengthPrefixedFormat(string? ns, string key, string expectedFormat)
    {
        var value = StreamId.Create(ns, key);

        var formatted = StreamIdJsonConverter.FormatPropertyName(value);
        Assert.Equal(expectedFormat, formatted);

        var parsed = StreamIdJsonConverter.ParsePropertyName(formatted);
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("nocolon", "Failed to parse StreamId from property name.")]
    [InlineData("ab:rest", "Failed to parse StreamId from property name.")]
    [InlineData("50:short", "Failed to parse StreamId from property name.")]
    [InlineData("0:", "Could not deserialize StreamId.")]
    public void StreamId_ParsePropertyName_MalformedInput_ThrowsJsonException(string value, string expectedMessage)
    {
        var exception = Assert.Throws<JsonException>(() => StreamIdJsonConverter.ParsePropertyName(value));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void StreamId_Read_NamespaceExceedsUshortMaxBytes_ThrowsJsonException()
    {
        var oversizedNamespace = new string('n', ushort.MaxValue + 1);
        var json = JsonSerializer.Serialize(new[] { oversizedNamespace, "key" });

        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StreamId>(json));

        Assert.Equal($"StreamId namespaces cannot exceed {ushort.MaxValue} UTF-8 bytes.", exception.Message);
    }

    [Fact]
    public void StreamId_Read_NamespaceExactlyAtUshortMaxBytes_DoesNotThrow()
    {
        var boundaryNamespace = new string('n', ushort.MaxValue);
        var json = JsonSerializer.Serialize(new[] { boundaryNamespace, "key" });

        var value = JsonSerializer.Deserialize<StreamId>(json);

        Assert.Equal(boundaryNamespace, value.GetNamespace());
        Assert.Equal("key", value.GetKeyAsString());
    }

    [Fact]
    public void StreamId_ParsePropertyName_UnpairedSurrogateInNamespace_ThrowsJsonExceptionWrappingEncoderFallback()
    {
        var propertyNameWithLoneHighSurrogate = "1:\uD800k";

        var exception = Assert.Throws<JsonException>(() => StreamIdJsonConverter.ParsePropertyName(propertyNameWithLoneHighSurrogate));

        Assert.Equal("StreamId components must contain valid UTF-8.", exception.Message);
        Assert.IsType<EncoderFallbackException>(exception.InnerException);
    }

    [Fact]
    public void StreamId_Write_InvalidUtf8KeyBytes_ThrowsJsonExceptionWrappingDecoderFallback()
    {
        var invalidUtf8Key = new byte[] { 0xFF, 0xFE };
        var value = StreamId.Create(ReadOnlySpan<byte>.Empty, invalidUtf8Key);

        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(value));

        Assert.Equal("StreamId components must contain valid UTF-8.", exception.Message);
        Assert.IsType<DecoderFallbackException>(exception.InnerException);
    }

    [Fact]
    public void StreamId_Parse_ValidByteSpan_RoundTripsIdentity()
    {
        var expected = StreamId.Create("orders", "order-42");

        var parsed = StreamId.Parse(Encoding.UTF8.GetBytes("orders/order-42"));

        Assert.Equal(expected, parsed);
        Assert.Equal("orders", parsed.GetNamespace());
        Assert.Equal("order-42", parsed.GetKeyAsString());
    }

    [Fact]
    public void StreamId_Parse_MissingSeparator_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => StreamId.Parse(Encoding.UTF8.GetBytes("no-separator")));

        Assert.Contains("Unable to parse", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamId_IdentityMembers_EqualsCompareToToString_AreConsistent()
    {
        var a = StreamId.Create("orders", "order-1");
        var b = StreamId.Create("orders", "order-2");
        var aCopy = StreamId.Create("orders", "order-1");

        Assert.True(a.Equals(aCopy));
        Assert.False(a.Equals(b));
        Assert.True(a != b);
        Assert.False(a != aCopy);
        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(0, a.CompareTo(aCopy));
        Assert.Equal("orders/order-1", a.ToString());
        Assert.Equal(a.ToString(), ((IFormattable)a).ToString(null, null));
    }

    #endregion

    #region AsyncStreamConverter.Write (adjacent identity contract)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AsyncStream_WriteThenRead_RoundTripsExactIdentity(bool isRewindable)
    {
        var provider = new FakeInternalStreamProvider(isRewindable);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStreamProvider>("fake", provider);
        using var serviceProvider = services.BuildServiceProvider();
        var options = CreateStreamOptions(serviceProvider);
        var expectedStreamId = StreamId.Create("orders", "order-42");
        var json = CreateStreamJson("fake", expectedStreamId, options);

        var stream = Assert.IsType<StreamImpl<int>>(JsonSerializer.Deserialize<IAsyncStream<int>>(json, options));
        var serialized = JsonSerializer.Serialize<IAsyncStream<int>>(stream, options);

        Assert.Equal(json, serialized);

        var roundTripped = Assert.IsType<StreamImpl<int>>(JsonSerializer.Deserialize<IAsyncStream<int>>(serialized, options));
        Assert.Equal("fake", roundTripped.ProviderName);
        Assert.Equal(expectedStreamId, roundTripped.StreamId);
        Assert.Equal(isRewindable, roundTripped.IsRewindable);
        Assert.Equal(0, provider.GetStreamCallCount);
        Assert.Equal(0, provider.ProducerInterfaceCallCount);
        Assert.Equal(0, provider.ConsumerInterfaceCallCount);
    }

    #endregion

    #region EventSequenceTokenJsonConverter.Write (adjacent identity contract)

    [Theory]
    [InlineData(typeof(EventSequenceToken), 42L, "[1,42]")]
    [InlineData(typeof(EventSequenceTokenV2), 43L, "[2,43]")]
    public void EventSequenceToken_Write_EventIndexZero_OmitsThirdElement(Type tokenType, long sequenceNumber, string expectedJson)
    {
        var token = CreateSequenceToken(tokenType, sequenceNumber, eventIndex: 0);

        var json = SerializeSequenceToken(token);

        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData(typeof(EventSequenceToken), 42L, 7, "[1,42,7]")]
    [InlineData(typeof(EventSequenceTokenV2), 43L, 9, "[2,43,9]")]
    public void EventSequenceToken_Write_NonZeroEventIndex_IncludesThirdElement(Type tokenType, long sequenceNumber, int eventIndex, string expectedJson)
    {
        var token = CreateSequenceToken(tokenType, sequenceNumber, eventIndex);

        var json = SerializeSequenceToken(token);

        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData(typeof(EventSequenceToken), 42L, 0)]
    [InlineData(typeof(EventSequenceToken), 42L, 7)]
    [InlineData(typeof(EventSequenceTokenV2), 43L, 0)]
    [InlineData(typeof(EventSequenceTokenV2), 43L, 9)]
    public void EventSequenceToken_WriteThenRead_RoundTripsExactIdentity(Type tokenType, long sequenceNumber, int eventIndex)
    {
        var token = CreateSequenceToken(tokenType, sequenceNumber, eventIndex);
        var json = SerializeSequenceToken(token);

        var roundTripped = DeserializeToken(json, typeof(StreamSequenceToken));

        Assert.Equal(tokenType, roundTripped.GetType());
        Assert.Equal(sequenceNumber, roundTripped.SequenceNumber);
        Assert.Equal(eventIndex, roundTripped.EventIndex);
    }

    [Fact]
    public void EventSequenceToken_Write_UnsupportedTokenType_ThrowsNotSupportedException()
    {
        var token = new FakeStreamSequenceToken();

        var exception = Assert.Throws<NotSupportedException>(() => SerializeSequenceToken(token));

        Assert.Contains(nameof(FakeStreamSequenceToken), exception.Message, StringComparison.Ordinal);
    }

    private static StreamSequenceToken CreateSequenceToken(Type tokenType, long sequenceNumber, int eventIndex)
        => tokenType == typeof(EventSequenceTokenV2)
            ? new EventSequenceTokenV2(sequenceNumber, eventIndex)
            : new EventSequenceToken(sequenceNumber, eventIndex);

    private static string SerializeSequenceToken(StreamSequenceToken token)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new EventSequenceTokenJsonConverter());
        return JsonSerializer.Serialize(token, typeof(StreamSequenceToken), options);
    }

    private sealed class FakeStreamSequenceToken : StreamSequenceToken
    {
        public override long SequenceNumber { get; protected set; }

        public override int EventIndex { get; protected set; }

        public override bool Equals(StreamSequenceToken? other) => ReferenceEquals(this, other);

        public override int CompareTo(StreamSequenceToken? other) => 0;
    }

    #endregion
}
