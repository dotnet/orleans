using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class ArrayListCodec : GeneralizedReferenceTypeSurrogateCodec<ArrayList, ArrayListSurrogate>
    {
        public ArrayListCodec(IValueSerializer<ArrayListSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override ArrayList ConvertFromSurrogate(ref ArrayListSurrogate surrogate) => surrogate.Values switch
        {
            null => default,
            object => new ArrayList(surrogate.Values)
        };

        public override void ConvertToSurrogate(ArrayList value, ref ArrayListSurrogate surrogate)
        {
            if (value is null)
            {
                surrogate = default;
            }
            else
            {
                var result = new List<object>(value.Count);
                foreach (var item in value)
                {
                    result.Add(item);
                }

                surrogate = new ArrayListSurrogate
                {
                    Values = result
                };
            }
        }
    }

    [GenerateSerializer]
    public struct ArrayListSurrogate
    {
        [Id(1)]
        public List<object> Values { get; set; }
    }

    [RegisterCopier]
    public sealed class ArrayListCopier : IDeepCopier<ArrayList>, IBaseCopier<ArrayList>
    {
        private readonly IDeepCopier<object> _copier;
        public ArrayListCopier(IDeepCopier<object> copier)
        {
            _copier = copier;
        }

        public ArrayList DeepCopy(ArrayList input, CopyContext context)
        {
            if (context.TryGetCopy<ArrayList>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ArrayList))
            {
                return context.Copy(input);
            }

            result = new ArrayList(input.Count);
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Add(_copier.DeepCopy(item, context));
            }

            return result;
        }

        public void DeepCopy(ArrayList input, ArrayList output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Add(_copier.DeepCopy(item, context));
            }
        }
    }
}
