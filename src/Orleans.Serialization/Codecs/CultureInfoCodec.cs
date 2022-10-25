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
        public override CultureInfo ConvertFromSurrogate(ref CultureInfoSurrogate surrogate) => new(surrogate.Name);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(CultureInfo value, ref CultureInfoSurrogate surrogate) => surrogate.Name = value.Name;
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
        public string Name;
    }

    [RegisterCopier]
    internal sealed class CultureInfoCopier : ShallowCopier<CultureInfo>, IDerivedTypeCopier { }
}