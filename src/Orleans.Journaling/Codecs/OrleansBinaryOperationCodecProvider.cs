using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Orleans binary format implementation of the durable type codec providers.
/// </summary>
/// <remarks>
/// Each <c>GetCodec</c> method constructs the appropriate binary codec using <c>new</c> and
/// resolves <see cref="ILogValueCodec{T}"/> from DI — no reflection required. Codec instances
/// are cached per closed generic combination so they behave as singletons.
/// </remarks>
internal sealed class OrleansBinaryOperationCodecProvider(IServiceProvider serviceProvider) :
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
            _ => new OrleansBinaryDictionaryOperationCodec<TKey, TValue>(
                serviceProvider.GetRequiredService<ILogValueCodec<TKey>>(),
                serviceProvider.GetRequiredService<ILogValueCodec<TValue>>()));

    /// <inheritdoc/>
    public IDurableListOperationCodec<T> GetCodec<T>()
        => (IDurableListOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListOperationCodec<T>),
            _ => new OrleansBinaryListOperationCodec<T>(
                serviceProvider.GetRequiredService<ILogValueCodec<T>>()));

    /// <inheritdoc/>
    IDurableQueueOperationCodec<T> IDurableQueueOperationCodecProvider.GetCodec<T>()
        => (IDurableQueueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueOperationCodec<T>),
            _ => new OrleansBinaryQueueOperationCodec<T>(
                serviceProvider.GetRequiredService<ILogValueCodec<T>>()));

    /// <inheritdoc/>
    IDurableSetOperationCodec<T> IDurableSetOperationCodecProvider.GetCodec<T>()
        => (IDurableSetOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetOperationCodec<T>),
            _ => new OrleansBinarySetOperationCodec<T>(
                serviceProvider.GetRequiredService<ILogValueCodec<T>>()));

    /// <inheritdoc/>
    IDurableValueOperationCodec<T> IDurableValueOperationCodecProvider.GetCodec<T>()
        => (IDurableValueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueOperationCodec<T>),
            _ => new OrleansBinaryValueOperationCodec<T>(
                serviceProvider.GetRequiredService<ILogValueCodec<T>>()));

    /// <inheritdoc/>
    IDurableStateOperationCodec<T> IDurableStateOperationCodecProvider.GetCodec<T>()
        => (IDurableStateOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateOperationCodec<T>),
            _ => new OrleansBinaryStateOperationCodec<T>(
                serviceProvider.GetRequiredService<ILogValueCodec<T>>()));

    /// <inheritdoc/>
    IDurableTaskCompletionSourceOperationCodec<T> IDurableTaskCompletionSourceOperationCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceOperationCodec<T>),
            _ => new OrleansBinaryTcsOperationCodec<T>(
                serviceProvider.GetRequiredService<ILogValueCodec<T>>(),
                serviceProvider.GetRequiredService<ILogValueCodec<Exception>>()));
}
