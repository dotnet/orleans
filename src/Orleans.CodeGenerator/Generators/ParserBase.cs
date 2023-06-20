namespace Orleans.CodeGenerator.Generators;
using System;
using System.Threading;
using Microsoft.CodeAnalysis;

internal abstract class ParserBase
{
    protected Compilation compilation;

    public ParserBase(Compilation compilation)
    {
        this.compilation = compilation;
    }

    protected INamedTypeSymbol Type(string metadataName)
    {
        var result = compilation.GetTypeByMetadataName(metadataName);
        if (result is null)
        {
            throw new InvalidOperationException("Cannot find type with metadata name " + metadataName);
        }

        return result;
    }


    public abstract IncrementalGeneratorContext Parse(CancellationToken token);
}
