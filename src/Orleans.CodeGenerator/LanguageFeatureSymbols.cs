using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

/// <summary>
/// Reads closed-type and union information from compilers which support those language features.
/// </summary>
/// <remarks>
/// The generator is compiled against an older Roslyn API so that it loads in older compilers.
/// Compilers which predate these features cannot declare closed or union types, but they can reference union types compiled by newer compilers.
/// Those compilers identify referenced unions by their <c>System.Runtime.CompilerServices.UnionAttribute</c> metadata and expose no case types.
/// Some preview compilers can declare union types without exposing their case types, so callers must handle unions whose case types are unavailable.
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
    /// Gets the case types of a union type in declaration order, or <see langword="null"/> if the compiler does not expose union case types.
    /// </summary>
    public static ImmutableArray<ITypeSymbol>? GetUnionCaseTypes(ITypeSymbol type) => UnionCaseTypesAccessor?.Invoke(type);

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
