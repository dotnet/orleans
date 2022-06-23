using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal class MetadataModel
    {
        public List<ISerializableTypeDescription> SerializableTypes { get; } = new(1024);
        public List<InvokableInterfaceDescription> InvokableInterfaces { get; } = new(1024);
        public List<INamedTypeSymbol> InvokableInterfaceImplementations { get; } = new(1024);
        public Dictionary<MethodDescription, GeneratedInvokerDescription> GeneratedInvokables { get; } = new();
        public List<GeneratedProxyDescription> GeneratedProxies { get; } = new(1024);
        public List<ISerializableTypeDescription> ActivatableTypes { get; } = new(1024);
        public List<INamedTypeSymbol> DetectedSerializers { get; } = new();
        public List<INamedTypeSymbol> DetectedActivators { get; } = new();
        public List<INamedTypeSymbol> DetectedCopiers { get; } = new();
        public List<INamedTypeSymbol> DetectedConverters { get; } = new();
        public List<(TypeSyntax Type, string Alias)> TypeAliases { get; } = new(1024);
        public List<(TypeSyntax Type, uint Id)> WellKnownTypeIds { get; } = new(1024);
        public HashSet<string> ApplicationParts { get; } = new();
    }
}