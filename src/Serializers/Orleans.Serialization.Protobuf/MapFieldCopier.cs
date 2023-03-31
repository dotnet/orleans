using Google.Protobuf.Collections;
using Orleans.Serialization.Cloning;

namespace Orleans.Serialization;

/// <summary>
/// Copier for <see cref="MapField{TKey, TValue}"/>.
/// </summary>
/// <typeparam name="TKey">The type of the t key.</typeparam>
/// <typeparam name="TValue">The type of the t value.</typeparam>
[RegisterCopier]
public sealed class MapFieldCopier<TKey, TValue> : IDeepCopier<MapField<TKey, TValue>>, IBaseCopier<MapField<TKey, TValue>>
{
    private readonly IDeepCopier<TKey> _keyCopier;
    private readonly IDeepCopier<TValue> _valueCopier;

    /// <summary>
    /// Initializes a new instance of the <see cref="MapFieldCopier{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="keyCopier">The key copier.</param>
    /// <param name="valueCopier">The value copier.</param>
    public MapFieldCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
    {
        _keyCopier = keyCopier;
        _valueCopier = valueCopier;
    }

    /// <inheritdoc/>
    public MapField<TKey, TValue> DeepCopy(MapField<TKey, TValue> input, CopyContext context)
    {
        if (context.TryGetCopy<MapField<TKey, TValue>>(input, out var result))
        {
            return result;
        }

        if (input.GetType() != typeof(MapField<TKey, TValue>))
        {
            return context.DeepCopy(input);
        }

        result = new MapField<TKey, TValue>();
        context.RecordCopy(input, result);
        foreach (var pair in input)
        {
            result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
        }

        return result;
    }

    /// <inheritdoc/>
    public void DeepCopy(MapField<TKey, TValue> input, MapField<TKey, TValue> output, CopyContext context)
    {
        foreach (var pair in input)
        {
            output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
        }
    }
}