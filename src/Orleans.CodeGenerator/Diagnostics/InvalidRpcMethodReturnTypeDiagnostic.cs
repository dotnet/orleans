using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when an RPC method has an unsupported return type.
/// </summary>
public static class InvalidRpcMethodReturnTypeDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string RuleId = DiagnosticRuleId.InvalidRpcMethodReturnType; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.InvalidRpcMethodReturnTypeTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.InvalidRpcMethodReturnTypeMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.InvalidRpcMethodReturnTypeDescription), Resources.ResourceManager, typeof(Resources));

    internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    /// <summary>
    /// Creates a diagnostic for an RPC method with an unsupported return type.
    /// </summary>
    /// <param name="location">The source location associated with the diagnostic.</param>
    /// <param name="returnType">The unsupported return type.</param>
    /// <param name="methodIdentifier">The identifier of the RPC method.</param>
    /// <param name="supportedReturnTypeList">A display list of supported return types.</param>
    /// <returns>The diagnostic.</returns>
    public static Diagnostic CreateDiagnostic(Location? location, string returnType, string methodIdentifier, string supportedReturnTypeList) => Diagnostic.Create(Rule, location, returnType, methodIdentifier, supportedReturnTypeList);

    internal static Diagnostic CreateDiagnostic(InvokableMethodDescription methodDescription)
    {
        var methodReturnType = methodDescription.Method.ReturnType;
        var diagnostic = CreateDiagnostic(
            methodDescription.Method.OriginalDefinition.Locations.FirstOrDefault(),
            methodReturnType.ToDisplayString(),
            methodDescription.Method.ToDisplayString(),
            string.Join(", ", methodDescription.InvokableBaseTypes.Keys.Select(v => v.ToDisplayString())));
        return diagnostic;
    }
}
