using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

public class OrleansGeneratorDiagnosticAnalysisException(Diagnostic diagnostic) : Exception(diagnostic.GetMessage())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}
