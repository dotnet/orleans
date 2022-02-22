using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Globalization;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="CultureInfo"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class CultureInfoCodec : GeneralizedReferenceTypeSurrogateCodec<CultureInfo, CultureInfoSurrogate>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CultureInfoCodec"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public CultureInfoCodec(IValueSerializer<CultureInfoSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override CultureInfo ConvertFromSurrogate(ref CultureInfoSurrogate surrogate) => surrogate.Name switch
        {
            string name => new CultureInfo(name),
            null => null
        };

        /// <inheritdoc/>
        public override void ConvertToSurrogate(CultureInfo value, ref CultureInfoSurrogate surrogate) => surrogate = value switch
        {
            CultureInfo info => new CultureInfoSurrogate { Name = info.Name },
            null => default
        };
    }

    /// <summary>
    /// Surrogate type used by <see cref="CultureInfoCodec"/>.
    /// </summary>
    [GenerateSerializer]
    public struct CultureInfoSurrogate
    {
        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        /// <value>The name.</value>
        [Id(0)]
        public string Name { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="CultureInfo"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class CultureInfoCopier : IDeepCopier<CultureInfo>
    {
        /// <inheritdoc/>
        public CultureInfo DeepCopy(CultureInfo input, CopyContext _) => input;
    }
}