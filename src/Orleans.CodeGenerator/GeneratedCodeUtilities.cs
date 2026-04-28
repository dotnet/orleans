using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SyntaxGeneration.SymbolExtensions;

#nullable disable
namespace Orleans.CodeGenerator
{
    internal static class GeneratedCodeUtilities
    {
        internal const string CodeGeneratorName = "OrleansCodeGen";

        internal static string GetGeneratedNamespaceName(ITypeSymbol type) => type.GetNamespaceAndNesting() switch
        {
            { Length: > 0 } ns => $"{CodeGeneratorName}.{ns}",
            _ => CodeGeneratorName
        };

        internal static uint? GetId(LibraryTypes libraryTypes, ISymbol memberSymbol)
        {
            return memberSymbol.GetAttribute(libraryTypes.IdAttributeType) is { } attr
                ? (uint)attr.ConstructorArguments.First().Value
                : null;
        }

        internal static string CreateHashedMethodId(IMethodSymbol methodSymbol)
        {
            var methodSignature = Format(methodSymbol);
            var hash = XxHash32.Hash(Encoding.UTF8.GetBytes(methodSignature));
            return $"{HexConverter.ToString(hash)}";

            static string Format(IMethodSymbol methodInfo)
            {
                var result = new StringBuilder();
                result.Append(methodInfo.ContainingType.ToDisplayName());
                result.Append('.');
                result.Append(methodInfo.Name);

                if (methodInfo.IsGenericMethod)
                {
                    result.Append('<');
                    var first = true;
                    foreach (var typeArgument in methodInfo.TypeArguments)
                    {
                        if (!first) result.Append(',');
                        else first = false;
                        result.Append(typeArgument.Name);
                    }

                    result.Append('>');
                }

                {
                    result.Append('(');
                    var parameters = methodInfo.Parameters;
                    var first = true;
                    foreach (var parameter in parameters)
                    {
                        if (!first)
                        {
                            result.Append(',');
                        }

                        var parameterType = parameter.Type;
                        switch (parameterType)
                        {
                            case ITypeParameterSymbol _:
                                result.Append(parameterType.Name);
                                break;
                            default:
                                result.Append(parameterType.ToDisplayName());
                                break;
                        }

                        first = false;
                    }
                }

                result.Append(')');
                return result.ToString();
            }
        }

        internal static string GetAlias(LibraryTypes libraryTypes, ISymbol symbol) => (string)symbol.GetAttribute(libraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;

        internal static AttributeListSyntax GetGeneratedCodeAttributes() => GeneratedCodeAttributeSyntax;

        private static readonly AttributeListSyntax GeneratedCodeAttributeSyntax =
            AttributeList().AddAttributes(
                Attribute(ParseName("global::System.CodeDom.Compiler.GeneratedCodeAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(CodeGeneratorName.GetLiteralExpression()),
                        AttributeArgument(typeof(GeneratedCodeUtilities).Assembly.GetName().Version.ToString().GetLiteralExpression())),
                Attribute(ParseName("global::System.ComponentModel.EditorBrowsableAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(ParseName("global::System.ComponentModel.EditorBrowsableState").Member("Never"))),
                        Attribute(ParseName("global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"))
            );

        internal static AttributeSyntax GetMethodImplAttributeSyntax() => MethodImplAttributeSyntax;

        private static readonly AttributeSyntax MethodImplAttributeSyntax =
            Attribute(ParseName("global::System.Runtime.CompilerServices.MethodImplAttribute"))
                .AddArgumentListArguments(AttributeArgument(ParseName("global::System.Runtime.CompilerServices.MethodImplOptions").Member("AggressiveInlining")));
    }
}
