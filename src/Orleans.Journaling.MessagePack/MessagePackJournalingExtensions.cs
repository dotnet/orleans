using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.MessagePack;

/// <summary>
/// Extension methods for configuring MessagePack-based serialization for Orleans.Journaling.
/// </summary>
public static class MessagePackJournalingExtensions
{
    /// <summary>
    /// The well-known key for the MessagePack log format.
    /// </summary>
    public const string LogFormatKey = "messagepack";

    /// <summary>
    /// Configures Orleans.Journaling to use MessagePack for durable log entry serialization.
    /// </summary>
    public static ISiloBuilder UseMessagePackCodec(this ISiloBuilder builder)
        => UseMessagePackCodecCore(builder, configure: null);

    /// <summary>
    /// Configures Orleans.Journaling to use MessagePack for durable log entry serialization.
    /// </summary>
    public static ISiloBuilder UseMessagePackCodec(this ISiloBuilder builder, Action<MessagePackJournalingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseMessagePackCodecCore(builder, configure);
    }

    private static ISiloBuilder UseMessagePackCodecCore(ISiloBuilder builder, Action<MessagePackJournalingOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MessagePackJournalingOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);
        builder.Services
            .AddJournalingFormatFamily(LogFormatKey)
            .AddLogFormat<MessagePackLogFormat>()
            .AddOperationCodecProvider<MessagePackOperationCodecProvider>();

        return builder;
    }
}

internal sealed class MessagePackOperationCodecProvider(MessagePackJournalingOptions options) :
    IDurableDictionaryOperationCodecProvider,
    IDurableListOperationCodecProvider,
    IDurableQueueOperationCodecProvider,
    IDurableSetOperationCodecProvider,
    IDurableValueOperationCodecProvider,
    IDurableStateOperationCodecProvider,
    IDurableTaskCompletionSourceOperationCodecProvider
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    public IDurableDictionaryOperationCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull
        => (IDurableDictionaryOperationCodec<TKey, TValue>)_codecs.GetOrAdd(
            typeof(IDurableDictionaryOperationCodec<TKey, TValue>),
            _ => new MessagePackDictionaryOperationCodec<TKey, TValue>(options.SerializerOptions));

    public IDurableListOperationCodec<T> GetCodec<T>()
        => (IDurableListOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListOperationCodec<T>),
            _ => new MessagePackListOperationCodec<T>(options.SerializerOptions));

    IDurableQueueOperationCodec<T> IDurableQueueOperationCodecProvider.GetCodec<T>()
        => (IDurableQueueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueOperationCodec<T>),
            _ => new MessagePackQueueOperationCodec<T>(options.SerializerOptions));

    IDurableSetOperationCodec<T> IDurableSetOperationCodecProvider.GetCodec<T>()
        => (IDurableSetOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetOperationCodec<T>),
            _ => new MessagePackSetOperationCodec<T>(options.SerializerOptions));

    IDurableValueOperationCodec<T> IDurableValueOperationCodecProvider.GetCodec<T>()
        => (IDurableValueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueOperationCodec<T>),
            _ => new MessagePackValueOperationCodec<T>(options.SerializerOptions));

    IDurableStateOperationCodec<T> IDurableStateOperationCodecProvider.GetCodec<T>()
        => (IDurableStateOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateOperationCodec<T>),
            _ => new MessagePackStateOperationCodec<T>(options.SerializerOptions));

    IDurableTaskCompletionSourceOperationCodec<T> IDurableTaskCompletionSourceOperationCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceOperationCodec<T>),
            _ => new MessagePackTcsOperationCodec<T>(options.SerializerOptions));
}
