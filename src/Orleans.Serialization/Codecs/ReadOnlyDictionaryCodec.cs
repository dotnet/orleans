using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ReadOnlyDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class ReadOnlyDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ReadOnlyDictionary<TKey, TValue>, ReadOnlyDictionarySurrogate<TKey, TValue>> where TKey : notnull
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ReadOnlyDictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ReadOnlyDictionaryCodec(IValueSerializer<ReadOnlyDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override ReadOnlyDictionary<TKey, TValue> ConvertFromSurrogate(ref ReadOnlyDictionarySurrogate<TKey, TValue> surrogate) => new(surrogate.Values);

        /// <inheritdoc />
        public override void ConvertToSurrogate(ReadOnlyDictionary<TKey, TValue> value, ref ReadOnlyDictionarySurrogate<TKey, TValue> surrogate) => surrogate.Values = new(value);
    }

    /// <summary>
    /// Surrogate for <see cref="ReadOnlyDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct ReadOnlyDictionarySurrogate<TKey, TValue> where TKey : notnull
    {
        /// <summary>
        /// Gets or sets the dictionary entries.
        /// </summary>
        [Id(0)]
        public Dictionary<TKey, TValue> Values;
    }

    /// <summary>
    /// Copier for <see cref="ReadOnlyDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class ReadOnlyDictionaryCopier<TKey, TValue> : IDeepCopier<ReadOnlyDictionary<TKey, TValue>> where TKey : notnull
    {
        private readonly Type _fieldType = typeof(ReadOnlyDictionary<TKey, TValue>);
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReadOnlyDictionaryCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public ReadOnlyDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc />
        public ReadOnlyDictionary<TKey, TValue> DeepCopy(ReadOnlyDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ReadOnlyDictionary<TKey, TValue>>(input, out var result))
            {
                return result!;
            }

            if (input.GetType() as object != _fieldType as object)
            {
                return context.DeepCopy(input)!;
            }

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var temp = new Dictionary<TKey, TValue>(input.Count);
            foreach (var pair in input)
            {
                temp[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            result = new ReadOnlyDictionary<TKey, TValue>(temp);
            context.RecordCopy(input, result);
            return result;
        }
    }
}
