using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class SortedDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<SortedDictionary<TKey, TValue>, SortedDictionarySurrogate<TKey, TValue>>
    {
        public SortedDictionaryCodec(IValueSerializer<SortedDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override SortedDictionary<TKey, TValue> ConvertFromSurrogate(ref SortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                SortedDictionary<TKey, TValue> result;
                if (surrogate.Comparer is object)
                {
                    result = new SortedDictionary<TKey, TValue>(surrogate.Comparer);
                }
                else
                {
                    result = new SortedDictionary<TKey, TValue>();
                }

                foreach (var kvp in surrogate.Values)
                {
                    result.Add(kvp.Key, kvp.Value);
                }

                return result;
            }
        }

        public override void ConvertToSurrogate(SortedDictionary<TKey, TValue> value, ref SortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new SortedDictionarySurrogate<TKey, TValue>
                {
                    Values = new List<KeyValuePair<TKey, TValue>>(value)
                };

                if (!ReferenceEquals(value.Comparer, Comparer<TKey>.Default))
                {
                    surrogate.Comparer = value.Comparer;
                }
            }
        }
    }

    [GenerateSerializer]
    public struct SortedDictionarySurrogate<TKey, TValue>
    {
        [Id(1)]
        public List<KeyValuePair<TKey, TValue>> Values { get; set; }

        [Id(2)]
        public IComparer<TKey> Comparer { get; set; }
    }

    [RegisterCopier]
    public sealed class SortedDictionaryCopier<TKey, TValue> : IDeepCopier<SortedDictionary<TKey, TValue>>, IBaseCopier<SortedDictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public SortedDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        public SortedDictionary<TKey, TValue> DeepCopy(SortedDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<SortedDictionary<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(SortedDictionary<TKey, TValue>))
            {
                return context.Copy(input);
            }

            result = new SortedDictionary<TKey, TValue>(input.Comparer);
            context.RecordCopy(input, result);
            foreach (var pair in input)
            {
                result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            return result;
        }

        public void DeepCopy(SortedDictionary<TKey, TValue> input, SortedDictionary<TKey, TValue> output, CopyContext context)
        {
            foreach (var pair in input)
            {
                output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }
        }
    }
}
