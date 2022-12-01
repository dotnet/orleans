using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    internal interface ICopierDescription
    {
        ITypeSymbol UnderlyingType { get; }
    }
}