using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

internal static class InvalidRpcParameterIdDiagnostic
{
    public const string DiagnosticId = DiagnosticRuleId.InvalidRpcParameterId;
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor CancellationTokenRule = new(
        DiagnosticId,
        "CancellationToken parameters cannot declare RPC field identifiers",
        "The CancellationToken parameter '{0}' on RPC method '{1}' cannot declare [Id] because cancellation tokens are not serialized",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateFieldIdRule = new(
        DiagnosticId,
        "RPC parameter field identifiers must be unique",
        "RPC parameter '{0}' on method '{1}' uses field ID {2}, which is already used by parameter '{3}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static Diagnostic CreateCancellationTokenDiagnostic(IParameterSymbol parameter) =>
        Diagnostic.Create(
            CancellationTokenRule,
            parameter.Locations.FirstOrDefault() ?? Location.None,
            parameter.Name,
            parameter.ContainingSymbol.ToDisplayString());

    internal static Diagnostic CreateDuplicateFieldIdDiagnostic(
        IParameterSymbol parameter,
        uint fieldId,
        IParameterSymbol conflictingParameter) =>
        Diagnostic.Create(
            DuplicateFieldIdRule,
            parameter.Locations.FirstOrDefault() ?? Location.None,
            parameter.Name,
            parameter.ContainingSymbol.ToDisplayString(),
            fieldId,
            conflictingParameter.Name);
}
