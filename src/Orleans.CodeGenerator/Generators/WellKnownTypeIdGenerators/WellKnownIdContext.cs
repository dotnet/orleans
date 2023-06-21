namespace Orleans.CodeGenerator.Generators.WellKnownIdGenerators;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal class WellKnownIdGeneratorContext : IncrementalGeneratorContext
{
    public List<(TypeSyntax Type, uint Id)> WellKnownTypeIds { get; } = new(1024);


}
