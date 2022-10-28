using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class IncorrectProxyBaseClassSpecificationDiagnostic
{
    public const string RuleId = "ORLEANS0104"; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = "The proxy base class specified is not a valid proxy base class";
    private static readonly LocalizableString MessageFormat = "Proxy base class {0} does not conform to requirements: {1}";
    private static readonly LocalizableString Description = "Proxy base clases must conform. Please report this bug by opening an issue https://github.com/dotnet/orleans/issues/new.";

    internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    internal static Diagnostic CreateDiagnostic(INamedTypeSymbol baseClass, Location location, string complaint) => Diagnostic.Create(Rule, location, baseClass.ToDisplayString(), complaint);
}
