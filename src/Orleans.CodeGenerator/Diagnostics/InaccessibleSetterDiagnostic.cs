using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when generated serialization code cannot assign a member.
/// </summary>
public static class InaccessibleSetterDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string RuleId = DiagnosticRuleId.InaccessibleSetter; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.InaccessibleSetterTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.InaccessibleSetterMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.InaccessibleSetterDescription), Resources.ResourceManager, typeof(Resources));

    internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    /// <summary>
    /// Creates a diagnostic for an inaccessible member setter.
    /// </summary>
    /// <param name="location">The source location associated with the diagnostic.</param>
    /// <param name="identifier">The identifier of the member which cannot be assigned.</param>
    /// <returns>The diagnostic.</returns>
    public static Diagnostic CreateDiagnostic(Location? location, string identifier) => Diagnostic.Create(Rule, location, identifier);
}
