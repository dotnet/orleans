using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

internal sealed class WellKnownCodecDescription(ITypeSymbol? underlyingType, INamedTypeSymbol? codecType)
{
    public readonly ITypeSymbol UnderlyingType = underlyingType!;
    public readonly INamedTypeSymbol CodecType = codecType!;
}

internal sealed class WellKnownCopierDescription(ITypeSymbol underlyingType, INamedTypeSymbol codecType) : ICopierDescription
{
    public ITypeSymbol UnderlyingType { get; } = underlyingType;

    public INamedTypeSymbol CopierType { get; } = codecType;
}
