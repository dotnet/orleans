using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

internal static class InvalidGrainReferenceFactoryDiagnostic
{
    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticRuleId.InvalidGrainReferenceFactory,
        "Invalid grain-reference factory",
        "GenerateGrainReference requires an accessible concrete grain interface with a (GrainReferenceShared, IdSpan) proxy constructor",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
