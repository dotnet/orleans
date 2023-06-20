namespace Orleans.CodeGenerator.Generators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal abstract class EmitterBase
{
    protected readonly SourceProductionContext _sourceProductionContext;

    public EmitterBase(SourceProductionContext context)
    {
        _sourceProductionContext = context;
    }

    public abstract void Emit();

    protected static string ConvertIntoString(CompilationUnitSyntax compilationUnitSyntax)
    {
        return compilationUnitSyntax.NormalizeWhitespace().ToFullString();

    }

    public void AddSource(string fileName, string content)
    {

        _sourceProductionContext.AddSource(fileName + ".g.cs", content);

    }
}
