namespace Orleans.CodeGenerator.Generators.AliasGenerators;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal class AliasGeneratorContext : IncrementalGeneratorContext
{
    public List<(TypeSyntax Type, string Alias)> TypeAliases { get; } = new(1024);


}
