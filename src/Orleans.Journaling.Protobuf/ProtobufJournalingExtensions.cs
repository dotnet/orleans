using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Extension methods for configuring the Protocol Buffers Orleans.Journaling format.
/// </summary>
public static class ProtobufJournalingExtensions
{
    /// <summary>
    /// The well-known key for the Protocol Buffers log format.
    /// </summary>
    public const string LogFormatKey = "protobuf";

    /// <summary>
    /// Configures this silo with the Protocol Buffers Orleans.Journaling format family.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Physical log data is a stream of length-delimited <c>LogEntry</c> messages:
    /// <c>message LogEntry { uint64 stream_id = 1; bytes payload = 2; }</c>.
    /// The durable entry payload is encoded with protobuf wire-format tags.
    /// </para>
    /// <para>
    /// Each entry type is serialized using protobuf wire-format tags. Common scalar values
    /// (<see cref="string"/>, byte arrays, numeric primitives, and <see cref="bool"/>)
    /// use native protobuf payload encoding. Other user values fall back to <see cref="ILogValueCodec{T}"/>
    /// unless native protobuf message encoding is configured using
    /// <see cref="ProtobufJournalingOptions.AddMessageParser{T}(Google.Protobuf.MessageParser{T})"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddLogStorage().UseProtobufJournalingFormat();
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufJournalingFormat(this ISiloBuilder builder)
        => UseProtobufJournalingFormatCore(builder, configure: null);

    /// <summary>
    /// Configures this silo with the Protocol Buffers Orleans.Journaling format family.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">A delegate used to configure protobuf journaling options.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// Use <see cref="ProtobufJournalingOptions.AddMessageParser{T}(Google.Protobuf.MessageParser{T})"/>
    /// to register generated protobuf message parsers for native, reflection-free message value encoding.
    /// Unregistered message values fall back to <see cref="ILogValueCodec{T}"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddLogStorage().UseProtobufJournalingFormat(options =>
    /// {
    ///     options.AddMessageParser(MyMessage.Parser);
    /// });
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufJournalingFormat(this ISiloBuilder builder, Action<ProtobufJournalingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseProtobufJournalingFormatCore(builder, configure);
    }

    private static ISiloBuilder UseProtobufJournalingFormatCore(ISiloBuilder builder, Action<ProtobufJournalingOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ProtobufBuiltInValueCodecs.AddTo(builder.Services);
        if (configure is not null)
        {
            var options = new ProtobufJournalingOptions();
            configure(options);
            options.Apply(builder.Services);
        }

        builder.Services
            .AddJournalingFormatFamily(LogFormatKey)
            .AddLogFormat<ProtobufLogFormat>()
            .AddOperationCodecProvider<ProtobufOperationCodecProvider>();

        return builder;
    }
}

/// <summary>
/// Protocol Buffers format implementation of the durable type codec providers.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>GetCodec</c> method constructs the appropriate protobuf codec using <c>new</c>.
/// For each type argument, a <see cref="ProtobufValueConverter{T}"/> is created that uses native
/// protobuf encoding for built-in and explicitly registered types and falls back to
/// <see cref="ILogValueCodec{T}"/> only when needed. No reflection (<c>MakeGenericType</c>,
/// <c>GetGenericTypeDefinition</c>, parser property lookup, etc.) is used.
/// </para>
/// </remarks>
internal sealed class ProtobufOperationCodecProvider(IServiceProvider serviceProvider) :
    IDurableDictionaryOperationCodecProvider,
    IDurableListOperationCodecProvider,
    IDurableQueueOperationCodecProvider,
    IDurableSetOperationCodecProvider,
    IDurableValueOperationCodecProvider,
    IDurableStateOperationCodecProvider,
    IDurableTaskCompletionSourceOperationCodecProvider
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    /// <inheritdoc/>
    public IDurableDictionaryOperationCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull
        => (IDurableDictionaryOperationCodec<TKey, TValue>)_codecs.GetOrAdd(
            typeof(IDurableDictionaryOperationCodec<TKey, TValue>),
            _ => new ProtobufDictionaryOperationCodec<TKey, TValue>(
                CreateConverter<TKey>(),
                CreateConverter<TValue>()));

    /// <inheritdoc/>
    public IDurableListOperationCodec<T> GetCodec<T>()
        => (IDurableListOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListOperationCodec<T>),
            _ => new ProtobufListOperationCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableQueueOperationCodec<T> IDurableQueueOperationCodecProvider.GetCodec<T>()
        => (IDurableQueueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueOperationCodec<T>),
            _ => new ProtobufQueueOperationCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableSetOperationCodec<T> IDurableSetOperationCodecProvider.GetCodec<T>()
        => (IDurableSetOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetOperationCodec<T>),
            _ => new ProtobufSetOperationCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableValueOperationCodec<T> IDurableValueOperationCodecProvider.GetCodec<T>()
        => (IDurableValueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueOperationCodec<T>),
            _ => new ProtobufValueOperationCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableStateOperationCodec<T> IDurableStateOperationCodecProvider.GetCodec<T>()
        => (IDurableStateOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateOperationCodec<T>),
            _ => new ProtobufStateOperationCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableTaskCompletionSourceOperationCodec<T> IDurableTaskCompletionSourceOperationCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceOperationCodec<T>),
            _ => new ProtobufTcsOperationCodec<T>(CreateConverter<T>()));

    private ProtobufValueConverter<T> CreateConverter<T>()
    {
        var nativeCodec = serviceProvider.GetService<IProtobufValueCodec<T>>();
        if (nativeCodec is not null)
        {
            return new ProtobufValueConverter<T>(nativeCodec);
        }

        if (ProtobufValueConverter<T>.IsNativeType)
        {
            return new ProtobufValueConverter<T>();
        }

        var codec = serviceProvider.GetService<ILogValueCodec<T>>();
        if (codec is not null)
        {
            return new ProtobufValueConverter<T>(codec);
        }

        throw CreateMissingValueCodecException<T>();
    }

    private static InvalidOperationException CreateMissingValueCodecException<T>()
    {
        var typeName = typeof(T).FullName ?? typeof(T).Name;
        if (typeof(IMessage).IsAssignableFrom(typeof(T)))
        {
            return new InvalidOperationException(
                $"Protocol Buffers journaling does not have a native parser or fallback codec for message type '{typeName}'. "
                + $"Register the generated parser with UseProtobufJournalingFormat(options => options.AddMessageParser({typeof(T).Name}.Parser)) "
                + $"or register {nameof(ILogValueCodec<T>)}.");
        }

        return new InvalidOperationException(
            $"Protocol Buffers journaling does not have a native value codec for type '{typeName}' and no {nameof(ILogValueCodec<T>)} fallback was registered. "
            + $"Use a supported native protobuf value type or register {nameof(ILogValueCodec<T>)}.");
    }
}
