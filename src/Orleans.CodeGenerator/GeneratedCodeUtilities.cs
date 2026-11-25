using System.Globalization;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.SyntaxGeneration;
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
        IMethodSymbol method)
    {
        return GetId(libraryTypes, method)?.ToString(CultureInfo.InvariantCulture)
            ?? GetAlias(libraryTypes, method);
    }

    internal static string? GetClaimedGeneratedMethodId(
        LibraryTypes libraryTypes,
        IMethodSymbol method,
        INamedTypeSymbol containingInterface,
        bool isExtension)
    {
        if (GetId(libraryTypes, method) is not { } methodId)
        {
            return null;
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

        return null;
    }

    internal static string? GetAlias(LibraryTypes libraryTypes, ISymbol symbol) => (string?)symbol.GetAttribute(libraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;

    private static readonly string GeneratedCodeAttributeArguments =
        $"{CodeText.QuotedText(CodeGeneratorName)}, {CodeText.QuotedText(typeof(GeneratedCodeUtilities).Assembly.GetName().Version.ToString())}";

    /// <summary>The attribute list applied to each generated type.</summary>
    internal static readonly string GeneratedCodeAttributeText =
        $"[global::System.CodeDom.Compiler.GeneratedCodeAttribute({GeneratedCodeAttributeArguments}), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]";

    /// <summary>The attribute list applied to generated members of user-declared types.</summary>
    internal static readonly string GeneratedMemberAttributeText =
        $"[global::System.CodeDom.Compiler.GeneratedCodeAttribute({GeneratedCodeAttributeArguments}), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]";

    internal const string MethodImplAttributeText =
        "[global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]";
}
