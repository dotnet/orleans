using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class ReferenceAssemblyModelExtractor
{
    /// <summary>
    /// Extracts reference-assembly metadata from the compilation using the provided code generation options.
    /// This isolates reference-assembly scanning into a cacheable pipeline step so that
    /// downstream work can be skipped when references don't change.
    /// </summary>
    internal static ReferenceAssemblyModel ExtractReferenceAssemblyData(
        Compilation compilation,
        CodeGeneratorOptions options,
        CancellationToken cancellationToken)
        => ExtractReferenceAssemblyData(compilation, options, cancellationToken, out _);

    internal static ReferenceAssemblyModel ExtractReferenceAssemblyData(
        Compilation compilation,
        CodeGeneratorOptions options,
        CancellationToken cancellationToken,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        var libraryTypes = LibraryTypes.FromCompilation(compilation, options);

        var applicationParts = new List<string>();
        var applicationPartSet = new HashSet<string>(StringComparer.Ordinal);
        AddApplicationPart(compilation.Assembly.MetadataName);

        var assembliesToExamine = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        ComputeAssembliesToExamine(
            compilation.Assembly,
            assembliesToExamine,
            libraryTypes.GenerateCodeForDeclaringAssemblyAttribute,
            cancellationToken);

        var wellKnownTypeIds = new HashSet<WellKnownTypeIdModel>();
        var typeAliases = new HashSet<TypeAliasModel>();
        var compoundTypeAliases = new HashSet<CompoundTypeAliasModel>();
        var referencedSerializableTypes = new HashSet<SerializableTypeModel>();
        var referencedProxyInterfaces = new HashSet<ProxyInterfaceModel>();
        var registeredCodecs = new HashSet<RegisteredCodecModel>();
        var interfaceImplementations = new HashSet<InterfaceImplementationModel>();
        var diagnosticBuilder = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (var reference in compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm)
            {
                continue;
            }

            if (!asm.GetAttributes(libraryTypes.ApplicationPartAttribute, out var attrs))
            {
                continue;
            }

            AddApplicationPart(asm.MetadataName);
            foreach (var attr in attrs)
            {
                if (attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string partName)
                {
                    AddApplicationPart(partName);
                }
            }
        }

        foreach (var asm in assembliesToExamine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var reportSerializableTypeDiagnostics = !SymbolEqualityComparer.Default.Equals(asm, compilation.Assembly);
            foreach (var symbol in asm.GetDeclaredTypes())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (SerializableTypeModelExtractor.TryExtractSerializableTypeModel(symbol, compilation, libraryTypes, options, reportSerializableTypeDiagnostics) is { } serializableTypeModel)
                    {
                        referencedSerializableTypes.Add(serializableTypeModel);
                    }
                }
                catch (OrleansGeneratorDiagnosticAnalysisException exception) when (reportSerializableTypeDiagnostics)
                {
                    diagnosticBuilder.Add(exception.Diagnostic);
                }

                if (ProxyInterfaceModelExtractor.ExtractProxyInterfaceModel(symbol, compilation, cancellationToken) is { } proxyInterfaceModel)
                {
                    referencedProxyInterfaces.Add(proxyInterfaceModel);
                }

                var typeRef = new TypeRef(symbol.ToOpenTypeSyntax().ToString());
                if (GeneratedCodeUtilities.GetId(libraryTypes, symbol) is uint wellKnownTypeId)
                {
                    wellKnownTypeIds.Add(new WellKnownTypeIdModel(typeRef, wellKnownTypeId));
                }

                if (symbol.GetAttribute(libraryTypes.AliasAttribute) is { ConstructorArguments.Length: > 0 } aliasAttr
                    && aliasAttr.ConstructorArguments[0].Value is string alias)
                {
                    typeAliases.Add(new TypeAliasModel(typeRef, alias));
                }

                if (TryExtractCompoundTypeAlias(symbol, libraryTypes.CompoundTypeAliasAttribute, out var components))
                {
                    compoundTypeAliases.Add(new CompoundTypeAliasModel(components, typeRef));
                }

                if ((symbol.TypeKind == TypeKind.Class || symbol.TypeKind == TypeKind.Struct)
                    && !symbol.IsAbstract
                    && (symbol.DeclaredAccessibility == Accessibility.Public || symbol.DeclaredAccessibility == Accessibility.Internal))
                {
                    if (symbol.HasAttribute(libraryTypes.RegisterSerializerAttribute))
                    {
                        registeredCodecs.Add(new RegisteredCodecModel(typeRef, RegisteredCodecKind.Serializer));
                    }

                    if (symbol.HasAttribute(libraryTypes.RegisterCopierAttribute))
                    {
                        registeredCodecs.Add(new RegisteredCodecModel(typeRef, RegisteredCodecKind.Copier));
                    }

                    if (symbol.HasAttribute(libraryTypes.RegisterActivatorAttribute))
                    {
                        registeredCodecs.Add(new RegisteredCodecModel(typeRef, RegisteredCodecKind.Activator));
                    }

                    if (symbol.HasAttribute(libraryTypes.RegisterConverterAttribute))
                    {
                        registeredCodecs.Add(new RegisteredCodecModel(typeRef, RegisteredCodecKind.Converter));
                    }

                    foreach (var iface in symbol.AllInterfaces)
                    {
                        if (iface.GetAttribute(libraryTypes.GenerateMethodSerializersAttribute, inherited: true) is not null)
                        {
                        interfaceImplementations.Add(new InterfaceImplementationModel(typeRef, SymbolSourceLocationExtractor.GetSourceLocation(symbol)));
                            break;
                        }
                    }
                }
            }
        }

        var orderedApplicationParts = applicationParts.ToImmutableArray();

        var sortedWellKnownTypeIds = wellKnownTypeIds
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Id)
            .ToImmutableArray();

        var sortedTypeAliases = typeAliases
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Alias, StringComparer.Ordinal)
            .ToImmutableArray();

        var sortedCompoundTypeAliases = compoundTypeAliases
            .OrderBy(static entry => GetCompoundTypeAliasOrderKey(entry), StringComparer.Ordinal)
            .ThenBy(static entry => entry.TargetType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        var sortedReferencedSerializableTypes = MetadataAggregateModelBuilder.OrderSerializableTypeModels(referencedSerializableTypes)
            .ToImmutableArray();

        var sortedReferencedProxyInterfaces = MetadataAggregateModelBuilder.OrderProxyInterfaceModels(referencedProxyInterfaces)
            .ToImmutableArray();

        var sortedRegisteredCodecs = registeredCodecs
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Kind)
            .ToImmutableArray();

        var sortedInterfaceImplementations = interfaceImplementations
            .OrderBy(static entry => entry.ImplementationType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        diagnostics = diagnosticBuilder.ToImmutable();

        return new ReferenceAssemblyModel(
            AssemblyName: compilation.AssemblyName ?? string.Empty,
            ApplicationParts: orderedApplicationParts,
            WellKnownTypeIds: sortedWellKnownTypeIds,
            TypeAliases: sortedTypeAliases,
            CompoundTypeAliases: sortedCompoundTypeAliases,
            ReferencedSerializableTypes: sortedReferencedSerializableTypes,
            ReferencedProxyInterfaces: sortedReferencedProxyInterfaces,
            RegisteredCodecs: sortedRegisteredCodecs,
            InterfaceImplementations: sortedInterfaceImplementations);

        void AddApplicationPart(string applicationPart)
        {
            if (applicationPartSet.Add(applicationPart))
            {
                applicationParts.Add(applicationPart);
            }
        }
    }

    private static void ComputeAssembliesToExamine(
        IAssemblySymbol asm,
        HashSet<IAssemblySymbol> expandedAssemblies,
        INamedTypeSymbol generateCodeForDeclaringAssemblyAttribute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!expandedAssemblies.Add(asm))
        {
            return;
        }

        if (!asm.GetAttributes(generateCodeForDeclaringAssemblyAttribute, out var attrs))
        {
            return;
        }

        foreach (var attr in attrs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attr.ConstructorArguments.Length != 1)
            {
                continue;
            }

            var argument = attr.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Type || argument.Value is not ITypeSymbol type)
            {
                continue;
            }

            var declaringAssembly = type.OriginalDefinition.ContainingAssembly;
            if (declaringAssembly is null)
            {
                continue;
            }

            ComputeAssembliesToExamine(
                declaringAssembly,
                expandedAssemblies,
                generateCodeForDeclaringAssemblyAttribute,
                cancellationToken);
        }
    }

    private static bool TryExtractCompoundTypeAlias(
        INamedTypeSymbol symbol,
        INamedTypeSymbol compoundTypeAliasAttribute,
        out ImmutableArray<CompoundAliasComponentModel> components)
    {
        var attr = symbol.GetAttribute(compoundTypeAliasAttribute);
        if (attr is null)
        {
            components = [];
            return false;
        }

        var allArgs = attr.ConstructorArguments;
        var attributeName = attr.AttributeClass?.Name ?? "unknown";
        var constructorArguments = string.Join(", ", allArgs.Select(static argument => argument.ToString()));
        if (allArgs.Length != 1 || allArgs[0].Values.Length == 0)
        {
            throw new ArgumentException($"Unsupported arguments in attribute [{attributeName}({constructorArguments})]");
        }

        var args = allArgs[0].Values;
        var result = ImmutableArray.CreateBuilder<CompoundAliasComponentModel>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.IsNull)
            {
                throw new ArgumentNullException($"Unsupported null argument in attribute [{attributeName}({constructorArguments})]");
            }

            result.Add(arg.Value switch
            {
                ITypeSymbol type => new CompoundAliasComponentModel(new TypeRef(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
                string str => new CompoundAliasComponentModel(str),
                _ => throw new ArgumentException($"Unrecognized argument type for argument {arg} in attribute [{attributeName}({constructorArguments})]"),
            });
        }

        components = result.MoveToImmutable();
        return true;
    }

    internal static string GetCompoundTypeAliasOrderKey(CompoundTypeAliasModel entry)
    {
        if (entry.Components.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\u001F",
            entry.Components.Select(static component => component.IsString
                ? $"S:{component.StringValue ?? string.Empty}"
                : component.IsType
                    ? $"T:{component.TypeValue.SyntaxString}"
                    : string.Empty));
    }

    /// <summary>
    /// Extracts a <see cref="RegisteredCodecModel"/> from a symbol with one of the Register* attributes.
    /// </summary>
    internal static RegisteredCodecModel ExtractRegisteredCodec(INamedTypeSymbol symbol, RegisteredCodecKind kind)
    {
        return new RegisteredCodecModel(
            new TypeRef(symbol.ToOpenTypeSyntax().ToString()),
            kind);
    }
}


