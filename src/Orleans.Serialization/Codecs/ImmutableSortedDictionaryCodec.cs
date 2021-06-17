using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ImmutableSortedDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ImmutableSortedDictionary<TKey, TValue>, ImmutableSortedDictionarySurrogate<TKey, TValue>>
    {
        public ImmutableSortedDictionaryCodec(IValueSerializer<ImmutableSortedDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ImmutableSortedDictionary<TKey, TValue> ConvertFromSurrogate(ref ImmutableSortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                var result = ImmutableSortedDictionary.CreateRange(surrogate.Values);

                if (surrogate.KeyComparer is object && surrogate.ValueComparer is object)
                {
                    result = result.WithComparers(surrogate.KeyComparer, surrogate.ValueComparer);
                }
                else if (surrogate.KeyComparer is object)
                {
                    result = result.WithComparers(surrogate.KeyComparer);
                }

                return result;
            }
        }

        public override void ConvertToSurrogate(ImmutableSortedDictionary<TKey, TValue> value, ref ImmutableSortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new ImmutableSortedDictionarySurrogate<TKey, TValue>
                {
                    Values = new List<KeyValuePair<TKey, TValue>>(value)
                };

                if (!ReferenceEquals(value.KeyComparer, Comparer<TKey>.Default))
                {
                    surrogate.KeyComparer = value.KeyComparer;
                }

                if (!ReferenceEquals(value.ValueComparer, EqualityComparer<TKey>.Default))
                {
                    surrogate.ValueComparer = value.ValueComparer;
                }
            }
        }
    }

    [GenerateSerializer]
    public struct ImmutableSortedDictionarySurrogate<TKey, TValue>
    {
        [Id(1)]
        public List<KeyValuePair<TKey, TValue>> Values { get; set; }

        [Id(2)]
        public IComparer<TKey> KeyComparer { get; set; }

        [Id(3)]
        public IEqualityComparer<TValue> ValueComparer { get; set; }
    }

    [RegisterCopier]
    public sealed class ImmutableSortedDictionaryCopier<TKey, TValue> : IDeepCopier<ImmutableSortedDictionary<TKey, TValue>>
    {
        public ImmutableSortedDictionary<TKey, TValue> DeepCopy(ImmutableSortedDictionary<TKey, TValue> input, CopyContext _) => input;
    }
}
