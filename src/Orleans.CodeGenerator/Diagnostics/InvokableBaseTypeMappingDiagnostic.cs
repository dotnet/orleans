using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

internal static class InvokableBaseTypeMappingDiagnostic
{
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(
        nameof(Resources.InvalidInvokableBaseTypeMappingTitle),
        Resources.ResourceManager,
        typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
        nameof(Resources.InvalidInvokableBaseTypeMappingMessageFormat),
        Resources.ResourceManager,
        typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(
        nameof(Resources.InvalidInvokableBaseTypeMappingDescription),
        Resources.ResourceManager,
        typeof(Resources));

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticRuleId.InvalidInvokableBaseTypeMapping,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description);

    public static Diagnostic CreateDiagnostic(ResolverDiagnostic diagnostic)
        => Diagnostic.Create(Rule, diagnostic.Location, diagnostic.Message);
}
