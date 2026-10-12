using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

internal static class InvalidUnionCaseDiagnostic
{
    public const string DiagnosticId = DiagnosticRuleId.InvalidUnionCase;
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor MissingCaseIdRule = new(
        DiagnosticId,
        "Subtypes of closed types which use case identifiers must declare a case identifier",
        "Type '{0}' derives from closed type '{1}', which uses case identifiers, so '{0}' must declare [Case]",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateCaseIdRule = new(
        DiagnosticId,
        "Case identifiers must be unique",
        "Case identifier {0} is used by both '{1}' and '{2}' in '{3}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CaseIdCountRule = new(
        DiagnosticId,
        "CaseIds must specify one identifier per union case",
        "[CaseIds] on union '{0}' specifies {1} identifier(s), but the union declares {2} case(s)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedGenericCaseRule = new(
        DiagnosticId,
        "Cases of a generic closed type must be cases of every instantiation",
        "Case '{0}' must pass its own type parameters as the type arguments of '{1}', as in 'Case<T> : Base<T>', so that it is a case of every instantiation of '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedConstrainedCaseRule = new(
        DiagnosticId,
        "The type parameter constraints of a closed type's case must be declared on the closed type",
        "Case '{0}' has type parameter constraints which closed type '{1}' does not declare. Declare the same constraints on the corresponding type parameters of '{1}'.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InaccessibleCaseRule = new(
        DiagnosticId,
        "Union and closed type cases must be accessible to generated code",
        "Case type '{0}' of '{1}' is not accessible to generated code",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ClassUnionRule = new(
        DiagnosticId,
        "Generated serializers support struct union types",
        "Union type '{0}' is a class. Generated serializers support struct union types.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnionCaseTypesUnavailableRule = new(
        DiagnosticId,
        "Generated union serializers require a compiler which exposes union case types",
        "The compiler does not expose the case types of union type '{0}' to source generators. Build with the .NET 11 SDK or later to generate its serializer.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static Location GetLocation(ISymbol symbol) => symbol.Locations.FirstOrDefault() ?? Location.None;

    internal static Diagnostic CreateMissingCaseIdDiagnostic(INamedTypeSymbol subtype, INamedTypeSymbol closedType) =>
        Diagnostic.Create(MissingCaseIdRule, GetLocation(subtype), subtype.ToDisplayString(), closedType.ToDisplayString());

    internal static Diagnostic CreateDuplicateCaseIdDiagnostic(ISymbol location, uint id, ITypeSymbol first, ITypeSymbol second, INamedTypeSymbol unionType) =>
        Diagnostic.Create(DuplicateCaseIdRule, GetLocation(location), id, first.ToDisplayString(), second.ToDisplayString(), unionType.ToDisplayString());

    internal static Diagnostic CreateCaseIdCountDiagnostic(INamedTypeSymbol unionType, int idCount, int caseCount) =>
        Diagnostic.Create(CaseIdCountRule, GetLocation(unionType), unionType.ToDisplayString(), idCount, caseCount);

    internal static Diagnostic CreateUnsupportedGenericCaseDiagnostic(INamedTypeSymbol subtype, INamedTypeSymbol closedType) =>
        Diagnostic.Create(UnsupportedGenericCaseRule, GetLocation(subtype), subtype.ToDisplayString(), closedType.ToDisplayString());

    internal static Diagnostic CreateUnsupportedConstrainedCaseDiagnostic(INamedTypeSymbol subtype, INamedTypeSymbol closedType) =>
        Diagnostic.Create(UnsupportedConstrainedCaseRule, GetLocation(subtype), subtype.ToDisplayString(), closedType.ToDisplayString());

    internal static Diagnostic CreateInaccessibleCaseDiagnostic(ISymbol location, ITypeSymbol caseType, INamedTypeSymbol unionType) =>
        Diagnostic.Create(InaccessibleCaseRule, GetLocation(location), caseType.ToDisplayString(), unionType.ToDisplayString());

    internal static Diagnostic CreateUnionCaseTypesUnavailableDiagnostic(INamedTypeSymbol unionType) =>
        Diagnostic.Create(UnionCaseTypesUnavailableRule, GetLocation(unionType), unionType.ToDisplayString());

    internal static Diagnostic CreateClassUnionDiagnostic(INamedTypeSymbol unionType) =>
        Diagnostic.Create(ClassUnionRule, GetLocation(unionType), unionType.ToDisplayString());
}
