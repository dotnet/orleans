using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.Analyzers;

/// <summary>
/// Determines whether two types can become the same type for some substitution of their type parameters.
/// </summary>
/// <remarks>
/// Each type parameter is bound at most once across the whole comparison. A binding to a concrete type must satisfy the type parameter's
/// constraints. A binding to another type parameter is accepted, because the result of combining both parameters' constraints is
/// satisfiable for typical constraints. Constraints which mention type parameters are not checked, so the result can be a false positive
/// in that case, but it is never a false negative.
/// </remarks>
internal sealed class TypeUnifier(Compilation compilation)
{
    private readonly Dictionary<ITypeParameterSymbol, ITypeSymbol> _bindings = new(SymbolEqualityComparer.Default);

    public static bool CanUnify(Compilation compilation, ITypeSymbol first, ITypeSymbol second) => new TypeUnifier(compilation).Unify(first, second);

    private bool Unify(ITypeSymbol first, ITypeSymbol second)
    {
        first = Resolve(first);
        second = Resolve(second);
        if (SymbolEqualityComparer.Default.Equals(first, second))
        {
            return true;
        }

        return (first, second) switch
        {
            (ITypeParameterSymbol parameter, _) => Bind(parameter, second),
            (_, ITypeParameterSymbol parameter) => Bind(parameter, first),
            (IArrayTypeSymbol x, IArrayTypeSymbol y) => x.Rank == y.Rank && Unify(x.ElementType, y.ElementType),
            (INamedTypeSymbol x, INamedTypeSymbol y) => UnifyNamed(x, y),
            _ => false,
        };
    }

    private bool UnifyNamed(INamedTypeSymbol first, INamedTypeSymbol second)
    {
        if (!SymbolEqualityComparer.Default.Equals(first.OriginalDefinition, second.OriginalDefinition))
        {
            return false;
        }

        if (first.ContainingType is { } firstContainer && second.ContainingType is { } secondContainer && !UnifyNamed(firstContainer, secondContainer))
        {
            return false;
        }

        for (var i = 0; i < first.TypeArguments.Length; i++)
        {
            if (!Unify(first.TypeArguments[i], second.TypeArguments[i]))
            {
                return false;
            }
        }

        return true;
    }

    private ITypeSymbol Resolve(ITypeSymbol type)
    {
        while (type is ITypeParameterSymbol parameter && _bindings.TryGetValue(parameter, out var bound))
        {
            type = bound;
        }

        return type;
    }

    private bool Bind(ITypeParameterSymbol parameter, ITypeSymbol type)
    {
        if (Occurs(parameter, type) || !SatisfiesConstraints(parameter, type))
        {
            return false;
        }

        _bindings[parameter] = type;
        return true;
    }

    private bool Occurs(ITypeParameterSymbol parameter, ITypeSymbol type)
    {
        type = Resolve(type);
        return type switch
        {
            ITypeParameterSymbol other => SymbolEqualityComparer.Default.Equals(parameter, other),
            IArrayTypeSymbol array => Occurs(parameter, array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => Occurs(parameter, argument))
                || named.ContainingType is { } container && Occurs(parameter, container),
            _ => false,
        };
    }

    private bool SatisfiesConstraints(ITypeParameterSymbol parameter, ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
        {
            return true;
        }

        var isNullableValueType = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        if (parameter.HasReferenceTypeConstraint && !type.IsReferenceType
            || parameter.HasValueTypeConstraint && (!type.IsValueType || isNullableValueType)
            || parameter.HasUnmanagedTypeConstraint && !type.IsUnmanagedType
            || parameter.HasConstructorConstraint && !HasPublicParameterlessConstructor(type))
        {
            return false;
        }

        foreach (var constraintType in parameter.ConstraintTypes)
        {
            if (ContainsTypeParameter(constraintType))
            {
                continue;
            }

            var conversion = ((CSharpCompilation)compilation).ClassifyConversion(type, constraintType);
            if (!conversion.IsImplicit || !(conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasPublicParameterlessConstructor(ITypeSymbol type) => type switch
    {
        { IsValueType: true } => true,
        INamedTypeSymbol { IsAbstract: false } named => named.InstanceConstructors.Any(static ctor => ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility == Accessibility.Public),
        _ => false,
    };

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };
}
