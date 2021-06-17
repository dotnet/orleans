using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class VersionCodec : GeneralizedReferenceTypeSurrogateCodec<Version, VersionSurrogate>
    {
        public VersionCodec(IValueSerializer<VersionSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

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

        public override void ConvertToSurrogate(Version value, ref VersionSurrogate surrogate) => 
            surrogate = new VersionSurrogate { Major = value.Major, Minor = value.Minor, Build = value.Build, Revision = value.Revision };
    }

    [GenerateSerializer]
    public struct VersionSurrogate
    {
        [Id(0)]
        public int Major { get; set; }

        [Id(1)]
        public int Minor { get; set; }

        [Id(2)]
        public int Build { get; set; }

        [Id(3)]
        public int Revision { get; set; }
    }

    [RegisterCopier]
    public sealed class VersionCopier : IDeepCopier<Version>
    {
        public Version DeepCopy(Version input, CopyContext context) => input;
    }
}