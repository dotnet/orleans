using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Orleans binary format implementation of the durable type codec providers.
/// </summary>
/// <remarks>
/// Each <c>GetCodec</c> method constructs the appropriate binary codec using <c>new</c> and
/// resolves <see cref="ILogDataCodec{T}"/> from DI — no reflection required. Codec instances
/// are cached per closed generic combination so they behave as singletons.
/// </remarks>
internal sealed class OrleansBinaryLogEntryCodecProvider(IServiceProvider serviceProvider) :
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
            _ => new OrleansBinaryDictionaryEntryCodec<TKey, TValue>(
                serviceProvider.GetRequiredService<ILogDataCodec<TKey>>(),
                serviceProvider.GetRequiredService<ILogDataCodec<TValue>>()));

    /// <inheritdoc/>
    public IDurableListCodec<T> GetCodec<T>()
        => (IDurableListCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListCodec<T>),
            _ => new OrleansBinaryListEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    IDurableQueueCodec<T> IDurableQueueCodecProvider.GetCodec<T>()
        => (IDurableQueueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueCodec<T>),
            _ => new OrleansBinaryQueueEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    IDurableSetCodec<T> IDurableSetCodecProvider.GetCodec<T>()
        => (IDurableSetCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetCodec<T>),
            _ => new OrleansBinarySetEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    IDurableValueCodec<T> IDurableValueCodecProvider.GetCodec<T>()
        => (IDurableValueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueCodec<T>),
            _ => new OrleansBinaryValueEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    IDurableStateCodec<T> IDurableStateCodecProvider.GetCodec<T>()
        => (IDurableStateCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateCodec<T>),
            _ => new OrleansBinaryStateEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    IDurableTaskCompletionSourceCodec<T> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceCodec<T>),
            _ => new OrleansBinaryTcsEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>(),
                serviceProvider.GetRequiredService<ILogDataCodec<Exception>>()));
}
