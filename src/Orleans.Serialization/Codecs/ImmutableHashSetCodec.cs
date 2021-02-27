using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ImmutableHashSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableHashSet<T>, ImmutableHashSetSurrogate<T>>
    {
        public ImmutableHashSetCodec(IValueSerializer<ImmutableHashSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ImmutableHashSet<T> ConvertFromSurrogate(ref ImmutableHashSetSurrogate<T> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                if (surrogate.KeyComparer is object)
                {
                    return ImmutableHashSet.CreateRange(surrogate.KeyComparer, surrogate.Values);
                }
                else
                {
                    return ImmutableHashSet.CreateRange(surrogate.Values);
                }
            }
        }

        public override void ConvertToSurrogate(ImmutableHashSet<T> value, ref ImmutableHashSetSurrogate<T> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new ImmutableHashSetSurrogate<T>
                {
                    Values = new List<T>(value)
                };

                if (!ReferenceEquals(value.KeyComparer, EqualityComparer<T>.Default))
                {
                    surrogate.KeyComparer = value.KeyComparer;
                }
            }
        }
    }

    [GenerateSerializer]
    public struct ImmutableHashSetSurrogate<T>
    {
        [Id(1)]
        public List<T> Values { get; set; }

        [Id(2)]
        public IEqualityComparer<T> KeyComparer { get; set; }
    }

    [RegisterCopier]
    public sealed class ImmutableHashSetCopier<T> : IDeepCopier<ImmutableHashSet<T>>
    {
        public ImmutableHashSet<T> DeepCopy(ImmutableHashSet<T> input, CopyContext context) => input;
    }
}
