using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal sealed class ProxyGenerationContext : IGeneratorServices
{
    private readonly Dictionary<string, List<MemberDeclarationSyntax>> _namespacedMembers = new();
    private readonly Dictionary<InvokableMethodId, InvokableMethodDescription> _invokableMethodDescriptions = new();
    private readonly HashSet<INamedTypeSymbol> _visitedInterfaces = new(SymbolEqualityComparer.Default);

    internal ProxyGenerationContext(Compilation compilation, CodeGeneratorOptions options)
        : this(compilation, options, LibraryTypes.FromCompilation(compilation, options))
    {
    }

    internal ProxyGenerationContext(Compilation compilation, CodeGeneratorOptions options, LibraryTypes libraryTypes)
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        LibraryTypes = libraryTypes ?? throw new ArgumentNullException(nameof(libraryTypes));
        MetadataModel = new MetadataModel();
        ProxyGenerator = new ProxyGenerator(this, new CopierGenerator(this));
        InvokableGenerator = new InvokableGenerator(this);
    }

    public Compilation Compilation { get; }
    public CodeGeneratorOptions Options { get; }
    internal LibraryTypes LibraryTypes { get; }
    LibraryTypes IGeneratorServices.LibraryTypes => LibraryTypes;
    internal MetadataModel MetadataModel { get; }
    internal ProxyGenerator ProxyGenerator { get; }
    internal InvokableGenerator InvokableGenerator { get; }

    internal void AddMember(string ns, MemberDeclarationSyntax member)
    {
        if (!_namespacedMembers.TryGetValue(ns, out var existing))
        {
            existing = _namespacedMembers[ns] = new List<MemberDeclarationSyntax>();
        }

        existing.Add(member);
    }

    internal IEnumerable<(string Namespace, MemberDeclarationSyntax Member)> GetEmittedMembers()
    {
        foreach (var entry in _namespacedMembers)
        {
            foreach (var member in entry.Value)
            {
                yield return (entry.Key, member);
            }
        }
    }

    internal uint? GetId(ISymbol memberSymbol) => GeneratedCodeUtilities.GetId(LibraryTypes, memberSymbol);

    internal string? GetAlias(ISymbol symbol) => GeneratedCodeUtilities.GetAlias(LibraryTypes, symbol);

    internal void VisitInterface(INamedTypeSymbol interfaceType)
    {
        // Get or generate an invokable for the original method definition.
        if (!SymbolEqualityComparer.Default.Equals(interfaceType, interfaceType.OriginalDefinition))
        {
            interfaceType = interfaceType.OriginalDefinition;
        }

        if (!_visitedInterfaces.Add(interfaceType))
        {
            return;
        }

        foreach (var proxyBase in GetProxyBases(interfaceType))
        {
            _ = GetInvokableInterfaceDescription(proxyBase.ProxyBaseType, interfaceType);
        }
    }

    internal bool TryGetInvokableInterfaceDescription(INamedTypeSymbol interfaceType, [NotNullWhen(true)] out ProxyInterfaceDescription? result)
    {
        if (!TryGetProxyBaseDescription(interfaceType, out var description))
        {
            result = null;
            return false;
        }

        result = GetInvokableInterfaceDescription(description.ProxyBaseType, interfaceType);
        return true;
    }

    private readonly Dictionary<INamedTypeSymbol, List<InvokableMethodProxyBase>> _interfaceProxyBases = new(SymbolEqualityComparer.Default);
    internal List<InvokableMethodProxyBase> GetProxyBases(INamedTypeSymbol interfaceType)
    {
        if (_interfaceProxyBases.TryGetValue(interfaceType, out var result))
        {
            return result;
        }

        result = new List<InvokableMethodProxyBase>();
        if (interfaceType.GetAttributes(LibraryTypes.GenerateMethodSerializersAttribute, out var attributes, inherited: true))
        {
            foreach (var attribute in attributes)
            {
                var proxyBase = GetProxyBaseDescription(attribute);
                if (!result.Contains(proxyBase))
                {
                    result.Add(proxyBase);
                }
            }
        }

        _interfaceProxyBases[interfaceType] = result;
        return result;
    }

    internal bool TryGetProxyBaseDescription(INamedTypeSymbol interfaceType, [NotNullWhen(true)] out InvokableMethodProxyBase? result)
    {
        var attribute = interfaceType.GetAttribute(LibraryTypes.GenerateMethodSerializersAttribute, inherited: true);
        if (attribute == null)
        {
            result = null;
            return false;
        }

        result = GetProxyBaseDescription(attribute);
        return true;
    }

    private InvokableMethodProxyBase GetProxyBaseDescription(AttributeData attribute)
    {
        var proxyBaseType = ((INamedTypeSymbol)attribute.ConstructorArguments[0].Value!).OriginalDefinition;
        var isExtension = (bool)attribute.ConstructorArguments[1].Value!;
        var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseType);
        var descriptor = new InvokableMethodProxyBaseId(proxyBaseType, isExtension);
        var description = new InvokableMethodProxyBase(this, descriptor, invokableBaseTypes);
        return description;

        Dictionary<INamedTypeSymbol, INamedTypeSymbol> GetInvokableBaseTypes(INamedTypeSymbol baseClass)
        {
            // Set the base invokable types which are used if attributes on individual methods do not override them.
            if (!MetadataModel.ProxyBaseTypeInvokableBaseTypes.TryGetValue(baseClass, out var invokableBaseTypes))
            {
                invokableBaseTypes = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
                if (baseClass.GetAttributes(LibraryTypes.DefaultInvokableBaseTypeAttribute, out var invokableBaseTypeAttributes))
                {
                    foreach (var attr in invokableBaseTypeAttributes)
                    {
                        var ctorArgs = attr.ConstructorArguments;
                        var returnType = (INamedTypeSymbol)ctorArgs[0].Value!;
                        var invokableBaseType = (INamedTypeSymbol)ctorArgs[1].Value!;
                        invokableBaseTypes[returnType] = invokableBaseType;
                    }
                }

                MetadataModel.ProxyBaseTypeInvokableBaseTypes[baseClass] = invokableBaseTypes;
            }

            return invokableBaseTypes;
        }
    }

    internal InvokableMethodProxyBase GetProxyBase(INamedTypeSymbol interfaceType)
    {
        if (!TryGetProxyBaseDescription(interfaceType, out var result))
        {
            throw new InvalidOperationException($"Cannot get proxy base description for a type which does not have or inherit [{nameof(LibraryTypes.GenerateMethodSerializersAttribute)}]");
        }

        return result;
    }

    private ProxyInterfaceDescription GetInvokableInterfaceDescription(INamedTypeSymbol proxyBaseType, INamedTypeSymbol interfaceType)
    {
        var originalInterface = interfaceType.OriginalDefinition;
        if (MetadataModel.InvokableInterfaces.TryGetValue(originalInterface, out var description))
        {
            return description;
        }

        description = new ProxyInterfaceDescription(this, proxyBaseType, originalInterface);
        MetadataModel.InvokableInterfaces.Add(originalInterface, description);

        // Generate a proxy.
        var (generatedClass, proxyDescription) = ProxyGenerator.Generate(description);

        // Emit the generated proxy
        if (Compilation.GetTypeByMetadataName(proxyDescription.MetadataName) == null)
        {
            AddMember(proxyDescription.InterfaceDescription.GeneratedNamespace, generatedClass);
        }

        return description;
    }

    internal ProxyMethodDescription GetProxyMethodDescription(INamedTypeSymbol interfaceType, IMethodSymbol method)
    {
        var originalMethod = method.OriginalDefinition;
        var proxyBaseInfo = GetProxyBase(interfaceType);

        // For extensions, we want to ensure that the containing type is always the extension.
        // This ensures that we will always know which 'component' to get in our SetTarget method.
        // If the type is not an extension, use the original method definition's containing type.
        // This is the interface where the type was originally defined.
        var containingType = proxyBaseInfo.IsExtension ? interfaceType : originalMethod.ContainingType;

        var invokableId = new InvokableMethodId(proxyBaseInfo, containingType, originalMethod);
        var interfaceDescription = GetInvokableInterfaceDescription(invokableId.ProxyBase.ProxyBaseType, interfaceType);

        // Get or generate an invokable for the original method definition.
        if (!MetadataModel.GeneratedInvokables.TryGetValue(invokableId, out var generatedInvokable))
        {
            if (!_invokableMethodDescriptions.TryGetValue(invokableId, out var methodDescription))
            {
                methodDescription = _invokableMethodDescriptions[invokableId] = InvokableMethodDescription.Create(invokableId, containingType);
            }

            generatedInvokable = MetadataModel.GeneratedInvokables[invokableId] = InvokableGenerator.Generate(methodDescription);

            if (Compilation.GetTypeByMetadataName(generatedInvokable.MetadataName) == null)
            {
                // Emit the generated code on-demand.
                AddMember(generatedInvokable.GeneratedNamespace, generatedInvokable.ClassDeclarationSyntax);
            }
        }

        var proxyMethodDescription = ProxyMethodDescription.Create(interfaceDescription, generatedInvokable, method);

        // For backwards compatibility, generate invokers for the specific implementation types as well, where they differ.
        if (Options.GenerateCompatibilityInvokers && !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType, interfaceType))
        {
            var compatInvokableId = new InvokableMethodId(proxyBaseInfo, interfaceType, method);
            var compatMethodDescription = InvokableMethodDescription.Create(compatInvokableId, interfaceType);
            var compatInvokable = InvokableGenerator.Generate(compatMethodDescription);
            AddMember(compatInvokable.GeneratedNamespace, compatInvokable.ClassDeclarationSyntax);
        }

        return proxyMethodDescription;
    }
}
