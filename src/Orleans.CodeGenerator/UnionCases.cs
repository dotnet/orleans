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
/// <param name="RequiresObjectConversion"><see langword="true"/> when the case type is not statically convertible to the union type, such as a case which derives from one instantiation of a generic closed type.</param>
internal sealed record UnionCaseDescription(uint Id, ITypeSymbol CaseType, ITypeSymbol ValueType, bool RequiresObjectConversion);

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

        var caseTypes = LanguageFeatureSymbols.GetUnionCaseTypes(type);
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
            result.Add(new UnionCaseDescription(ids[i], caseType, valueType, RequiresObjectConversion: false));
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
            var (caseType, requiresObjectConversion) = GetClosedCaseType(subtype, definition);
            result.Add(new UnionCaseDescription(id, caseType, caseType, requiresObjectConversion));
        }

        EnsureUniqueIds(definition, null, result);
        result.Sort(static (x, y) => x.Id.CompareTo(y.Id));
        return result;
    }

    /// <summary>
    /// Expresses a direct subtype of a closed type in terms of the closed type's type parameters.
    /// </summary>
    private static (INamedTypeSymbol CaseType, bool RequiresObjectConversion) GetClosedCaseType(INamedTypeSymbol subtype, INamedTypeSymbol closedType)
    {
        if (!closedType.IsGenericType)
        {
            return (subtype, false);
        }

        if (subtype.ContainingType is { IsGenericType: true })
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnsupportedGenericCaseDiagnostic(subtype, closedType));
        }

        var baseTypeArguments = subtype.BaseType!.TypeArguments;
        var subtypeArguments = new ITypeSymbol?[subtype.TypeParameters.Length];
        var requiresObjectConversion = false;
        for (var i = 0; i < baseTypeArguments.Length; i++)
        {
            var argument = baseTypeArguments[i];
            if (argument is ITypeParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, subtype))
            {
                if (subtypeArguments[parameter.Ordinal] is not null)
                {
                    throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnsupportedGenericCaseDiagnostic(subtype, closedType));
                }

                subtypeArguments[parameter.Ordinal] = closedType.TypeParameters[i];
            }
            else if (ContainsTypeParameter(argument))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnsupportedGenericCaseDiagnostic(subtype, closedType));
            }
            else
            {
                // The case applies to a single instantiation of the closed type, such as 'IntShape : Shape<int>'.
                requiresObjectConversion = true;
            }
        }

        if (subtypeArguments.Any(static argument => argument is null))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidUnionCaseDiagnostic.CreateUnsupportedGenericCaseDiagnostic(subtype, closedType));
        }

        var caseType = subtype.TypeParameters.Length == 0 ? subtype : subtype.Construct([.. subtypeArguments!]);
        return (caseType, requiresObjectConversion);
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

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
