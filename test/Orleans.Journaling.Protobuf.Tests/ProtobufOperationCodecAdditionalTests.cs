using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling.Tests;
using Xunit;

namespace Orleans.Journaling.Protobuf.Tests;

[TestCategory("BVT")]
public sealed class ProtobufOperationCodecAdditionalTests
{
    [Fact]
    public void DictionaryCodec_AllCommands_RoundTrip()
    {
        var codec = new ProtobufDictionaryOperationCodec<string, int>(Native<string>(), Native<int>());
        var consumer = new RecordingDictionaryOperationHandler<string, int>();

        Apply(codec, writer => codec.WriteSet("alpha", 1, writer), consumer);
        Apply(codec, writer => codec.WriteRemove("alpha", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([new("beta", 2), new("gamma", 3)], writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([], writer), consumer);

        Assert.Equal(
            [
                "set:alpha:1",
                "remove:alpha",
                "clear",
                "reset:2",
                "set:beta:2",
                "set:gamma:3",
                "reset:0"
            ],
            consumer.Commands);
    }

    [Fact]
    public void OperationCodecs_RejectUnsupportedCommands()
    {
        var payload = CodecTestHelpers.Sequence(new byte[] { 8, 99 });

        Assert.Throws<NotSupportedException>(() => new ProtobufDictionaryOperationCodec<string, int>(Native<string>(), Native<int>()).Apply(payload, new RecordingDictionaryOperationHandler<string, int>()));
        Assert.Throws<NotSupportedException>(() => new ProtobufListOperationCodec<int>(Native<int>()).Apply(payload, new RecordingListOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new ProtobufQueueOperationCodec<int>(Native<int>()).Apply(payload, new RecordingQueueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new ProtobufSetOperationCodec<int>(Native<int>()).Apply(payload, new RecordingSetOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new ProtobufValueOperationCodec<int>(Native<int>()).Apply(payload, new RecordingValueOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new ProtobufStateOperationCodec<int>(Native<int>()).Apply(payload, new RecordingStateOperationHandler<int>()));
        Assert.Throws<NotSupportedException>(() => new ProtobufTcsOperationCodec<int>(Native<int>()).Apply(payload, new RecordingTaskCompletionSourceOperationHandler<int>()));
    }

    [Fact]
    public void OperationCodecs_RejectDuplicateCommandField()
    {
        var codec = new ProtobufValueOperationCodec<int>(Native<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.Sequence(new byte[] { 8, 0, 8, 0 }), new RecordingValueOperationHandler<int>()));

        Assert.Contains("duplicate field 'command'", exception.Message);
    }

    [Fact]
    public void DictionaryCodec_RejectsDuplicateValueFieldForRemove()
    {
        var codec = new ProtobufDictionaryOperationCodec<string, int>(Native<string>(), Native<int>());
        var buffer = CodecTestHelpers.Write(writer =>
        {
            var key = Native<string>().ToBytes("alpha");
            writer.Write(new byte[] { 8, 1, 18, (byte)key.Length });
            writer.Write(key);
            writer.Write(new byte[] { 26, 1, 0 });
            writer.Write(new byte[] { 26, 1, 0 });
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.Sequence(buffer.WrittenMemory), new RecordingDictionaryOperationHandler<string, int>()));

        Assert.Contains("duplicate field 'value'", exception.Message);
    }

    [Fact]
    public void NativeValueConverter_RoundTripsScalarEdges()
    {
        AssertNativeRoundTrip(int.MinValue);
        AssertNativeRoundTrip(int.MaxValue);
        AssertNativeRoundTrip(uint.MaxValue);
        AssertNativeRoundTrip(long.MinValue);
        AssertNativeRoundTrip(long.MaxValue);
        AssertNativeRoundTrip(ulong.MaxValue);
        AssertNativeRoundTrip("");
        AssertNativeRoundTrip("hello \u03c0");
        AssertNativeByteArrayRoundTrip([]);
        AssertNativeByteArrayRoundTrip([1, 2, 3]);

        var floatConverter = new ProtobufValueConverter<float>();
        Assert.True(float.IsNaN(floatConverter.FromBytes(new ReadOnlySequence<byte>(floatConverter.ToBytes(float.NaN)))));
        Assert.Equal(float.PositiveInfinity, floatConverter.FromBytes(new ReadOnlySequence<byte>(floatConverter.ToBytes(float.PositiveInfinity))));

        var doubleConverter = new ProtobufValueConverter<double>();
        Assert.True(double.IsNaN(doubleConverter.FromBytes(new ReadOnlySequence<byte>(doubleConverter.ToBytes(double.NaN)))));
        Assert.Equal(double.NegativeInfinity, doubleConverter.FromBytes(new ReadOnlySequence<byte>(doubleConverter.ToBytes(double.NegativeInfinity))));
    }

    [Fact]
    public void UseProtobufCodec_RegistersEveryFormatFamilyProviderByKey()
    {
        var builder = new TestSiloBuilder();

        builder.UseProtobufCodec();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<ProtobufLogFormat>(serviceProvider.GetRequiredKeyedService<ILogFormat>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.IsType<ProtobufLogFormat>(serviceProvider.GetRequiredService<ILogFormat>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableDictionaryOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableListOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableQueueOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableSetOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableStateOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableTaskCompletionSourceOperationCodecProvider>(ProtobufJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableDictionaryOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableListOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableQueueOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableSetOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableValueOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableStateOperationCodecProvider>());
        Assert.Same(serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>(), serviceProvider.GetRequiredService<IDurableTaskCompletionSourceOperationCodecProvider>());
    }

    [Fact]
    public void OperationCodecProvider_CachesPerClosedGenericCodecType()
    {
        var builder = new TestSiloBuilder();
        builder.UseProtobufCodec();
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<ProtobufOperationCodecProvider>();

        Assert.Same(provider.GetCodec<string, int>(), provider.GetCodec<string, int>());
        Assert.NotSame(provider.GetCodec<string, int>(), provider.GetCodec<string, long>());
        Assert.Same(provider.GetCodec<string>(), provider.GetCodec<string>());
        Assert.Same(((IDurableQueueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableQueueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableSetOperationCodecProvider)provider).GetCodec<int>(), ((IDurableSetOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableValueOperationCodecProvider)provider).GetCodec<int>(), ((IDurableValueOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableStateOperationCodecProvider)provider).GetCodec<int>(), ((IDurableStateOperationCodecProvider)provider).GetCodec<int>());
        Assert.Same(((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>(), ((IDurableTaskCompletionSourceOperationCodecProvider)provider).GetCodec<int>());
    }

    private static ProtobufValueConverter<T> Native<T>() => new();

    private static void Apply<TKey, TValue>(
        IDurableDictionaryOperationCodec<TKey, TValue> codec,
        Action<LogStreamWriter> write,
        RecordingDictionaryOperationHandler<TKey, TValue> consumer)
        where TKey : notnull
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void AssertNativeRoundTrip<T>(T value)
    {
        var converter = new ProtobufValueConverter<T>();
        var result = converter.FromBytes(new ReadOnlySequence<byte>(converter.ToBytes(value)));

        Assert.Equal(value, result);
    }

    private static void AssertNativeByteArrayRoundTrip(byte[] value)
    {
        var converter = new ProtobufValueConverter<byte[]>();
        var result = converter.FromBytes(new ReadOnlySequence<byte>(converter.ToBytes(value)));

        Assert.Equal(value, result);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
