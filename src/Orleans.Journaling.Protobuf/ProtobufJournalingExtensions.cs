using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Extension methods for configuring Protocol Buffers-based serialization for Orleans.Journaling.
/// </summary>
public static class ProtobufJournalingExtensions
{
    /// <summary>
    /// Configures Orleans.Journaling to use Google Protocol Buffers wire format for log entry serialization.
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
    /// use native protobuf payload encoding. Other user values fall back to <see cref="ILogDataCodec{T}"/>
    /// unless native protobuf message encoding is configured using
    /// <see cref="ProtobufJournalingOptions.AddMessageParser{T}(Google.Protobuf.MessageParser{T})"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseProtobufCodec();
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufCodec(this ISiloBuilder builder)
        => UseProtobufCodecCore(builder, configure: null);

    /// <summary>
    /// Configures Orleans.Journaling to use Google Protocol Buffers wire format for log entry serialization.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">A delegate used to configure protobuf journaling options.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// Use <see cref="ProtobufJournalingOptions.AddMessageParser{T}(Google.Protobuf.MessageParser{T})"/>
    /// to register generated protobuf message parsers for native, reflection-free message value encoding.
    /// Unregistered message values fall back to <see cref="ILogDataCodec{T}"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseProtobufCodec(options =>
    /// {
    ///     options.AddMessageParser(MyMessage.Parser);
    /// });
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufCodec(this ISiloBuilder builder, Action<ProtobufJournalingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseProtobufCodecCore(builder, configure);
    }

    private static ISiloBuilder UseProtobufCodecCore(ISiloBuilder builder, Action<ProtobufJournalingOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ProtobufBuiltInValueCodecs.AddTo(builder.Services);
        if (configure is not null)
        {
            var options = new ProtobufJournalingOptions();
            configure(options);
            options.Apply(builder.Services);
        }

        builder.Services.AddSingleton<ProtobufLogFormat>();
        builder.Services.AddKeyedSingleton<IStateMachineLogFormat>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogFormat>());
        builder.Services.AddSingleton<IStateMachineLogFormat>(static sp => sp.GetRequiredService<ProtobufLogFormat>());
        builder.Services.AddSingleton<ProtobufLogEntryCodecProvider>();
        builder.Services.AddKeyedSingleton<IDurableDictionaryCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddKeyedSingleton<IDurableListCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddKeyedSingleton<IDurableQueueCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddKeyedSingleton<IDurableSetCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddKeyedSingleton<IDurableValueCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddKeyedSingleton<IDurableStateCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddKeyedSingleton<IDurableTaskCompletionSourceCodecProvider>(StateMachineLogFormatKeys.Protobuf, static (sp, _) => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableDictionaryCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableListCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableQueueCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableSetCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableValueCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableStateCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableTaskCompletionSourceCodecProvider>(static sp => sp.GetRequiredService<ProtobufLogEntryCodecProvider>());

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
/// <see cref="ILogDataCodec{T}"/> only when needed. No reflection (<c>MakeGenericType</c>,
/// <c>GetGenericTypeDefinition</c>, parser property lookup, etc.) is used.
/// </para>
/// </remarks>
internal sealed class ProtobufLogEntryCodecProvider(IServiceProvider serviceProvider) :
    IDurableDictionaryCodecProvider,
    IDurableListCodecProvider,
    IDurableQueueCodecProvider,
    IDurableSetCodecProvider,
    IDurableValueCodecProvider,
    IDurableStateCodecProvider,
    IDurableTaskCompletionSourceCodecProvider
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    /// <inheritdoc/>
    public IDurableDictionaryCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull
        => (IDurableDictionaryCodec<TKey, TValue>)_codecs.GetOrAdd(
            typeof(IDurableDictionaryCodec<TKey, TValue>),
            _ => new ProtobufDictionaryEntryCodec<TKey, TValue>(
                CreateConverter<TKey>(),
                CreateConverter<TValue>()));

    /// <inheritdoc/>
    public IDurableListCodec<T> GetCodec<T>()
        => (IDurableListCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListCodec<T>),
            _ => new ProtobufListEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableQueueCodec<T> IDurableQueueCodecProvider.GetCodec<T>()
        => (IDurableQueueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueCodec<T>),
            _ => new ProtobufQueueEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableSetCodec<T> IDurableSetCodecProvider.GetCodec<T>()
        => (IDurableSetCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetCodec<T>),
            _ => new ProtobufSetEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableValueCodec<T> IDurableValueCodecProvider.GetCodec<T>()
        => (IDurableValueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueCodec<T>),
            _ => new ProtobufValueEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableStateCodec<T> IDurableStateCodecProvider.GetCodec<T>()
        => (IDurableStateCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateCodec<T>),
            _ => new ProtobufStateEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    IDurableTaskCompletionSourceCodec<T> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceCodec<T>),
            _ => new ProtobufTcsEntryCodec<T>(CreateConverter<T>()));

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

        var codec = serviceProvider.GetService<ILogDataCodec<T>>();
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
                + $"Register the generated parser with UseProtobufCodec(options => options.AddMessageParser({typeof(T).Name}.Parser)) "
                + $"or register {nameof(ILogDataCodec<T>)}.");
        }

        return new InvalidOperationException(
            $"Protocol Buffers journaling does not have a native value codec for type '{typeName}' and no {nameof(ILogDataCodec<T>)} fallback was registered. "
            + $"Use a supported native protobuf value type or register {nameof(ILogDataCodec<T>)}.");
    }
}
