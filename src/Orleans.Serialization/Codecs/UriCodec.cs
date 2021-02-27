using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;
using System.ComponentModel;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class UriCodec : GeneralizedReferenceTypeSurrogateCodec<Uri, UriSurrogate>
    {
        private readonly TypeConverter _uriConverter = TypeDescriptor.GetConverter(typeof(Uri));

        public UriCodec(IValueSerializer<UriSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override Uri ConvertFromSurrogate(ref UriSurrogate surrogate) => surrogate.Value switch
        {
            null => null,
            _ => (Uri)_uriConverter.ConvertFromInvariantString(surrogate.Value)
        };

        public override void ConvertToSurrogate(Uri value, ref UriSurrogate surrogate) => surrogate = value switch
        {
            null => default,
            _ => new UriSurrogate
            {
                Value = _uriConverter.ConvertToInvariantString(value)
            },
        };
    }

    [GenerateSerializer]
    public struct UriSurrogate
    {
        [Id(1)]
        public string Value { get; set; }
    }

    [RegisterCopier]
    public sealed class UriCopier : IDeepCopier<Uri>, IGeneralizedCopier
    {
        public Uri DeepCopy(Uri input, CopyContext context) => input;
        public object DeepCopy(object input, CopyContext context) => input;
        public bool IsSupportedType(Type type) => typeof(Uri).IsAssignableFrom(type);
    }
}