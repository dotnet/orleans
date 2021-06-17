using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class SortedSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<SortedSet<T>, SortedSetSurrogate<T>>
    {
        public SortedSetCodec(IValueSerializer<SortedSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override SortedSet<T> ConvertFromSurrogate(ref SortedSetSurrogate<T> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                if (surrogate.Comparer is object)
                {
                    return new SortedSet<T>(surrogate.Values, surrogate.Comparer);
                }
                else
                {
                    return new SortedSet<T>(surrogate.Values);
                }
            }
        }

        public override void ConvertToSurrogate(SortedSet<T> value, ref SortedSetSurrogate<T> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new SortedSetSurrogate<T>
                {
                    Values = new List<T>(value)
                };

                if (!ReferenceEquals(value.Comparer, Comparer<T>.Default))
                {
                    surrogate.Comparer = value.Comparer;
                }
            }
        }
    }

    [GenerateSerializer]
    public struct SortedSetSurrogate<T>
    {
        [Id(1)]
        public List<T> Values { get; set; }

        [Id(2)]
        public IComparer<T> Comparer { get; set; }
    }

    [RegisterCopier]
    public sealed class SortedSetCopier<T> : IDeepCopier<SortedSet<T>>, IBaseCopier<SortedSet<T>>
    {
        private readonly IDeepCopier<T> _elementCopier;

        public SortedSetCopier(IDeepCopier<T> elementCopier)
        {
            _elementCopier = elementCopier;
        }

        public SortedSet<T> DeepCopy(SortedSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<SortedSet<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(SortedSet<T>))
            {
                return context.Copy(input);
            }

            result = new SortedSet<T>(input.Comparer);
            context.RecordCopy(input, result);
            foreach (var element in input)
            {
                result.Add(_elementCopier.DeepCopy(element, context));
            }

            return result;
        }

        public void DeepCopy(SortedSet<T> input, SortedSet<T> output, CopyContext context)
        {
            foreach (var element in input)
            {
                output.Add(_elementCopier.DeepCopy(element, context));
            }
        }
    }
}
