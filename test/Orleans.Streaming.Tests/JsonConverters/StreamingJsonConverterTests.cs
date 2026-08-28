using System;
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
}
