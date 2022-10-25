using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Version"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class VersionCodec : GeneralizedReferenceTypeSurrogateCodec<Version, VersionSurrogate>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VersionCodec"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public VersionCodec(IValueSerializer<VersionSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override Version ConvertFromSurrogate(ref VersionSurrogate surrogate)
        {
            int revision = surrogate.Revision;
            int build = surrogate.Build;

            // ArgumentOutOfRangeException is thrown if any argument is less than zero
            // Build and Revision are -1 if they are not defined during construction
            if (revision != -1)
            {
                return new Version(surrogate.Major, surrogate.Minor, build, revision);
            }
            else if (build != -1)
            {
                return new Version(surrogate.Major, surrogate.Minor, build);
            }
            else
            {
                return new Version(surrogate.Major, surrogate.Minor);
            }
        }

        /// <inheritdoc />
        public override void ConvertToSurrogate(Version value, ref VersionSurrogate surrogate)
        {
            surrogate.Major = value.Major;
            surrogate.Minor = value.Minor;
            surrogate.Build = value.Build;
            surrogate.Revision = value.Revision;
        }
    }

    /// <summary>
    /// Surrogate type for <see cref="VersionCodec"/>.
    /// </summary>
    [GenerateSerializer]
    public struct VersionSurrogate
    {
        /// <summary>
        /// Gets or sets the major version component.
        /// </summary>
        /// <value>The major version component.</value>
        [Id(0)]
        public int Major;

        /// <summary>
        /// Gets or sets the minor version component.
        /// </summary>
        /// <value>The minor version component.</value>
        [Id(1)]
        public int Minor;

        /// <summary>
        /// Gets or sets the build number.
        /// </summary>
        /// <value>The build number.</value>
        [Id(2)]
        public int Build;

        /// <summary>
        /// Gets or sets the revision.
        /// </summary>
        /// <value>The revision.</value>
        [Id(3)]
        public int Revision;
    }
}