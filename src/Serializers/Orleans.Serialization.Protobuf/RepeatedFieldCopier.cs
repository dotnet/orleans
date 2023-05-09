using Google.Protobuf.Collections;
using Orleans.Serialization.Cloning;

namespace Orleans.Serialization;

/// <summary>
/// Copier for <see cref="RepeatedField{T}"/>.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
[RegisterCopier]
public sealed class RepeatedFieldCopier<T> : IDeepCopier<RepeatedField<T>>, IBaseCopier<RepeatedField<T>>
{
    private readonly IDeepCopier<T> _copier;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepeatedFieldCopier{T}"/> class.
    /// </summary>
    /// <param name="valueCopier">The value copier.</param>
    public RepeatedFieldCopier(IDeepCopier<T> valueCopier)
    {
        _copier = valueCopier;
    }

    /// <inheritdoc/>
    public RepeatedField<T> DeepCopy(RepeatedField<T> input, CopyContext context)
    {
        if (context.TryGetCopy<RepeatedField<T>>(input, out var result))
        {
            return result;
        }

        if (input.GetType() != typeof(RepeatedField<T>))
        {
            return context.DeepCopy(input);
        }

        result = new RepeatedField<T> { Capacity = input.Count };
        context.RecordCopy(input, result);
        foreach (var item in input)
        {
            result.Add(_copier.DeepCopy(item, context));
        }

        return result;
    }

    /// <inheritdoc/>
    public void DeepCopy(RepeatedField<T> input, RepeatedField<T> output, CopyContext context)
    {
        foreach (var item in input)
        {
            output.Add(_copier.DeepCopy(item, context));
        }
    }
}