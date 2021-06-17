using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.Serialization.Codecs
{
    [GenerateSerializer]
    public struct ReadOnlyCollectionSurrogate<T>
    {
        [Id(1)]
        public List<T> Values { get; set; }
    }

    [RegisterSerializer]
    public sealed class ReadOnlyCollectionCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ReadOnlyCollection<T>, ReadOnlyCollectionSurrogate<T>>
    {
        public ReadOnlyCollectionCodec(IValueSerializer<ReadOnlyCollectionSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ReadOnlyCollection<T> ConvertFromSurrogate(ref ReadOnlyCollectionSurrogate<T> surrogate) => surrogate.Values switch
        {
            object => new ReadOnlyCollection<T>(surrogate.Values),
            _ => null
        };

        public override void ConvertToSurrogate(ReadOnlyCollection<T> value, ref ReadOnlyCollectionSurrogate<T> surrogate)
        {
            switch (value)
            {
                case object:
                    surrogate = new ReadOnlyCollectionSurrogate<T>
                    {
                        Values = new List<T>(value)
                    };
                    break;
                default:
                    surrogate = default;
                    break;
            }
        }
    }

    [RegisterCopier]
    public sealed class ReadOnlyCollectionCopier<T> : IDeepCopier<ReadOnlyCollection<T>>
    {
        private readonly IDeepCopier<T> _elementCopier;

        public ReadOnlyCollectionCopier(IDeepCopier<T> elementCopier)
        {
            _elementCopier = elementCopier;
        }

        public ReadOnlyCollection<T> DeepCopy(ReadOnlyCollection<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ReadOnlyCollection<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ReadOnlyCollection<T>))
            {
                return context.Copy(input);
            }

            var tempResult = new T[input.Count];
            for (var i = 0; i < tempResult.Length; i++)
            {
                tempResult[i] = _elementCopier.DeepCopy(input[i], context);
            }

            result = new ReadOnlyCollection<T>(tempResult);
            context.RecordCopy(input, result);
            return result;
        }
    }
}