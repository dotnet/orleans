using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

/// <summary>
/// A case of a union type or of a closed type hierarchy, identified on the wire by <see cref="Id"/>.
/// </summary>
/// <param name="Id">The case identifier, which is the field identifier of the case value within the union field.</param>
/// <param name="CaseType">The case type, expressed in terms of the union type's type parameters.</param>
/// <param name="ValueType">The type of case values: <paramref name="CaseType"/>, or its underlying type when it is a nullable value type.</param>
internal sealed record UnionCaseDescription(uint Id, ITypeSymbol CaseType, ITypeSymbol ValueType);

internal static class UnionCaseResolver
{
    /// <summary>
    /// Gets the cases of a union type in declaration order, or <see langword="null"/> if <paramref name="type"/> is not a union type.
    /// </summary>
    public static List<UnionCaseDescription>? GetUnionTypeCases(INamedTypeSymbol type, Compilation compilation, LibraryTypes libraryTypes)
    {
        if (!LanguageFeatureSymbols.IsUnion(type))
        {
            return null;
        }

        if (!type.IsValueType)
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateClassUnionDiagnostic(type));
        }

        if (LanguageFeatureSymbols.GetUnionCaseTypes(type) is not { } caseTypes)
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnionCaseTypesUnavailableDiagnostic(type));
        }

        uint[] ids;
        if (libraryTypes.CaseIdsAttributeOrDefault is { } caseIdsAttributeType && type.GetAttribute(caseIdsAttributeType) is { } caseIdsAttribute)
        {
            var values = caseIdsAttribute.ConstructorArguments.FirstOrDefault().Values;
            ids = values.IsDefault ? [] : [.. values.Select(static value => (uint)value.Value!)];
            if (ids.Length != caseTypes.Length)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateCaseIdCountDiagnostic(type, ids.Length, caseTypes.Length));
            }
        }
        else
        {
            ids = [.. Enumerable.Range(0, caseTypes.Length).Select(static index => (uint)index)];
        }

        var result = new List<UnionCaseDescription>(caseTypes.Length);
        for (var i = 0; i < caseTypes.Length; i++)
        {
            var caseType = caseTypes[i].WithNullableAnnotation(NullableAnnotation.None);
            if (!compilation.IsSymbolAccessibleWithin(caseType, compilation.Assembly))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateInaccessibleCaseDiagnostic(type, caseType, type));
            }

            var valueType = caseType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : caseType;
            result.Add(new UnionCaseDescription(ids[i], caseType, valueType));
        }

        EnsureUniqueIds(type, type, result);
        return result;
    }

    /// <summary>
    /// Gets the cases of a closed type ordered by case identifier, or <see langword="null"/> if <paramref name="type"/> is not a closed type
    /// or if none of its direct subtypes declare a case identifier.
    /// </summary>
    public static List<UnionCaseDescription>? GetClosedTypeCases(INamedTypeSymbol type, Compilation compilation, LibraryTypes libraryTypes)
    {
        if (!LanguageFeatureSymbols.IsClosed(type) || libraryTypes.CaseAttributeOrDefault is not { } caseAttributeType)
        {
            return null;
        }

        var definition = type.OriginalDefinition;
        var subtypes = definition.ContainingAssembly.GetDeclaredTypes()
            .Where(candidate => candidate.BaseType is { } baseType && SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, definition))
            .Select(subtype => (Subtype: subtype, Attribute: subtype.GetAttribute(caseAttributeType)))
            .ToList();
        if (!subtypes.Any(static entry => entry.Attribute is not null))
        {
            return null;
        }

        var result = new List<UnionCaseDescription>(subtypes.Count);
        foreach (var (subtype, attribute) in subtypes)
        {
            if (attribute is null)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateMissingCaseIdDiagnostic(subtype, definition));
            }

            if (!compilation.IsSymbolAccessibleWithin(subtype, compilation.Assembly))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateInaccessibleCaseDiagnostic(subtype, subtype, definition));
            }

            var id = (uint)attribute.ConstructorArguments[0].Value!;
            var caseType = GetClosedCaseType(subtype, definition, compilation);
            result.Add(new UnionCaseDescription(id, caseType, caseType));
        }

        EnsureUniqueIds(definition, null, result);
        result.Sort(static (x, y) => x.Id.CompareTo(y.Id));
        return result;
    }

    /// <summary>
    /// Expresses a direct subtype of a closed type in terms of the closed type's type parameters.
    /// </summary>
    /// <remarks>
    /// A case of a generic closed type must be a case of every instantiation of that type, so each type argument of its base type must be a distinct
    /// type parameter of the case, as in 'Circle&lt;T&gt; : Shape&lt;T&gt;'. A subtype of one instantiation, such as 'IntSquare : Shape&lt;int&gt;', is rejected.
    /// </remarks>
    private static INamedTypeSymbol GetClosedCaseType(INamedTypeSymbol subtype, INamedTypeSymbol closedType, Compilation compilation)
    {
        if (!closedType.IsGenericType && !subtype.IsGenericType)
        {
            return subtype;
        }

        var baseTypeArguments = subtype.BaseType!.TypeArguments;
        var subtypeArguments = new ITypeSymbol[subtype.TypeParameters.Length];
        var isFullyMapped = subtype.ContainingType is not { IsGenericType: true } && baseTypeArguments.Length == subtypeArguments.Length;
        for (var i = 0; isFullyMapped && i < baseTypeArguments.Length; i++)
        {
            if (baseTypeArguments[i] is ITypeParameterSymbol parameter
                && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, subtype)
                && subtypeArguments[parameter.Ordinal] is null)
            {
                subtypeArguments[parameter.Ordinal] = closedType.TypeParameters[i];
            }
            else
            {
                isFullyMapped = false;
            }
        }

        if (!isFullyMapped)
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnsupportedGenericCaseDiagnostic(subtype, closedType));
        }

        // The generated codec for the closed type names each case using the closed type's type parameters, so those parameters must satisfy the case's constraints.
        if (!AreCaseConstraintsSatisfied(subtype, subtypeArguments, compilation))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnsupportedConstrainedCaseDiagnostic(subtype, closedType));
        }

        return subtype.Construct(subtypeArguments);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the closed type's type parameters, substituted for the subtype's type parameters, satisfy every constraint of the subtype.
    /// </summary>
    /// <remarks>
    /// Each constraint must be declared on the corresponding closed-type parameter. Constraints which are only implied, such as an interface implemented by a class constraint, are rejected.
    /// </remarks>
    private static bool AreCaseConstraintsSatisfied(INamedTypeSymbol subtype, ITypeSymbol[] subtypeArguments, Compilation compilation)
    {
        foreach (var parameter in subtype.TypeParameters)
        {
            var target = (ITypeParameterSymbol)subtypeArguments[parameter.Ordinal];
            var targetIsValueType = target.HasValueTypeConstraint || target.HasUnmanagedTypeConstraint;
            if (parameter.HasReferenceTypeConstraint && !target.HasReferenceTypeConstraint
                || parameter.HasValueTypeConstraint && !targetIsValueType
                || parameter.HasUnmanagedTypeConstraint && !target.HasUnmanagedTypeConstraint
                || parameter.HasConstructorConstraint && !target.HasConstructorConstraint && !targetIsValueType)
            {
                return false;
            }

            foreach (var constraintType in parameter.ConstraintTypes)
            {
                var substituted = Substitute(constraintType, subtype, subtypeArguments, compilation);
                if (!target.ConstraintTypes.Any(targetConstraint => SymbolEqualityComparer.Default.Equals(targetConstraint, substituted)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ITypeSymbol Substitute(ITypeSymbol type, INamedTypeSymbol subtype, ITypeSymbol[] subtypeArguments, Compilation compilation) => type switch
    {
        ITypeParameterSymbol parameter when SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, subtype) => subtypeArguments[parameter.Ordinal],
        IArrayTypeSymbol array => compilation.CreateArrayTypeSymbol(Substitute(array.ElementType, subtype, subtypeArguments, compilation), array.Rank),
        INamedTypeSymbol { IsGenericType: true } named => SubstituteNamedType(named, subtype, subtypeArguments, compilation),
        _ => type,
    };

    private static INamedTypeSymbol SubstituteNamedType(INamedTypeSymbol type, INamedTypeSymbol subtype, ITypeSymbol[] subtypeArguments, Compilation compilation)
    {
        // A nested type's own type arguments exclude its containing type's arguments, so substitute the containing type before constructing the nested type.
        var definition = type.ContainingType is { } containing
            ? ((INamedTypeSymbol)Substitute(containing, subtype, subtypeArguments, compilation)).GetTypeMembers(type.Name, type.Arity).Single()
            : type.OriginalDefinition;
        return type.Arity == 0
            ? definition
            : definition.Construct([.. type.TypeArguments.Select(argument => Substitute(argument, subtype, subtypeArguments, compilation))]);
    }

    private static void EnsureUniqueIds(INamedTypeSymbol unionType, ISymbol? location, List<UnionCaseDescription> cases)
    {
        var seen = new Dictionary<uint, UnionCaseDescription>();
        foreach (var unionCase in cases)
        {
            if (seen.TryGetValue(unionCase.Id, out var existing))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateDuplicateCaseIdDiagnostic(
                    location ?? unionCase.CaseType.OriginalDefinition,
                    unionCase.Id,
                    existing.CaseType,
                    unionCase.CaseType,
                    unionType));
            }

            seen.Add(unionCase.Id, unionCase);
        }
    }
}
