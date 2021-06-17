using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ImmutableListCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableList<T>, ImmutableListSurrogate<T>>
    {
        public ImmutableListCodec(IValueSerializer<ImmutableListSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ImmutableList<T> ConvertFromSurrogate(ref ImmutableListSurrogate<T> surrogate) => surrogate.Values switch
        {
            null => default,
            object => ImmutableList.CreateRange(surrogate.Values)
        };

        public override void ConvertToSurrogate(ImmutableList<T> value, ref ImmutableListSurrogate<T> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
            }
            else
            {
                surrogate = new ImmutableListSurrogate<T>
                {
                    Values = new List<T>(value)
                };
            }
        }
    }

    [GenerateSerializer]
    public struct ImmutableListSurrogate<T>
    {
        [Id(1)]
        public List<T> Values { get; set; }
    }

    [RegisterCopier]
    public sealed class ImmutableListCopier<T> : IDeepCopier<ImmutableList<T>>
    {
        public ImmutableList<T> DeepCopy(ImmutableList<T> input, CopyContext _) => input;
    }
}
