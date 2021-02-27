using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ConcurrentDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ConcurrentDictionary<TKey, TValue>, ConcurrentDictionarySurrogate<TKey, TValue>>
    {
        public ConcurrentDictionaryCodec(IValueSerializer<ConcurrentDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ConcurrentDictionary<TKey, TValue> ConvertFromSurrogate(ref ConcurrentDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                // Order of the key-value pairs in the return value may not match the order of the key-value pairs in the surrogate
                return new ConcurrentDictionary<TKey, TValue>(surrogate.Values);
            }
        }

        public override void ConvertToSurrogate(ConcurrentDictionary<TKey, TValue> value, ref ConcurrentDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new ConcurrentDictionarySurrogate<TKey, TValue>
                {
                    Values = new Dictionary<TKey, TValue>(value)
                };
            }
        }
    }

    [GenerateSerializer]
    public struct ConcurrentDictionarySurrogate<TKey, TValue>
    {
        [Id(1)]
        public Dictionary<TKey, TValue> Values { get; set; }
    }

    [RegisterCopier]
    public sealed class ConcurrentDictionaryCopier<TKey, TValue> : IDeepCopier<ConcurrentDictionary<TKey, TValue>>, IBaseCopier<ConcurrentDictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public ConcurrentDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        public ConcurrentDictionary<TKey, TValue> DeepCopy(ConcurrentDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ConcurrentDictionary<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ConcurrentDictionary<TKey, TValue>))
            {
                return context.Copy(input);
            }

            // Note that this cannot propagate the input's key comparer, since it is not exposed from ConcurrentDictionary.
            result = new ConcurrentDictionary<TKey, TValue>();
            context.RecordCopy(input, result);
            foreach (var pair in input)
            {
                result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            return result;
        }

        public void DeepCopy(ConcurrentDictionary<TKey, TValue> input, ConcurrentDictionary<TKey, TValue> output, CopyContext context)
        {
            foreach (var pair in input)
            {
                output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }
        }
    }
}
