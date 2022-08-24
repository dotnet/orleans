using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public class InaccessibleSetterDiagnostic
{
    public const string RuleId = "ORLEANS0101"; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.InaccessibleSetterTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.InaccessibleSetterMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.InaccessibleSetterDescription), Resources.ResourceManager, typeof(Resources));

    internal static DiagnosticDescriptor Rule { get; } = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    public static Diagnostic CreateDiagnostic(Location location, string identifier) => Diagnostic.Create(Rule, location, identifier);
}
