namespace Orleans.CodeGenerator.Generators.ApplicationPartsGenerator;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

internal class MetadataGeneratorContext : IncrementalGeneratorContext
{
    public HashSet<string> ApplicationParts { get; } = new();

    public INamedTypeSymbol ApplicationPartAttribute { get; set; }

    public INamedTypeSymbol TypeManifestProviderAttribute { get; set; }
}
