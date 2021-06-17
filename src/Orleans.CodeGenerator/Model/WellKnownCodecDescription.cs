using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    internal class WellKnownCodecDescription : ICodecDescription
    {
        public WellKnownCodecDescription(ITypeSymbol underlyingType, INamedTypeSymbol codecType)
        {
            UnderlyingType = underlyingType;
            CodecType = codecType;
        }

        public ITypeSymbol UnderlyingType { get; }

        public INamedTypeSymbol CodecType { get; }
    }

    internal class WellKnownCopierDescription : ICopierDescription
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