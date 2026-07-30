using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class ProxyInterfaceModelExtractor
{
    /// <summary>
    /// Extracts a <see cref="ProxyInterfaceModel"/> from a <see cref="GeneratorAttributeSyntaxContext"/>
    /// provided by the <c>ForAttributeWithMetadataName</c> incremental pipeline step for
    /// <c>[GenerateMethodSerializers]</c>-annotated interfaces.
    /// </summary>
    internal static ProxyInterfaceModel? ExtractProxyInterfaceFromAttributeContext(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol typeSymbol || typeSymbol.TypeKind != TypeKind.Interface)
        {
            return null;
        }

        return ExtractProxyInterfaceModel(typeSymbol, context.SemanticModel.Compilation, context.Attributes, cancellationToken);
    }

    internal static ProxyInterfaceModel? ExtractProxyInterfaceModel(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (typeSymbol is null || typeSymbol.TypeKind != TypeKind.Interface)
        {
            return null;
        }

        return ExtractProxyInterfaceModel(typeSymbol, compilation, [], cancellationToken);
    }

    internal static ProxyInterfaceModel? ExtractInheritedProxyInterfaceFromSyntaxContext(
        GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.Node is not InterfaceDeclarationSyntax interfaceDeclaration)
        {
            return null;
        }

        var compilation = context.SemanticModel.Compilation;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol
            || typeSymbol.TypeKind != TypeKind.Interface)
        {
            return null;
        }

        var options = new CodeGeneratorOptions();
        var libraryTypes = LibraryTypes.FromCompilation(compilation, options);
        if (typeSymbol.GetAttributes(libraryTypes.GenerateMethodSerializersAttribute, out var directAttributes, inherited: false)
            && directAttributes.Any(static attribute => TryGetProxyBaseInfo(attribute, out _, out _)))
        {
            return null;
        }

        return ExtractProxyInterfaceModel(typeSymbol, compilation, [], cancellationToken);
    }

    private static ProxyInterfaceModel? ExtractProxyInterfaceModel(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        ImmutableArray<AttributeData> candidateAttributes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = new CodeGeneratorOptions();
        var libraryTypes = LibraryTypes.FromCompilation(compilation, options);

        if (!TryGetGenerateMethodSerializersAttribute(typeSymbol, candidateAttributes, libraryTypes, out var attribute)
            || !TryGetProxyBaseInfo(attribute, out var proxyBaseTypeSymbol, out var isExtension))
        {
            return null;
        }

        var proxyBaseType = proxyBaseTypeSymbol.OriginalDefinition;
        var invokableBaseTypes = ExtractInvokableBaseTypeMappings(proxyBaseType, libraryTypes, cancellationToken);
        var generatedClassNameComponent = isExtension ? $"{proxyBaseType.Name}_Ext" : proxyBaseType.Name;
        var proxyBase = new ProxyBaseModel(
            new TypeRef(proxyBaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            isExtension,
            generatedClassNameComponent,
            invokableBaseTypes);

        var name = GetProxyInterfaceName(typeSymbol, libraryTypes);
        var typeParameters = ExtractInterfaceTypeParameters(typeSymbol);
        var methods = ExtractInterfaceMethods(typeSymbol, libraryTypes, isExtension, cancellationToken);
        var generatedNamespace = GeneratedCodeUtilities.GetGeneratedNamespaceName(typeSymbol);

        return new ProxyInterfaceModel(
            new TypeRef(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            name,
            generatedNamespace,
            typeParameters,
            proxyBase,
            methods,
            SourceLocation: SymbolSourceLocationExtractor.GetSourceLocation(typeSymbol),
            MetadataIdentity: TypeMetadataIdentity.Create(typeSymbol));
    }

    private static string GetProxyInterfaceName(INamedTypeSymbol typeSymbol, LibraryTypes libraryTypes)
    {
        var alias = typeSymbol.GetAttribute(libraryTypes.AliasAttribute);
        var name = alias is { ConstructorArguments.Length: > 0 }
            && alias.ConstructorArguments[0].Value is string aliasName
            && !string.IsNullOrWhiteSpace(aliasName)
            ? aliasName
            : typeSymbol.Name;

        if (name.IndexOfAny(['`', '.']) >= 0)
        {
            name = name.Replace('`', '_').Replace('.', '_');
        }

        return name;
    }


    private static bool TryGetGenerateMethodSerializersAttribute(
        INamedTypeSymbol typeSymbol,
        ImmutableArray<AttributeData> candidateAttributes,
        LibraryTypes libraryTypes,
        [NotNullWhen(true)] out AttributeData? attribute)
    {
        foreach (var candidate in candidateAttributes)
        {
            if (!TryGetProxyBaseInfo(candidate, out _, out _))
            {
                continue;
            }

            attribute = candidate;
            return true;
        }

        if (typeSymbol.GetAttributes(libraryTypes.GenerateMethodSerializersAttribute, out var inheritedAttributes, inherited: true))
        {
            foreach (var inheritedAttribute in inheritedAttributes)
            {
                if (!TryGetProxyBaseInfo(inheritedAttribute, out _, out _))
                {
                    continue;
                }

                attribute = inheritedAttribute;
                return true;
            }
        }

        attribute = null;
        return false;
    }

    private static bool TryGetProxyBaseInfo(AttributeData? attribute, [NotNullWhen(true)] out INamedTypeSymbol? proxyBaseTypeSymbol, out bool isExtension)
    {
        proxyBaseTypeSymbol = null;
        isExtension = false;

        if (attribute is null
            || attribute.ConstructorArguments.Length < 1
            || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol proxyBaseType)
        {
            return false;
        }

        proxyBaseTypeSymbol = proxyBaseType;
        if (attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is bool extension)
        {
            isExtension = extension;
        }

        return true;
    }

    private static ImmutableArray<InvokableBaseTypeMapping> ExtractInvokableBaseTypeMappings(
        INamedTypeSymbol proxyBaseType,
        LibraryTypes libraryTypes,
        CancellationToken cancellationToken)
    {
        if (!proxyBaseType.GetAttributes(libraryTypes.DefaultInvokableBaseTypeAttribute, out var invokableBaseTypeAttributes))
        {
            return [];
        }

        var mappings = new Dictionary<string, InvokableBaseTypeMapping>(StringComparer.Ordinal);
        foreach (var attr in invokableBaseTypeAttributes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ctorArgs = attr.ConstructorArguments;
            if (ctorArgs.Length < 2
                || ctorArgs[0].Value is not INamedTypeSymbol returnType
                || ctorArgs[1].Value is not INamedTypeSymbol invokableBaseType)
            {
                continue;
            }

            var returnTypeRef = new TypeRef(returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            var invokableBaseTypeRef = new TypeRef(invokableBaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            mappings[returnTypeRef.SyntaxString] = new InvokableBaseTypeMapping(returnTypeRef, invokableBaseTypeRef);
        }

        if (mappings.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<InvokableBaseTypeMapping>(mappings.Count);
        foreach (var mapping in mappings.OrderBy(static m => m.Key, StringComparer.Ordinal))
        {
            builder.Add(mapping.Value);
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<TypeParameterModel> ExtractInterfaceTypeParameters(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeParameters.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeParameterModel>(typeSymbol.TypeParameters.Length);
        foreach (var tp in typeSymbol.TypeParameters)
        {
            builder.Add(new TypeParameterModel(tp.Name, tp.Name, tp.Ordinal));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<MethodModel> ExtractInterfaceMethods(
        INamedTypeSymbol interfaceType,
        LibraryTypes libraryTypes,
        bool isExtension,
        CancellationToken cancellationToken)
    {
        var methods = new SortedDictionary<string, MethodModel>(StringComparer.Ordinal);

        foreach (var iface in GetAllInterfaces(interfaceType))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var member in iface.GetDeclaredInstanceMembers<IMethodSymbol>())
            {
                if (member.MethodKind == MethodKind.ExplicitInterfaceImplementation)
                {
                    continue;
                }

                var originalMethod = member.OriginalDefinition;
                var methodIdentity = GetMethodIdentity(
                    isExtension ? interfaceType : originalMethod.ContainingType,
                    originalMethod);
                if (methods.ContainsKey(methodIdentity))
                {
                    continue;
                }

                var containingInterface = isExtension ? interfaceType : originalMethod.ContainingType;
                var methodModel = ExtractMethodModel(member, originalMethod, containingInterface, libraryTypes);
                if (methodModel is not null)
                {
                    methods.Add(methodIdentity, methodModel);
                }
            }
        }

        if (methods.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<MethodModel>(methods.Count);
        foreach (var method in methods.Values)
        {
            builder.Add(method);
        }

        return builder.MoveToImmutable();
    }

    private static IEnumerable<INamedTypeSymbol> GetAllInterfaces(INamedTypeSymbol symbol)
    {
        if (symbol.TypeKind == TypeKind.Interface)
        {
            yield return symbol;
        }

        foreach (var iface in symbol.AllInterfaces)
        {
            yield return iface;
        }
    }

    private static string GetMethodIdentity(INamedTypeSymbol containingType, IMethodSymbol method)
    {
        var builder = new StringBuilder();
        builder.Append(containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        builder.Append("::");
        builder.Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        builder.Append('.');
        builder.Append(method.Name);
        builder.Append('`');
        builder.Append(method.Arity);
        builder.Append('(');

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(method.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        builder.Append(')');
        builder.Append("->");
        builder.Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        return builder.ToString();
    }

    private static MethodModel ExtractMethodModel(
        IMethodSymbol method,
        IMethodSymbol originalMethod,
        INamedTypeSymbol containingInterface,
        LibraryTypes libraryTypes)
    {
        var generatedMethodId = GeneratedCodeUtilities.CreateHashedMethodId(originalMethod);

        // Determine method ID: explicit ID → alias → generated hash
        string methodId;
        var idValue = GeneratedCodeUtilities.GetId(libraryTypes, originalMethod);
        if (idValue.HasValue)
        {
            methodId = idValue.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            var aliasAttr = originalMethod.GetAttribute(libraryTypes.AliasAttribute);
            methodId = aliasAttr is not null && aliasAttr.ConstructorArguments.Length > 0
                ? (string?)aliasAttr.ConstructorArguments[0].Value ?? generatedMethodId
                : generatedMethodId;
        }

        var parameters = ExtractMethodParameters(method, libraryTypes);
        var typeParameters = ExtractMethodTypeParameters(method);
        var returnType = new TypeRef(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        // Response timeout
        long? responseTimeoutTicks = null;
        var timeoutAttr = originalMethod.GetAttribute(libraryTypes.ResponseTimeoutAttribute);
        if (timeoutAttr is not null
            && timeoutAttr.ConstructorArguments.Length > 0
            && timeoutAttr.ConstructorArguments[0].Value is string timeoutStr)
        {
            if (TimeSpan.TryParse(timeoutStr, out var timeout))
            {
                responseTimeoutTicks = timeout.Ticks;
            }
        }

        var customInitializers = ExtractCustomInitializers(originalMethod, libraryTypes);

        var isCancellable = false;
        foreach (var param in method.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(libraryTypes.CancellationToken, param.Type))
            {
                isCancellable = true;
                break;
            }
        }

        return new MethodModel(
            method.Name,
            returnType,
            parameters,
            typeParameters,
            new TypeRef(containingInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            new TypeRef(originalMethod.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            containingInterface.Name,
            GeneratedCodeUtilities.GetGeneratedNamespaceName(containingInterface),
            containingInterface.GetAllTypeParameters().Count(),
            generatedMethodId,
            methodId,
            responseTimeoutTicks,
            customInitializers,
            isCancellable);
    }

    private static ImmutableArray<MethodParameterModel> ExtractMethodParameters(
        IMethodSymbol method,
        LibraryTypes libraryTypes)
    {
        if (method.Parameters.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<MethodParameterModel>(method.Parameters.Length);
        foreach (var param in method.Parameters)
        {
            var isCancellationToken = SymbolEqualityComparer.Default.Equals(libraryTypes.CancellationToken, param.Type);
            builder.Add(new MethodParameterModel(
                param.Name,
                new TypeRef(param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                param.Ordinal,
                isCancellationToken));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<TypeParameterModel> ExtractMethodTypeParameters(IMethodSymbol method)
    {
        if (method.TypeParameters.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeParameterModel>(method.TypeParameters.Length);
        foreach (var tp in method.TypeParameters)
        {
            builder.Add(new TypeParameterModel(tp.Name, tp.Name, tp.Ordinal));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<CustomInitializerModel> ExtractCustomInitializers(
        IMethodSymbol method,
        LibraryTypes libraryTypes)
    {
        ImmutableArray<CustomInitializerModel>.Builder? builder = null;

        foreach (var methodAttr in method.GetAttributes())
        {
            if (methodAttr.AttributeClass is null)
            {
                continue;
            }

            if (methodAttr.AttributeClass.GetAttributes(libraryTypes.InvokableCustomInitializerAttribute, out var attrs))
            {
                foreach (var attr in attrs)
                {
                    if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is not string methodName)
                    {
                        continue;
                    }

                    string? argumentValue = null;

                    if (attr.ConstructorArguments.Length == 2)
                    {
                        argumentValue = attr.ConstructorArguments[1].Value?.ToString();
                    }
                    else
                    {
                        if (TryGetNamedArgument(attr.NamedArguments, "AttributeArgumentName", out var argNameArg)
                            && argNameArg.Value is string attributeArgumentName
                            && TryGetNamedArgument(methodAttr.NamedArguments, attributeArgumentName, out var namedArgument))
                        {
                            argumentValue = namedArgument.Value?.ToString();
                        }
                        else
                        {
                            var index = 0;
                            if (TryGetNamedArgument(attr.NamedArguments, "AttributeArgumentIndex", out var indexArg))
                            {
                                index = indexArg.Value is int value ? value : index;
                            }

                            if (methodAttr.ConstructorArguments.Length > index)
                            {
                                argumentValue = methodAttr.ConstructorArguments[index].Value?.ToString();
                            }
                        }
                    }

                    builder ??= ImmutableArray.CreateBuilder<CustomInitializerModel>();
                    builder.Add(new CustomInitializerModel(methodName ?? string.Empty, argumentValue ?? string.Empty));
                }
            }
        }

        return builder is not null
            ? builder.ToImmutable()
            : [];
    }

    private static bool TryGetNamedArgument(
        ImmutableArray<KeyValuePair<string, TypedConstant>> arguments,
        string name,
        out TypedConstant value)
    {
        foreach (var arg in arguments)
        {
            if (string.Equals(arg.Key, name, StringComparison.Ordinal))
            {
                value = arg.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}


