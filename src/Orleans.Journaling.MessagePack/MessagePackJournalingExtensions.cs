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
        builder.Services.AddSingleton<IStateMachineLogExtentCodec, MessagePackLogExtentCodec>();
        builder.Services.AddSingleton<MessagePackLogEntryCodecProvider>();
        builder.Services.AddSingleton<IDurableDictionaryCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableListCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableQueueCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableSetCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableValueCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableStateCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableTaskCompletionSourceCodecProvider>(static sp => sp.GetRequiredService<MessagePackLogEntryCodecProvider>());

        return builder;
    }
}

internal sealed class MessagePackLogEntryCodecProvider(MessagePackJournalingOptions options) :
    IDurableDictionaryCodecProvider,
    IDurableListCodecProvider,
    IDurableQueueCodecProvider,
    IDurableSetCodecProvider,
    IDurableValueCodecProvider,
    IDurableStateCodecProvider,
    IDurableTaskCompletionSourceCodecProvider
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    public IDurableDictionaryCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull
        => (IDurableDictionaryCodec<TKey, TValue>)_codecs.GetOrAdd(
            typeof(IDurableDictionaryCodec<TKey, TValue>),
            _ => new MessagePackDictionaryEntryCodec<TKey, TValue>(options.SerializerOptions));

    public IDurableListCodec<T> GetCodec<T>()
        => (IDurableListCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListCodec<T>),
            _ => new MessagePackListEntryCodec<T>(options.SerializerOptions));

    IDurableQueueCodec<T> IDurableQueueCodecProvider.GetCodec<T>()
        => (IDurableQueueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueCodec<T>),
            _ => new MessagePackQueueEntryCodec<T>(options.SerializerOptions));

    IDurableSetCodec<T> IDurableSetCodecProvider.GetCodec<T>()
        => (IDurableSetCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetCodec<T>),
            _ => new MessagePackSetEntryCodec<T>(options.SerializerOptions));

    IDurableValueCodec<T> IDurableValueCodecProvider.GetCodec<T>()
        => (IDurableValueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueCodec<T>),
            _ => new MessagePackValueEntryCodec<T>(options.SerializerOptions));

    IDurableStateCodec<T> IDurableStateCodecProvider.GetCodec<T>()
        => (IDurableStateCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateCodec<T>),
            _ => new MessagePackStateEntryCodec<T>(options.SerializerOptions));

    IDurableTaskCompletionSourceCodec<T> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceCodec<T>),
            _ => new MessagePackTcsEntryCodec<T>(options.SerializerOptions));
}
