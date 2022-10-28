using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class InvalidRpcMethodReturnTypeDiagnostic
{
    public const string RuleId = "ORLEANS0102"; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.InvalidRpcMethodReturnTypeTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.InvalidRpcMethodReturnTypeMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.InvalidRpcMethodReturnTypeDescription), Resources.ResourceManager, typeof(Resources));

    internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    public static Diagnostic CreateDiagnostic(Location location, string returnType, string methodIdentifier, string supportedReturnTypeList) => Diagnostic.Create(Rule, location, returnType, methodIdentifier, supportedReturnTypeList);

    internal static Diagnostic CreateDiagnostic(MethodDescription methodDescription)
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
