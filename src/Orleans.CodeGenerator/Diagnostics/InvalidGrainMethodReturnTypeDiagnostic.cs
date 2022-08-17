using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public class InvalidGrainMethodReturnTypeDiagnostic
{
    public const string RuleId = "ORLEANS0102"; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.InvalidGrainMethodReturnTypeTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.InvalidGrainMethodReturnTypeMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.InvalidGrainMethodReturnTypeDescription), Resources.ResourceManager, typeof(Resources));

    internal static DiagnosticDescriptor Rule { get; } = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

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