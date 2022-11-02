using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ReadOnlyDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ReadOnlyDictionary<TKey, TValue>, ReadOnlyDictionarySurrogate<TKey, TValue>>
    {
        public ReadOnlyDictionaryCodec(IValueSerializer<ReadOnlyDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ReadOnlyDictionary<TKey, TValue> ConvertFromSurrogate(ref ReadOnlyDictionarySurrogate<TKey, TValue> surrogate) => new(surrogate.Values);

        public override void ConvertToSurrogate(ReadOnlyDictionary<TKey, TValue> value, ref ReadOnlyDictionarySurrogate<TKey, TValue> surrogate) => surrogate.Values = new(value);
    }

    [GenerateSerializer]
    public struct ReadOnlyDictionarySurrogate<TKey, TValue>
    {
        [Id(0)]
        public Dictionary<TKey, TValue> Values;
    }

    [RegisterCopier]
    public sealed class ReadOnlyDictionaryCopier<TKey, TValue> : IDeepCopier<ReadOnlyDictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public ReadOnlyDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        public ReadOnlyDictionary<TKey, TValue> DeepCopy(ReadOnlyDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ReadOnlyDictionary<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ReadOnlyDictionary<TKey, TValue>))
            {
                return context.DeepCopy(input);
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
