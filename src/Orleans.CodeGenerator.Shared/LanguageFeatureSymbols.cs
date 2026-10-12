using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

/// <summary>
/// Reads closed-type and union information from compilers which support those language features.
/// </summary>
/// <remarks>
/// The generator is compiled against an older Roslyn API so that it loads in older compilers.
/// Compilers which predate these features cannot declare closed or union types, so the absence of the APIs means the features are absent.
/// </remarks>
internal static class LanguageFeatureSymbols
{
    private static readonly Func<ITypeSymbol, bool>? IsClosedAccessor = CreateAccessor<bool>("IsClosed");
    private static readonly Func<ITypeSymbol, bool>? IsUnionAccessor = CreateAccessor<bool>("IsUnion");
    private static readonly Func<ITypeSymbol, ImmutableArray<ITypeSymbol>>? UnionCaseTypesAccessor = CreateAccessor<ImmutableArray<ITypeSymbol>>("UnionCaseTypes");

    public static bool IsClosed(ITypeSymbol type) => IsClosedAccessor?.Invoke(type) == true;

    public static bool IsUnion(ITypeSymbol type) => IsUnionAccessor?.Invoke(type) == true;

    public static ImmutableArray<ITypeSymbol> GetUnionCaseTypes(ITypeSymbol type) => UnionCaseTypesAccessor?.Invoke(type) ?? [];

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
