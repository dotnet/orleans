using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SyntaxGeneration.SymbolExtensions;

namespace Orleans.CodeGenerator;

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
            ? (uint)attr.ConstructorArguments.First().Value!
            : null;
    }

    internal static string CreateHashedMethodId(IMethodSymbol methodSymbol)
        => MethodIdProvider.Create(methodSymbol);

    internal static string? GetMethodId(
        LibraryTypes libraryTypes,
        IMethodSymbol method,
        INamedTypeSymbol containingInterface,
        bool isExtension)
    {
        if (GetId(libraryTypes, method) is not { } methodId)
        {
            return GetAlias(libraryTypes, method);
        }

        foreach (var candidate in containingInterface.GetMembers().OfType<IMethodSymbol>()
            .Concat(containingInterface.AllInterfaces.SelectMany(static interfaceType => interfaceType.GetMembers().OfType<IMethodSymbol>())))
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, method.OriginalDefinition))
            {
                continue;
            }

            if (isExtension
                && !SymbolEqualityComparer.Default.Equals(
                    candidate.OriginalDefinition.ContainingType,
                    method.OriginalDefinition.ContainingType))
            {
                continue;
            }

            var generatedMethodId = CreateHashedMethodId(candidate.OriginalDefinition);
            if (uint.TryParse(generatedMethodId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var generatedId)
                && generatedId == methodId)
            {
                return generatedMethodId;
            }
        }

        return methodId.ToString(CultureInfo.InvariantCulture);
    }

    internal static string? GetAlias(LibraryTypes libraryTypes, ISymbol symbol) => (string?)symbol.GetAttribute(libraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;

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
