using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ImmutableSortedSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableSortedSet<T>, ImmutableSortedSetSurrogate<T>>
    {
        public ImmutableSortedSetCodec(IValueSerializer<ImmutableSortedSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ImmutableSortedSet<T> ConvertFromSurrogate(ref ImmutableSortedSetSurrogate<T> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                if (surrogate.KeyComparer is object)
                {
                    return ImmutableSortedSet.CreateRange(surrogate.KeyComparer, surrogate.Values);
                }
                else
                {
                    return ImmutableSortedSet.CreateRange(surrogate.Values);
                }
            }
        }

        public override void ConvertToSurrogate(ImmutableSortedSet<T> value, ref ImmutableSortedSetSurrogate<T> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new ImmutableSortedSetSurrogate<T>
                {
                    Values = new List<T>(value)
                };

                if (!ReferenceEquals(value.KeyComparer, Comparer<T>.Default))
                {
                    surrogate.KeyComparer = value.KeyComparer;
                }
            }
        }
    }

    [GenerateSerializer]
    public struct ImmutableSortedSetSurrogate<T>
    {
        [Id(1)]
        public List<T> Values { get; set; }

        [Id(2)]
        public IComparer<T> KeyComparer { get; set; }
    }

    [RegisterCopier]
    public sealed class ImmutableSortedSetCopier<T> : IDeepCopier<ImmutableSortedSet<T>>
    {
        public ImmutableSortedSet<T> DeepCopy(ImmutableSortedSet<T> input, CopyContext _) => input;
    }
}
