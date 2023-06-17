namespace Orleans.CodeGenerator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

internal class ContextGenerationSpecs
{
    public MetadataModel MetadataModel { get; set; }
    public AnalyzerConfigOptionsProvider AnalyzerConfigOptionsProvider { get; set; }

    public CodeGeneratorOptions CodeGeneratorOptions { get; set; }

    public Compilation Compilation { get; set; }

    public LibraryTypes LibraryTypes { get; set; }
}
