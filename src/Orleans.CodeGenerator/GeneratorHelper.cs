namespace Orleans.CodeGenerator;

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SyntaxGeneration.SymbolExtensions;
internal static class GeneratorHelper
{
    internal static uint? GetId(LibraryTypes libraryTypes, ISymbol memberSymbol)
    {
        return memberSymbol.GetAnyAttribute(libraryTypes.IdAttributeTypes) is { } attr
            ? (uint)attr.ConstructorArguments.First().Value
            : null;
    }

    // Returns true if the type declaration has the specified attribute.
    internal static AttributeData HasAttribute(INamedTypeSymbol symbol, INamedTypeSymbol attributeType, bool inherited)
    {
        if (symbol.GetAttribute(attributeType) is { } attribute)
            return attribute;

        if (inherited)
        {
            foreach (var iface in symbol.AllInterfaces)
            {
                if (iface.GetAttribute(attributeType) is { } iattr)
                    return iattr;
            }

            while ((symbol = symbol.BaseType) != null)
            {
                if (symbol.GetAttribute(attributeType) is { } attr)
                    return attr;
            }
        }

        return null;
    }

    internal static AttributeSyntax GetGeneratedCodeAttributeSyntax() => GeneratedCodeAttributeSyntax;
    private static readonly AttributeSyntax GeneratedCodeAttributeSyntax =
            Attribute(ParseName("global::System.CodeDom.Compiler.GeneratedCodeAttribute"))
                .AddArgumentListArguments(
                    AttributeArgument(Constants.CodeGeneratorName.GetLiteralExpression()),
                    AttributeArgument(typeof(CodeGenerator).Assembly.GetName().Version.ToString().GetLiteralExpression()));

    internal static AttributeSyntax GetMethodImplAttributeSyntax() => MethodImplAttributeSyntax;
    private static readonly AttributeSyntax MethodImplAttributeSyntax =
        Attribute(ParseName("global::System.Runtime.CompilerServices.MethodImplAttribute"))
            .AddArgumentListArguments(AttributeArgument(ParseName("global::System.Runtime.CompilerServices.MethodImplOptions").Member("AggressiveInlining")));
}
