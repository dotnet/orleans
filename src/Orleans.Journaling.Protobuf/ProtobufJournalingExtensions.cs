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
    /// Each entry type is serialized using protobuf wire-format tags. User values use native
    /// protobuf payload encoding where supported and fall back to <see cref="ILogDataCodec{T}"/>.
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
