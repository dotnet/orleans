using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    internal interface ICodecDescription
    {
        ITypeSymbol UnderlyingType { get; }
    }

    internal interface ICopierDescription
    {
        ITypeSymbol UnderlyingType { get; }
    }
}