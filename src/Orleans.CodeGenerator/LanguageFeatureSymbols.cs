using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator;

/// <summary>
/// Compatibility boundary for closed-type and union symbol APIs across supported compiler versions.
/// </summary>
/// <remarks>
/// The generator is compiled against an older Roslyn API so that it loads in older compilers.
/// Compilers which predate these features cannot declare closed or union types, but they can reference union types compiled by newer compilers.
/// Those compilers identify referenced unions by their <c>System.Runtime.CompilerServices.UnionAttribute</c> metadata and expose no case types.
/// Compilers which declare unions but expose no case types receive an actionable generator diagnostic when those cases are required.
/// This boundary owns API discovery, metadata recognition, and reporting unavailable case types.
/// Once the minimum supported Roslyn version exposes all three properties, replace calls with
/// <c>ITypeSymbol.IsClosed</c>, <c>ITypeSymbol.IsUnion</c>, and <c>ITypeSymbol.UnionCaseTypes</c>, then remove this class
/// and its unavailable-case-types diagnostic from <see cref="InvalidUnionCaseDiagnostic"/>.
/// </remarks>
internal static class LanguageFeatureSymbols
{
    private static readonly Func<ITypeSymbol, bool>? IsClosedAccessor = CreateAccessor<bool>("IsClosed");
    private static readonly Func<ITypeSymbol, bool>? IsUnionAccessor = CreateAccessor<bool>("IsUnion");
    private static readonly Func<ITypeSymbol, ImmutableArray<ITypeSymbol>>? UnionCaseTypesAccessor = CreateAccessor<ImmutableArray<ITypeSymbol>>("UnionCaseTypes");

    public static bool IsClosed(ITypeSymbol type) => IsClosedAccessor?.Invoke(type) == true;

    public static bool IsUnion(ITypeSymbol type) => IsUnionAccessor is { } isUnion ? isUnion(type) : HasUnionAttribute(type);

    private static bool HasUnionAttribute(ITypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Struct or TypeKind.Class))
        {
            return false;
        }

        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is
                {
                    Name: "UnionAttribute",
                    ContainingNamespace: { Name: "CompilerServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } }
                })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the case types of a union type in declaration order.
    /// </summary>
    /// <exception cref="OrleansGeneratorDiagnosticAnalysisException">
    /// The compiler does not expose union case types.
    /// </exception>
    public static ImmutableArray<ITypeSymbol> GetUnionCaseTypes(INamedTypeSymbol type)
    {
        if (UnionCaseTypesAccessor is { } accessor)
        {
            return accessor(type);
        }

        throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnionCaseTypesUnavailableDiagnostic(type));
    }

    private static Func<ITypeSymbol, T>? CreateAccessor<T>(string propertyName)
    {
        var property = typeof(ITypeSymbol).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetMethod is not { } getter || property.PropertyType != typeof(T))
        {
            return null;
        }

        return (Func<ITypeSymbol, T>)Delegate.CreateDelegate(typeof(Func<ITypeSymbol, T>), getter);
    }
}
