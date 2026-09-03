using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

/// <summary>
/// Represents a code-generation analysis failure which should be reported as a compiler diagnostic.
/// </summary>
/// <param name="diagnostic">The diagnostic which describes the failure.</param>
public class OrleansGeneratorDiagnosticAnalysisException(Diagnostic diagnostic) : Exception(diagnostic.GetMessage())
{
    /// <summary>
    /// Gets the diagnostic which describes the failure.
    /// </summary>
    public Diagnostic Diagnostic { get; } = diagnostic;
}
