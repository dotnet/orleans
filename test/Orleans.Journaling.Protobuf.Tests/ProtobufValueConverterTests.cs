using System.Buffers;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Xunit;

namespace Orleans.Journaling.Protobuf.Tests;

[TestCategory("BVT")]
public class ProtobufValueConverterTests
{
    [Fact]
    public void ProtobufValueConverter_BuiltInScalars_RoundTrip()
    {
        AssertNativeRoundTrip(true);
        AssertNativeRoundTrip(42);
        AssertNativeRoundTrip(-42);
        AssertNativeRoundTrip(42u);
        AssertNativeRoundTrip(42L);
        AssertNativeRoundTrip(-42L);
        AssertNativeRoundTrip(42UL);
        AssertNativeRoundTrip(1.5F);
        AssertNativeRoundTrip(2.5D);

        var converter = new ProtobufValueConverter<byte[]>();
        var value = new byte[] { 1, 2, 3 };

        Assert.True(ProtobufValueConverter<byte[]>.IsNativeType);
        Assert.Equal(value, converter.FromBytes(new ReadOnlySequence<byte>(converter.ToBytes(value))));
    }

    [Fact]
    public void ProtobufCodecProvider_RegisteredMessageParser_UsesNativePayload()
    {
        var builder = new TestSiloBuilder();
        builder.UseProtobufCodec(options => options.AddMessageParser(StringValue.Parser));
        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<ProtobufLogFormat>(serviceProvider.GetRequiredKeyedService<IStateMachineLogFormat>(StateMachineLogFormatKeys.Protobuf));
        var codec = serviceProvider.GetRequiredKeyedService<IDurableValueCodecProvider>(StateMachineLogFormatKeys.Protobuf).GetCodec<StringValue>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(new StringValue { Value = "hello" }, buffer);

        Assert.Equal([8, 0, 18, 8, 1, 10, 5, 104, 101, 108, 108, 111], buffer.WrittenSpan.ToArray());
        var consumer = new ValueConsumer<StringValue>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
        Assert.Equal("hello", consumer.Value?.Value);
    }

    [Fact]
    public void ProtobufCodecProvider_UnregisteredMessage_UsesFallbackCodec()
    {
        var builder = new TestSiloBuilder();
        var fallbackCodec = new StringValueFallbackCodec();
        builder.Services.AddSingleton<ILogDataCodec<StringValue>>(fallbackCodec);
        builder.UseProtobufCodec();
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var codec = serviceProvider.GetRequiredKeyedService<IDurableValueCodecProvider>(StateMachineLogFormatKeys.Protobuf).GetCodec<StringValue>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(new StringValue { Value = "hello" }, buffer);

        Assert.Equal([8, 0, 18, 2, 1, 42], buffer.WrittenSpan.ToArray());
        var consumer = new ValueConsumer<StringValue>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
        Assert.Equal("fallback", consumer.Value?.Value);
        Assert.Equal(1, fallbackCodec.WriteCount);
        Assert.Equal(1, fallbackCodec.ReadCount);
    }

    [Fact]
    public void ProtobufCodecProvider_UnregisteredMessageWithoutFallback_ThrowsHelpfulMessage()
    {
        var builder = new TestSiloBuilder();
        builder.UseProtobufCodec();
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredKeyedService<IDurableValueCodecProvider>(StateMachineLogFormatKeys.Protobuf);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetCodec<StringValue>());

        Assert.Contains("AddMessageParser(StringValue.Parser)", exception.Message);
    }

    [Fact]
    public void ProtobufCodecProvider_UnsupportedTypeWithoutFallback_ThrowsHelpfulMessage()
    {
        var builder = new TestSiloBuilder();
        builder.UseProtobufCodec();
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredKeyedService<IDurableValueCodecProvider>(StateMachineLogFormatKeys.Protobuf);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetCodec<UnsupportedValue>());

        Assert.Contains("ILogDataCodec", exception.Message);
        Assert.Contains(typeof(UnsupportedValue).FullName!, exception.Message);
    }

    private static void AssertNativeRoundTrip<T>(T value)
    {
        Assert.True(ProtobufValueConverter<T>.IsNativeType);
        var converter = new ProtobufValueConverter<T>();

        Assert.Equal(value, converter.FromBytes(new ReadOnlySequence<byte>(converter.ToBytes(value))));
    }

    private sealed class StringValueFallbackCodec : ILogDataCodec<StringValue>
    {
        public int WriteCount { get; private set; }

        public int ReadCount { get; private set; }

        public void Write(StringValue value, IBufferWriter<byte> output)
        {
            WriteCount++;
            var span = output.GetSpan(1);
            span[0] = 42;
            output.Advance(1);
        }

        public StringValue Read(ReadOnlySequence<byte> input, out long bytesConsumed)
        {
            ReadCount++;
            Assert.Equal([42], input.ToArray());
            bytesConsumed = input.Length;
            return new StringValue { Value = "fallback" };
        }
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class ValueConsumer<T> : IDurableValueLogEntryConsumer<T>
    {
        public T? Value { get; private set; }

        public void ApplySet(T value) => Value = value;
    }

    private sealed record UnsupportedValue(int Value);
}
