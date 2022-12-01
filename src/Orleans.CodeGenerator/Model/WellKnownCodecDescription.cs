using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    internal sealed class WellKnownCodecDescription
    {
        public WellKnownCodecDescription(ITypeSymbol underlyingType, INamedTypeSymbol codecType)
        {
            UnderlyingType = underlyingType;
            CodecType = codecType;
        }

        public readonly ITypeSymbol UnderlyingType;
        public readonly INamedTypeSymbol CodecType;
    }

    internal sealed class WellKnownCopierDescription : ICopierDescription
    {
        public WellKnownCopierDescription(ITypeSymbol underlyingType, INamedTypeSymbol codecType)
        {
            UnderlyingType = underlyingType;
            CopierType = codecType;
        }

        public ITypeSymbol UnderlyingType { get; }

        public INamedTypeSymbol CopierType { get; }
    }
}