using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

internal static class InvokableBaseTypeMappingDiagnostic
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticRuleId.InvalidInvokableBaseTypeMapping,
        "Invalid invokable base type mapping",
        "{0}",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static Diagnostic CreateDiagnostic(ResolverDiagnostic diagnostic)
        => Diagnostic.Create(Rule, diagnostic.Location, diagnostic.Message);
}
