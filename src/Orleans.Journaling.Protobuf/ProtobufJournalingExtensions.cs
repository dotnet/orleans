using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    /// Each entry type is serialized as a generated protobuf message. User values are wrapped in
    /// <see cref="Messages.TypedValue"/> which uses native protobuf encoding for well-known types
    /// (scalars and <see cref="Google.Protobuf.IMessage"/>) and falls back to
    /// <see cref="ILogDataCodec{T}"/> for all other types.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseProtobufCodec();
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufCodec(this ISiloBuilder builder)
    {
        builder.Services.AddSingleton<ProtobufLogEntryCodecProvider>();
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
/// protobuf encoding for well-known types and falls back to <see cref="ILogDataCodec{T}"/> only
/// when needed. No reflection (<c>MakeGenericType</c>, <c>GetGenericTypeDefinition</c>, etc.) is used.
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
    public ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>> GetCodec<TKey, TValue>() where TKey : notnull
        => (ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>)_codecs.GetOrAdd(
            typeof(DurableDictionaryEntry<TKey, TValue>),
            _ => new ProtobufDictionaryEntryCodec<TKey, TValue>(
                CreateConverter<TKey>(),
                CreateConverter<TValue>()));

    /// <inheritdoc/>
    public ILogEntryCodec<DurableListEntry<T>> GetCodec<T>()
        => (ILogEntryCodec<DurableListEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableListEntry<T>),
            _ => new ProtobufListEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableQueueEntry<T>> IDurableQueueCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableQueueEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableQueueEntry<T>),
            _ => new ProtobufQueueEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableSetEntry<T>> IDurableSetCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableSetEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableSetEntry<T>),
            _ => new ProtobufSetEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableValueEntry<T>> IDurableValueCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableValueEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableValueEntry<T>),
            _ => new ProtobufValueEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableStateEntry<T>> IDurableStateCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableStateEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableStateEntry<T>),
            _ => new ProtobufStateEntryCodec<T>(CreateConverter<T>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableTaskCompletionSourceEntry<T>> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableTaskCompletionSourceEntry<T>),
            _ => new ProtobufTcsEntryCodec<T>(CreateConverter<T>()));

    private ProtobufValueConverter<T> CreateConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
    {
        if (ProtobufValueConverter<T>.IsNativeType)
        {
            return new ProtobufValueConverter<T>();
        }

        var codec = serviceProvider.GetRequiredService<ILogDataCodec<T>>();
        return new ProtobufValueConverter<T>(codec);
    }
}
