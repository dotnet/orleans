using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ImmutableQueueCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableQueue<T>, ImmutableQueueSurrogate<T>>
    {
        public ImmutableQueueCodec(IValueSerializer<ImmutableQueueSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ImmutableQueue<T> ConvertFromSurrogate(ref ImmutableQueueSurrogate<T> surrogate) => surrogate.Values switch
        {
            null => null,
            object => ImmutableQueue.CreateRange(surrogate.Values)
        };

        public override void ConvertToSurrogate(ImmutableQueue<T> value, ref ImmutableQueueSurrogate<T> surrogate) => surrogate = value switch
        {
            null => default, 
            object => new ImmutableQueueSurrogate<T>
            {
                Values = new List<T>(value)
            }
        };
    }

    [GenerateSerializer]
    public struct ImmutableQueueSurrogate<T>
    {
        [Id(1)]
        public List<T> Values { get; set; }
    }

    [RegisterCopier]
    public sealed class ImmutableQueueCopier<T> : IDeepCopier<ImmutableQueue<T>>
    {
        public ImmutableQueue<T> DeepCopy(ImmutableQueue<T> input, CopyContext _) => input;
    }
}