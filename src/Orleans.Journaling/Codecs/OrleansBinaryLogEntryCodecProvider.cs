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
    public ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>> GetCodec<TKey, TValue>() where TKey : notnull
        => (ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>)_codecs.GetOrAdd(
            typeof(DurableDictionaryEntry<TKey, TValue>),
            _ => new OrleansBinaryDictionaryEntryCodec<TKey, TValue>(
                serviceProvider.GetRequiredService<ILogDataCodec<TKey>>(),
                serviceProvider.GetRequiredService<ILogDataCodec<TValue>>()));

    /// <inheritdoc/>
    public ILogEntryCodec<DurableListEntry<T>> GetCodec<T>()
        => (ILogEntryCodec<DurableListEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableListEntry<T>),
            _ => new OrleansBinaryListEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableQueueEntry<T>> IDurableQueueCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableQueueEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableQueueEntry<T>),
            _ => new OrleansBinaryQueueEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableSetEntry<T>> IDurableSetCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableSetEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableSetEntry<T>),
            _ => new OrleansBinarySetEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableValueEntry<T>> IDurableValueCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableValueEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableValueEntry<T>),
            _ => new OrleansBinaryValueEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableStateEntry<T>> IDurableStateCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableStateEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableStateEntry<T>),
            _ => new OrleansBinaryStateEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>()));

    /// <inheritdoc/>
    ILogEntryCodec<DurableTaskCompletionSourceEntry<T>> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableTaskCompletionSourceEntry<T>),
            _ => new OrleansBinaryTcsEntryCodec<T>(
                serviceProvider.GetRequiredService<ILogDataCodec<T>>(),
                serviceProvider.GetRequiredService<ILogDataCodec<Exception>>()));
}
