namespace Orleans.CodeGenerator.Generators.SerializerGenerators;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal class SerializerGeneratorContext : IncrementalGeneratorContext
{
    public List<ISerializableTypeDescription> SerializableTypes { get; } = new(1024);
    public List<InvokableInterfaceDescription> InvokableInterfaces { get; } = new(1024);
    public List<INamedTypeSymbol> InvokableInterfaceImplementations { get; } = new(1024);
    public Dictionary<MethodDescription, GeneratedInvokerDescription> GeneratedInvokables { get; } = new();
    public List<GeneratedProxyDescription> GeneratedProxies { get; } = new(1024);
    public List<ISerializableTypeDescription> ActivatableTypes { get; } = new(1024);
    public List<INamedTypeSymbol> DetectedSerializers { get; } = new();
    public List<INamedTypeSymbol> DetectedActivators { get; } = new();
    public Dictionary<ISerializableTypeDescription, TypeSyntax> DefaultCopiers { get; } = new();
    public List<INamedTypeSymbol> DetectedCopiers { get; } = new();
    public List<INamedTypeSymbol> DetectedConverters { get; } = new();

    public LibraryTypes LibraryTypes { get; set; }

    public CompoundTypeAliasTree CompoundTypeAliases { get; } = CompoundTypeAliasTree.Create();


}
