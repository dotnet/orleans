// Centralized diagnostic rule IDs for Orleans.CodeGenerator
namespace Orleans.CodeGenerator.Diagnostics;

internal static class DiagnosticRuleId
{
    public const string InaccessibleSetter = "ORLEANS0101";
    public const string InvalidRpcMethodReturnType = "ORLEANS0102";
    public const string UnhandledCodeGenerationException = "ORLEANS0103";
    public const string IncorrectProxyBaseClassSpecification = "ORLEANS0104";
    public const string RpcInterfaceProperty = "ORLEANS0105";
    public const string CanNotGenerateImplicitFieldIds = "ORLEANS0106";
    public const string InaccessibleSerializableType = "ORLEANS0107";
    public const string GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly = "ORLEANS0108";
    public const string MultipleCancellationTokenParameters = "ORLEANS0109";
    public const string ReferenceAssemblyWithGenerateSerializer = "ORLEANS0110";
}
