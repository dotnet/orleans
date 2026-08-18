using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Hashing;
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

    internal static string CreateHashedMethodId(
        IMethodSymbol methodSymbol,
        INamedTypeSymbol cancellationTokenType)
    {
        var includeCancellationTokens =
            HasNonTrailingCancellationToken(methodSymbol, cancellationTokenType)
            || HasEquivalentOverloadWithoutCancellation(methodSymbol, cancellationTokenType);
        return CreateHashedMethodId(
            methodSymbol,
            cancellationTokenType,
            includeCancellationTokens);
    }

    internal static string CreateLegacyHashedMethodId(
        IMethodSymbol methodSymbol,
        INamedTypeSymbol cancellationTokenType) =>
        CreateHashedMethodId(
            methodSymbol,
            cancellationTokenType,
            includeCancellationTokens: true);

    private static string CreateHashedMethodId(
        IMethodSymbol methodSymbol,
        INamedTypeSymbol cancellationTokenType,
        bool includeCancellationTokens)
    {
        var methodSignature = Format(
            methodSymbol,
            cancellationTokenType,
            includeCancellationTokens);
        var hash = XxHash32.Hash(Encoding.UTF8.GetBytes(methodSignature));
        return $"{HexConverter.ToString(hash)}";
    }

    private static string Format(
        IMethodSymbol methodInfo,
        INamedTypeSymbol cancellationTokenType,
        bool includeCancellationTokens)
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
                var parameterType = parameter.Type;
                if (!includeCancellationTokens
                    && SymbolEqualityComparer.Default.Equals(parameterType, cancellationTokenType))
                {
                    continue;
                }

                if (!first)
                {
                    result.Append(',');
                }

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

    private static bool HasEquivalentOverloadWithoutCancellation(
        IMethodSymbol method,
        INamedTypeSymbol cancellationTokenType)
    {
        foreach (var candidate in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, method)
                || candidate.Arity != method.Arity)
            {
                continue;
            }

            using var methodParameters = GetSerializedParameterTypes(method, cancellationTokenType).GetEnumerator();
            using var candidateParameters = GetSerializedParameterTypes(candidate, cancellationTokenType).GetEnumerator();
            while (true)
            {
                var methodHasNext = methodParameters.MoveNext();
                var candidateHasNext = candidateParameters.MoveNext();
                if (methodHasNext != candidateHasNext)
                {
                    break;
                }

                if (!methodHasNext)
                {
                    return true;
                }

                if (!string.Equals(
                    methodParameters.Current.ToDisplayName(),
                    candidateParameters.Current.ToDisplayName(),
                    StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return false;
    }

    private static bool HasNonTrailingCancellationToken(
        IMethodSymbol method,
        INamedTypeSymbol cancellationTokenType)
    {
        var foundCancellationToken = false;
        foreach (var parameter in method.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenType))
            {
                foundCancellationToken = true;
            }
            else if (foundCancellationToken)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ITypeSymbol> GetSerializedParameterTypes(
        IMethodSymbol method,
        INamedTypeSymbol cancellationTokenType)
    {
        foreach (var parameter in method.Parameters)
        {
            if (!SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenType))
            {
                yield return parameter.Type;
            }
        }
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
