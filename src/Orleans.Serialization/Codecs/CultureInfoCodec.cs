using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Globalization;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class CultureInfoCodec : GeneralizedReferenceTypeSurrogateCodec<CultureInfo, CultureInfoSurrogate>
    {
        public CultureInfoCodec(IValueSerializer<CultureInfoSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override CultureInfo ConvertFromSurrogate(ref CultureInfoSurrogate surrogate) => surrogate.Name switch
        {
            string name => new CultureInfo(name),
            null => null
        };

        public override void ConvertToSurrogate(CultureInfo value, ref CultureInfoSurrogate surrogate) => surrogate = value switch
        {
            CultureInfo info => new CultureInfoSurrogate { Name = info.Name },
            null => default
        };
    }

    [GenerateSerializer]
    public struct CultureInfoSurrogate
    {
        [Id(0)]
        public string Name { get; set; }
    }

    [RegisterCopier]
    public sealed class CultureInfoCopier : IDeepCopier<CultureInfo>
    {
        public CultureInfo DeepCopy(CultureInfo input, CopyContext _) => input;
    }
}