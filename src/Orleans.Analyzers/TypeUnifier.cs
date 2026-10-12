using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.Analyzers;

/// <summary>
/// Determines whether two types can become the same type for some substitution of their type parameters which satisfies the type parameters' constraints.
/// </summary>
/// <remarks>
/// Type parameters are grouped into variables. Unifying two type parameters merges their variables, provided that the combined constraints
/// can be satisfied together. Unifying a type parameter with any other type binds its variable to that type, provided that the type satisfies the
/// constraints of every type parameter in the variable and does not contain the variable itself. Constraint types which mention type parameters
/// are not evaluated, so the result can be a false positive in that case, but never a false negative.
/// </remarks>
internal sealed class TypeUnifier(CSharpCompilation compilation)
{
    private readonly Dictionary<ITypeParameterSymbol, Variable> _variables = new(SymbolEqualityComparer.Default);

    public static bool CanUnify(Compilation compilation, ITypeSymbol first, ITypeSymbol second)
        => new TypeUnifier((CSharpCompilation)compilation).Unify(first, second);

    private bool Unify(ITypeSymbol first, ITypeSymbol second)
    {
        first = Resolve(first);
        second = Resolve(second);
        return (first, second) switch
        {
            (ITypeParameterSymbol x, ITypeParameterSymbol y) => Merge(GetVariable(x), GetVariable(y)),
            (ITypeParameterSymbol x, _) => Bind(GetVariable(x), second),
            (_, ITypeParameterSymbol y) => Bind(GetVariable(y), first),
            (IArrayTypeSymbol x, IArrayTypeSymbol y) => x.Rank == y.Rank && Unify(x.ElementType, y.ElementType),
            (INamedTypeSymbol x, INamedTypeSymbol y) => UnifyNamed(x, y),
            _ => SymbolEqualityComparer.Default.Equals(first, second),
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

    private Variable GetVariable(ITypeParameterSymbol parameter)
    {
        if (!_variables.TryGetValue(parameter, out var variable))
        {
            _variables[parameter] = variable = new Variable(parameter);
        }

        return variable;
    }

    private ITypeSymbol Resolve(ITypeSymbol type)
    {
        while (type is ITypeParameterSymbol parameter && GetVariable(parameter).Binding is { } bound)
        {
            type = bound;
        }

        return type;
    }

    private bool Merge(Variable first, Variable second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (!AreCompatible(first.Parameters.Concat(second.Parameters)))
        {
            return false;
        }

        foreach (var parameter in second.Parameters)
        {
            first.Parameters.Add(parameter);
            _variables[parameter] = first;
        }

        return true;
    }

    private bool Bind(Variable variable, ITypeSymbol type)
    {
        if (Occurs(variable, type) || !variable.Parameters.All(parameter => SatisfiesConstraints(parameter, type)))
        {
            return false;
        }

        variable.Binding = type;
        return true;
    }

    private bool Occurs(Variable variable, ITypeSymbol type) => Resolve(type) switch
    {
        ITypeParameterSymbol parameter => ReferenceEquals(GetVariable(parameter), variable),
        IArrayTypeSymbol array => Occurs(variable, array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(argument => Occurs(variable, argument))
            || named.ContainingType is { } container && Occurs(variable, container),
        _ => false,
    };

    /// <summary>
    /// Returns <see langword="false"/> if no type can satisfy the constraints of all of the provided type parameters.
    /// </summary>
    private static bool AreCompatible(IEnumerable<ITypeParameterSymbol> parameters)
    {
        var isReferenceType = false;
        var isValueType = false;
        var classConstraints = new List<ITypeSymbol>();
        foreach (var parameter in parameters)
        {
            isReferenceType |= parameter.HasReferenceTypeConstraint;
            isValueType |= parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint;
            foreach (var constraintType in parameter.ConstraintTypes)
            {
                if (constraintType.TypeKind == TypeKind.Class && !ContainsTypeParameter(constraintType))
                {
                    classConstraints.Add(constraintType);

                    // 'System.ValueType' and 'System.Enum' constraints admit value types.
                    isReferenceType |= constraintType.SpecialType is not (SpecialType.System_ValueType or SpecialType.System_Enum);
                }
            }
        }

        if (isReferenceType && isValueType)
        {
            return false;
        }

        // A type has a single base class chain, so every class constraint must be on the same chain.
        for (var i = 0; i < classConstraints.Count; i++)
        {
            for (var j = i + 1; j < classConstraints.Count; j++)
            {
                if (!InheritsFrom(classConstraints[i], classConstraints[j]) && !InheritsFrom(classConstraints[j], classConstraints[i]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool SatisfiesConstraints(ITypeParameterSymbol parameter, ITypeSymbol type)
    {
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

            var conversion = compilation.ClassifyConversion(type, constraintType);
            if (!conversion.IsImplicit || !(conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InheritsFrom(ITypeSymbol type, ITypeSymbol baseType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// A set of type parameters which must be the same type, and the type they are bound to, if any.
    /// </summary>
    private sealed class Variable(ITypeParameterSymbol parameter)
    {
        public List<ITypeParameterSymbol> Parameters { get; } = [parameter];

        public ITypeSymbol? Binding { get; set; }
    }
}
