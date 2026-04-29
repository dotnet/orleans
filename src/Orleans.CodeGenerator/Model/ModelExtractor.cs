using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

/// <summary>
/// Extracts <see cref="SerializableTypeModel"/> and other incremental pipeline models
/// from Roslyn symbols, producing value-type representations suitable for pipeline caching.
/// </summary>
internal static class ModelExtractor
{
    /// <summary>
    /// Extracts a <see cref="SerializableTypeModel"/> from an <see cref="ISerializableTypeDescription"/>.
    /// Converts symbol-based descriptions into equatable value models for incremental pipeline caching.
    /// </summary>
    public static SerializableTypeModel ExtractSerializableTypeModel(
        ISerializableTypeDescription description,
        SourceLocationModel sourceLocation = default)
    {
        var typeParameters = ExtractTypeParameters(description.TypeParameters);
        var members = ExtractMembers(description.Members);
        var serializationHooks = ExtractTypeRefs(description.SerializationHooks);
        var activatorCtorParams = ExtractTypeRefSyntaxList(description.ActivatorConstructorParameters);
        var creationStrategy = DetermineCreationStrategy(description);

        return new SerializableTypeModel(
            accessibility: description.Accessibility,
            typeSyntax: new TypeRef(description.TypeSyntax.ToString()),
            hasComplexBaseType: description.HasComplexBaseType,
            includePrimaryConstructorParameters: description.IncludePrimaryConstructorParameters,
            baseTypeSyntax: description.HasComplexBaseType ? new TypeRef(description.BaseTypeSyntax.ToString()) : TypeRef.Empty,
            ns: description.Namespace ?? string.Empty,
            generatedNamespace: description.GeneratedNamespace ?? string.Empty,
            name: description.Name ?? string.Empty,
            isValueType: description.IsValueType,
            isSealedType: description.IsSealedType,
            isAbstractType: description.IsAbstractType,
            isEnumType: description.IsEnumType,
            isGenericType: description.IsGenericType,
            typeParameters: typeParameters,
            members: members,
            useActivator: description.UseActivator,
            isEmptyConstructable: description.IsEmptyConstructable,
            hasActivatorConstructor: description.HasActivatorConstructor,
            trackReferences: description.TrackReferences,
            omitDefaultMemberValues: description.OmitDefaultMemberValues,
            serializationHooks: serializationHooks,
            isShallowCopyable: description.IsShallowCopyable,
            isUnsealedImmutable: description.IsUnsealedImmutable,
            isImmutable: description.IsImmutable,
            isExceptionType: description.IsExceptionType,
            activatorConstructorParameters: activatorCtorParams,
            creationStrategy: creationStrategy,
            sourceLocation: sourceLocation,
            metadataIdentity: description is SerializableTypeDescription serializableDescription
                ? TypeMetadataIdentity.Create(serializableDescription.Type)
                : TypeMetadataIdentity.Empty);
    }

    /// <summary>
    /// Extracts reference-assembly metadata from the compilation using the provided code generation options.
    /// This isolates reference-assembly scanning into a cacheable pipeline step so that
    /// downstream work can be skipped when references don't change.
    /// </summary>
    public static ReferenceAssemblyModel ExtractReferenceAssemblyData(
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
                    if (TryExtractSerializableTypeModel(symbol, compilation, libraryTypes, options, reportSerializableTypeDiagnostics) is { } serializableTypeModel)
                    {
                        referencedSerializableTypes.Add(serializableTypeModel);
                    }
                }
                catch (OrleansGeneratorDiagnosticAnalysisException exception) when (reportSerializableTypeDiagnostics)
                {
                    diagnosticBuilder.Add(exception.Diagnostic);
                }

                if (ExtractProxyInterfaceModel(symbol, compilation, cancellationToken) is { } proxyInterfaceModel)
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
                        interfaceImplementations.Add(new InterfaceImplementationModel(typeRef, GetSourceLocation(symbol)));
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

        var sortedReferencedSerializableTypes = OrderSerializableTypeModels(referencedSerializableTypes)
            .ToImmutableArray();

        var sortedReferencedProxyInterfaces = OrderProxyInterfaceModels(referencedProxyInterfaces)
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
            assemblyName: compilation.AssemblyName ?? string.Empty,
            applicationParts: orderedApplicationParts,
            wellKnownTypeIds: sortedWellKnownTypeIds,
            typeAliases: sortedTypeAliases,
            compoundTypeAliases: sortedCompoundTypeAliases,
            referencedSerializableTypes: sortedReferencedSerializableTypes,
            referencedProxyInterfaces: sortedReferencedProxyInterfaces,
            registeredCodecs: sortedRegisteredCodecs,
            interfaceImplementations: sortedInterfaceImplementations);

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

    private static string GetCompoundTypeAliasOrderKey(CompoundTypeAliasModel entry)
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
    /// Creates a <see cref="MetadataAggregateModel"/> from the collected pipeline outputs.
    /// This provides a single equality checkpoint so that downstream generation can be
    /// skipped when no upstream pipeline has changed.
    /// </summary>
    public static MetadataAggregateModel CreateMetadataAggregate(
        string assemblyName,
        ImmutableArray<SerializableTypeModel> serializableTypes,
        ImmutableArray<ProxyInterfaceModel> proxyInterfaces,
        ReferenceAssemblyModel refData)
        => CreateMetadataAggregate(
            assemblyName,
            serializableTypes,
            proxyInterfaces.IsDefaultOrEmpty
                ? []
                : proxyInterfaces.Select(static proxy => new ProxyOutputModel(proxy, [], useDeclaredInvokableFallback: false)).ToImmutableArray(),
            refData);

    public static MetadataAggregateModel CreateMetadataAggregate(
        string assemblyName,
        ImmutableArray<SerializableTypeModel> serializableTypes,
        ImmutableArray<ProxyOutputModel> proxyOutputs,
        ReferenceAssemblyModel refData)
    {
        var normalizedReferenceData = NormalizeReferenceAssemblyData(refData);
        var normalizedSerializableTypes = MergeSerializableTypes(serializableTypes, normalizedReferenceData.ReferencedSerializableTypes);
        var sourceProxyInterfaces = proxyOutputs.IsDefaultOrEmpty
            ? []
            : proxyOutputs.Select(static output => output.ProxyInterface).ToImmutableArray();
        var normalizedProxyInterfaces = MergeProxyInterfaces(sourceProxyInterfaces, normalizedReferenceData.ReferencedProxyInterfaces);
        var activatableTypes = GetActivatableTypes(normalizedSerializableTypes);
        var generatedProxyTypes = GetGeneratedProxyTypes(normalizedProxyInterfaces);
        var invokableInterfaces = GetInvokableInterfaces(normalizedProxyInterfaces);
        var generatedInvokableActivatorMetadataNames = GetGeneratedInvokableActivatorMetadataNames(proxyOutputs);
        var defaultCopiers = GetDefaultCopiers(normalizedSerializableTypes);

        return new MetadataAggregateModel(
            assemblyName: assemblyName,
            serializableTypes: normalizedSerializableTypes,
            proxyInterfaces: normalizedProxyInterfaces,
            registeredCodecs: normalizedReferenceData.RegisteredCodecs,
            referenceAssemblyData: normalizedReferenceData,
            activatableTypes: activatableTypes,
            generatedProxyTypes: generatedProxyTypes,
            invokableInterfaces: invokableInterfaces,
            generatedInvokableActivatorMetadataNames: generatedInvokableActivatorMetadataNames,
            interfaceImplementations: normalizedReferenceData.InterfaceImplementations,
            defaultCopiers: defaultCopiers);
    }

    private static ReferenceAssemblyModel NormalizeReferenceAssemblyData(ReferenceAssemblyModel referenceData)
    {
        var applicationParts = referenceData.ApplicationParts
            .Distinct()
            .ToImmutableArray();

        var wellKnownTypeIds = referenceData.WellKnownTypeIds
            .Distinct()
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Id)
            .ToImmutableArray();

        var typeAliases = referenceData.TypeAliases
            .Distinct()
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Alias, StringComparer.Ordinal)
            .ToImmutableArray();

        var compoundTypeAliases = referenceData.CompoundTypeAliases
            .Distinct()
            .OrderBy(static entry => GetCompoundTypeAliasOrderKey(entry), StringComparer.Ordinal)
            .ThenBy(static entry => entry.TargetType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        var referencedSerializableTypes = DeduplicateSerializableTypes(referenceData.ReferencedSerializableTypes);

        var referencedProxyInterfaces = DeduplicateProxyInterfaces(referenceData.ReferencedProxyInterfaces);

        var registeredCodecs = referenceData.RegisteredCodecs
            .Distinct()
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Kind)
            .ToImmutableArray();

        var interfaceImplementations = referenceData.InterfaceImplementations
            .Distinct()
            .OrderBy(static entry => entry.ImplementationType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        return new ReferenceAssemblyModel(
            assemblyName: referenceData.AssemblyName ?? string.Empty,
            applicationParts: applicationParts,
            wellKnownTypeIds: wellKnownTypeIds,
            typeAliases: typeAliases,
            compoundTypeAliases: compoundTypeAliases,
            referencedSerializableTypes: referencedSerializableTypes,
            referencedProxyInterfaces: referencedProxyInterfaces,
            registeredCodecs: registeredCodecs,
            interfaceImplementations: interfaceImplementations);
    }

    internal static ImmutableArray<SerializableTypeModel> MergeSerializableTypes(
        ImmutableArray<SerializableTypeModel> source,
        ImmutableArray<SerializableTypeModel> referenced)
    {
        var merged = source.IsDefault ? [] : source;
        if (!referenced.IsDefaultOrEmpty)
        {
            merged = merged.AddRange(referenced);
        }

        return DeduplicateSerializableTypes(merged);
    }

    internal static ImmutableArray<ProxyInterfaceModel> MergeProxyInterfaces(
        ImmutableArray<ProxyInterfaceModel> source,
        ImmutableArray<ProxyInterfaceModel> referenced)
    {
        var merged = source.IsDefault ? [] : source;
        if (!referenced.IsDefaultOrEmpty)
        {
            merged = merged.AddRange(referenced);
        }

        return DeduplicateProxyInterfaces(merged);
    }

    internal static ImmutableArray<SerializableTypeModel> DeduplicateSerializableTypes(
        ImmutableArray<SerializableTypeModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        var selected = new Dictionary<string, SerializableTypeModel>(StringComparer.Ordinal);
        foreach (var entry in entries
            .Where(static entry => entry is not null)
            .OrderBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.TypeSyntax.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            var key = CreateTypeDedupeKey(
                entry.MetadataIdentity,
                entry.TypeSyntax.SyntaxString,
                entry.GeneratedNamespace,
                entry.Name);
            if (!selected.ContainsKey(key))
            {
                selected.Add(key, entry);
            }
        }

        return [.. OrderSerializableTypeModels(selected.Values)];
    }

    internal static ImmutableArray<ProxyInterfaceModel> DeduplicateProxyInterfaces(
        ImmutableArray<ProxyInterfaceModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        var selected = new Dictionary<string, ProxyInterfaceModel>(StringComparer.Ordinal);
        foreach (var entry in entries
            .Where(static entry => entry is not null)
            .OrderBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.InterfaceType.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            var key = CreateTypeDedupeKey(
                entry.MetadataIdentity,
                entry.InterfaceType.SyntaxString,
                entry.GeneratedNamespace,
                entry.Name);
            if (!selected.ContainsKey(key))
            {
                selected.Add(key, entry);
            }
        }

        return [.. OrderProxyInterfaceModels(selected.Values)];
    }

    internal static ImmutableArray<SerializableTypeModel> NormalizeSerializableTypeModels(
        ImmutableArray<SerializableTypeModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. OrderSerializableTypeModels(entries.Where(static entry => entry is not null))];
    }

    internal static ImmutableArray<ProxyInterfaceModel> NormalizeProxyInterfaceModels(
        ImmutableArray<ProxyInterfaceModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. OrderProxyInterfaceModels(entries.Where(static entry => entry is not null))];
    }

    private static string CreateTypeDedupeKey(
        TypeMetadataIdentity metadataIdentity,
        string typeSyntax,
        string generatedNamespace,
        string name)
    {
        if (!metadataIdentity.IsEmpty)
        {
            return string.Join(
                "|",
                "M",
                metadataIdentity.AssemblyIdentity ?? string.Empty,
                metadataIdentity.AssemblyName ?? string.Empty,
                metadataIdentity.MetadataName ?? string.Empty);
        }

        return string.Join(
            "|",
            "S",
            typeSyntax ?? string.Empty,
            generatedNamespace ?? string.Empty,
            name ?? string.Empty);
    }

    private static IOrderedEnumerable<SerializableTypeModel> OrderSerializableTypeModels(IEnumerable<SerializableTypeModel> entries)
        => entries
            .OrderBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.TypeSyntax.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal);

    private static IOrderedEnumerable<ProxyInterfaceModel> OrderProxyInterfaceModels(IEnumerable<ProxyInterfaceModel> entries)
        => entries
            .OrderBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.InterfaceType.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal);

    private static ImmutableArray<TypeRef> GetActivatableTypes(ImmutableArray<SerializableTypeModel> serializableTypes)
        => [.. serializableTypes
            .Where(static type => ShouldGenerateActivator(type))
            .Select(static type => type.TypeSyntax)
            .Distinct()
            .OrderBy(static type => type.SyntaxString, StringComparer.Ordinal)];

    private static ImmutableArray<TypeRef> GetGeneratedProxyTypes(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
        => [.. proxyInterfaces
            .Select(static proxy => CreateGeneratedTypeRef(
                proxy.GeneratedNamespace,
                ProxyGenerator.GetSimpleClassName(proxy.Name),
                proxy.TypeParameters.Length))
            .Distinct()
            .OrderBy(static type => type.SyntaxString, StringComparer.Ordinal)];

    private static ImmutableArray<TypeRef> GetInvokableInterfaces(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
        => [.. proxyInterfaces
            .Select(static proxy => proxy.InterfaceType)
            .Distinct()
            .OrderBy(static type => type.SyntaxString, StringComparer.Ordinal)];

    private static ImmutableArray<string> GetGeneratedInvokableActivatorMetadataNames(ImmutableArray<ProxyOutputModel> proxyOutputs)
    {
        if (proxyOutputs.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. proxyOutputs
            .SelectMany(static output => output.OwnedInvokableActivatorMetadataNames)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static metadataName => metadataName, StringComparer.Ordinal)];
    }

    private static ImmutableArray<DefaultCopierModel> GetDefaultCopiers(ImmutableArray<SerializableTypeModel> serializableTypes)
        => [.. serializableTypes
            .Where(static type => type.IsShallowCopyable && !type.IsGenericType)
            .Select(static type => new DefaultCopierModel(
                type.TypeSyntax,
                new TypeRef($"global::Orleans.Serialization.Cloning.ShallowCopier<{type.TypeSyntax.SyntaxString}>")))
            .Distinct()
            .OrderBy(static entry => entry.OriginalType.SyntaxString, StringComparer.Ordinal)];

    private static bool ShouldGenerateActivator(SerializableTypeModel type)
    {
        return !type.IsAbstractType
            && !type.IsEnumType
            && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator || type.HasActivatorConstructor);
    }

    private static TypeRef CreateGeneratedTypeRef(string generatedNamespace, string simpleName, int genericArity)
    {
        var qualifiedName = string.IsNullOrWhiteSpace(generatedNamespace)
            ? simpleName
            : $"{generatedNamespace}.{simpleName}";

        return genericArity > 0
            ? new TypeRef($"{qualifiedName}<{new string(',', genericArity - 1)}>")
            : new TypeRef(qualifiedName);
    }

    /// <summary>
    /// Extracts a <see cref="RegisteredCodecModel"/> from a symbol with one of the Register* attributes.
    /// </summary>
    public static RegisteredCodecModel ExtractRegisteredCodec(INamedTypeSymbol symbol, RegisteredCodecKind kind)
    {
        return new RegisteredCodecModel(
            new TypeRef(symbol.ToOpenTypeSyntax().ToString()),
            kind);
    }

    private static ImmutableArray<TypeParameterModel> ExtractTypeParameters(
        List<(string Name, ITypeParameterSymbol Parameter)> typeParameters)
    {
        if (typeParameters is null || typeParameters.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeParameterModel>(typeParameters.Count);
        for (var i = 0; i < typeParameters.Count; i++)
        {
            var (name, param) = typeParameters[i];
            builder.Add(new TypeParameterModel(name, param.Name, param.Ordinal));
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<MemberModel> ExtractMembers(List<IMemberDescription> members)
    {
        if (members is null || members.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<MemberModel>(members.Count);
        foreach (var member in members)
        {
            builder.Add(ExtractMember(member));
        }
        return builder.MoveToImmutable();
    }

    private static MemberModel ExtractMember(IMemberDescription member)
    {
        var kind = member is IFieldDescription ? MemberKind.Field : MemberKind.Property;
        var symbol = member.Symbol;
        var containingType = member.ContainingType;

        // Determine getter/setter accessibility strategies
        var getterStrategy = DetermineGetterStrategy(member);
        var setterStrategy = DetermineSetterStrategy(member);

        // Determine if member has immutable attribute
        var hasImmutableAttribute = false;
        if (symbol is IPropertySymbol prop)
        {
            hasImmutableAttribute = prop.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "ImmutableAttribute"
                && a.AttributeClass.ContainingNamespace?.Name == "Orleans");
        }
        if (!hasImmutableAttribute)
        {
            hasImmutableAttribute = symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "ImmutableAttribute"
                && a.AttributeClass.ContainingNamespace?.Name == "Orleans");
        }

        // Determine if obsolete
        var isObsolete = symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "ObsoleteAttribute"
            && a.AttributeClass.ContainingNamespace?.Name == "System");

        // Backing property name
        string? backingPropertyName = null;
        if (member is IFieldDescription fieldDesc)
        {
            var backingProp = PropertyUtility.GetMatchingProperty(fieldDesc.Field);
            if (backingProp is not null)
            {
                backingPropertyName = backingProp.Name;
            }
        }

        return new MemberModel(
            fieldId: member.FieldId,
            name: symbol.Name,
            type: new TypeRef(member.TypeSyntax.ToString()),
            containingType: containingType is not null ? new TypeRef(containingType.ToTypeSyntax().ToString()) : TypeRef.Empty,
            assemblyName: member.AssemblyName ?? string.Empty,
            typeNameIdentifier: member.TypeNameIdentifier ?? string.Empty,
            isPrimaryConstructorParameter: member.IsPrimaryConstructorParameter,
            isSerializable: member.IsSerializable,
            isCopyable: member.IsCopyable,
            kind: kind,
            getterStrategy: getterStrategy,
            setterStrategy: setterStrategy,
            isObsolete: isObsolete,
            hasImmutableAttribute: hasImmutableAttribute,
            isShallowCopyable: false, // Will be resolved later with LibraryTypes
            isValueType: member.Type?.IsValueType ?? false,
            containingTypeIsValueType: containingType?.IsValueType ?? false,
            backingPropertyName: backingPropertyName);
    }

    private static AccessStrategy DetermineGetterStrategy(IMemberDescription member)
    {
        if (member is IFieldDescription fieldDesc)
        {
            // Direct access if field is accessible
            return AccessStrategy.Direct;
        }

        if (member.Symbol is IPropertySymbol prop && prop.GetMethod is not null)
        {
            return AccessStrategy.Direct;
        }

        return AccessStrategy.GeneratedAccessor;
    }

    private static AccessStrategy DetermineSetterStrategy(IMemberDescription member)
    {
        if (member is IFieldDescription fieldDesc)
        {
            if (!fieldDesc.Field.IsReadOnly)
            {
                return AccessStrategy.Direct;
            }
            return AccessStrategy.GeneratedAccessor;
        }

        if (member.Symbol is IPropertySymbol prop)
        {
            if (prop.SetMethod is not null && !prop.SetMethod.IsInitOnly)
            {
                return AccessStrategy.Direct;
            }
            if (member.IsPrimaryConstructorParameter)
            {
                return AccessStrategy.UnsafeAccessor;
            }
            return AccessStrategy.GeneratedAccessor;
        }

        return AccessStrategy.GeneratedAccessor;
    }

    private static ImmutableArray<TypeRef> ExtractTypeRefs(List<INamedTypeSymbol>? symbols)
    {
        if (symbols is null || symbols.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeRef>(symbols.Count);
        foreach (var s in symbols)
        {
            builder.Add(new TypeRef(s.ToTypeSyntax().ToString()));
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<TypeRef> ExtractTypeRefSyntaxList(
        List<Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax>? syntaxList)
    {
        if (syntaxList is null || syntaxList.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeRef>(syntaxList.Count);
        foreach (var ts in syntaxList)
        {
            builder.Add(new TypeRef(ts.ToString()));
        }
        return builder.MoveToImmutable();
    }

    private static ObjectCreationStrategy DetermineCreationStrategy(ISerializableTypeDescription description)
    {
        if (description.IsValueType)
        {
            return ObjectCreationStrategy.Default;
        }

        // Check if we can determine from the existing expression
        var expr = description.GetObjectCreationExpression();
        if (expr is Microsoft.CodeAnalysis.CSharp.Syntax.DefaultExpressionSyntax)
        {
            return ObjectCreationStrategy.Default;
        }

        if (expr is Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax)
        {
            return ObjectCreationStrategy.NewExpression;
        }

        return ObjectCreationStrategy.GetUninitializedObject;
    }

    internal static SerializableTypeModel? TryExtractSerializableTypeModel(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        LibraryTypes libraryTypes,
        CodeGeneratorOptions options,
        bool throwOnFailure = false)
    {
        if (typeSymbol is null)
        {
            return null;
        }

        if (FSharpUtilities.IsUnionCase(libraryTypes, typeSymbol, out var sumType))
        {
            if (!sumType.HasAttribute(libraryTypes.GenerateSerializerAttribute))
            {
                return null;
            }

            if (throwOnFailure && HasReferenceAssemblyAttribute(sumType.ContainingAssembly))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    ReferenceAssemblyWithGenerateSerializerDiagnostic.CreateDiagnostic(sumType, Location.None));
            }

            if (!compilation.IsSymbolAccessibleWithin(sumType, compilation.Assembly))
            {
                if (throwOnFailure)
                {
                    throw new OrleansGeneratorDiagnosticAnalysisException(
                        InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType, Location.None));
                }

                return null;
            }

            var fsharpUnionCaseDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(compilation, typeSymbol, libraryTypes);
            return ExtractSerializableTypeModel(fsharpUnionCaseDescription, GetSourceLocation(typeSymbol));
        }

        if (!typeSymbol.HasAttribute(libraryTypes.GenerateSerializerAttribute))
        {
            return null;
        }

        if (throwOnFailure && HasReferenceAssemblyAttribute(typeSymbol.ContainingAssembly))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(
                ReferenceAssemblyWithGenerateSerializerDiagnostic.CreateDiagnostic(typeSymbol, Location.None));
        }

        if (!compilation.IsSymbolAccessibleWithin(typeSymbol, compilation.Assembly))
        {
            if (throwOnFailure)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(typeSymbol, Location.None));
            }

            return null;
        }

        if (FSharpUtilities.IsRecord(libraryTypes, typeSymbol))
        {
            var fsharpDescription = new FSharpUtilities.FSharpRecordTypeDescription(compilation, typeSymbol, libraryTypes);
            return ExtractSerializableTypeModel(fsharpDescription, GetSourceLocation(typeSymbol));
        }

        var includePrimaryCtorParams = GetIncludePrimaryConstructorParameters(typeSymbol, libraryTypes);
        var ctorParams = ResolveConstructorParameters(typeSymbol, includePrimaryCtorParams, libraryTypes);
        var implicitFieldIdStrategy = (options.GenerateFieldIds, GetFieldIdsOptionFromType(typeSymbol, libraryTypes)) switch
        {
            (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
            (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
            _ => GenerateFieldIds.None,
        };
        var helper = new FieldIdAssignmentHelper(typeSymbol, ctorParams, implicitFieldIdStrategy, libraryTypes);
        if (!helper.IsValidForSerialization)
        {
            if (throwOnFailure)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(typeSymbol, helper.FailureReason!, Location.None));
            }

            return null;
        }

        var members = CollectDataMembers(helper);
        var description = new SerializableTypeDescription(compilation, typeSymbol, includePrimaryCtorParams, members, libraryTypes);
        return ExtractSerializableTypeModel(description, GetSourceLocation(typeSymbol));
    }

    private static bool HasReferenceAssemblyAttribute(IAssemblySymbol assembly)
    {
        return assembly?.GetAttributes().Any(attributeData => attributeData.AttributeClass is
        {
            Name: "ReferenceAssemblyAttribute",
            ContainingNamespace:
            {
                Name: "CompilerServices",
                ContainingNamespace:
                {
                    Name: "Runtime",
                    ContainingNamespace:
                    {
                        Name: "System",
                        ContainingNamespace.IsGlobalNamespace: true,
                    },
                },
            },
        }) == true;
    }

    private static bool GetIncludePrimaryConstructorParameters(INamedTypeSymbol typeSymbol, LibraryTypes libraryTypes)
    {
        var attribute = typeSymbol.GetAttribute(libraryTypes.GenerateSerializerAttribute);
        if (attribute is not null)
        {
            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "IncludePrimaryConstructorParameters"
                    && namedArgument.Value.Kind == TypedConstantKind.Primitive
                    && namedArgument.Value.Value is bool b)
                {
                    return b;
                }
            }
        }

        // Default to true for records
        if (typeSymbol.IsRecord)
        {
            return true;
        }

        // Detect primary constructor via compiler-generated properties
        var properties = typeSymbol.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
        return typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
            .Any(ctor => ctor.Parameters.All(prm =>
                properties.Any(prop => prop.Name.Equals(prm.Name, StringComparison.Ordinal) && prop.IsCompilerGenerated())));
    }

    private static ImmutableArray<IParameterSymbol> ResolveConstructorParameters(
        INamedTypeSymbol typeSymbol,
        bool includePrimaryCtorParams,
        LibraryTypes libraryTypes)
    {
        if (!includePrimaryCtorParams)
        {
            return [];
        }

        if (typeSymbol.IsRecord)
        {
            // Primary constructor is declared before the copy constructor for records
            var potentialPrimaryConstructor = typeSymbol.Constructors[0];
            if (!potentialPrimaryConstructor.IsImplicitlyDeclared && !potentialPrimaryConstructor.IsCompilerGenerated())
            {
                return potentialPrimaryConstructor.Parameters;
            }
        }
        else
        {
            var annotatedConstructors = typeSymbol.Constructors
                .Where(ctor => ctor.HasAnyAttribute(libraryTypes.ConstructorAttributeTypes))
                .ToList();
            if (annotatedConstructors.Count == 1)
            {
                return annotatedConstructors[0].Parameters;
            }

            // Fallback: detect primary constructor via compiler-generated properties
            var properties = typeSymbol.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
            var primaryConstructor = typeSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
                .FirstOrDefault(ctor => ctor.Parameters.All(prm =>
                    properties.Any(prop => prop.Name.Equals(prm.Name, StringComparison.Ordinal) && prop.IsCompilerGenerated())));

            if (primaryConstructor is not null)
            {
                return primaryConstructor.Parameters;
            }
        }

        return [];
    }

    /// <summary>
    /// Extracts a <see cref="ProxyInterfaceModel"/> from a <see cref="GeneratorAttributeSyntaxContext"/>
    /// provided by the <c>ForAttributeWithMetadataName</c> incremental pipeline step for
    /// <c>[GenerateMethodSerializers]</c>-annotated interfaces.
    /// </summary>
    public static ProxyInterfaceModel? ExtractProxyInterfaceFromAttributeContext(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol typeSymbol || typeSymbol.TypeKind != TypeKind.Interface)
        {
            return null;
        }

        return ExtractProxyInterfaceModel(typeSymbol, context.SemanticModel.Compilation, context.Attributes, cancellationToken);
    }

    public static ProxyInterfaceModel? ExtractProxyInterfaceModel(
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

    public static ProxyInterfaceModel? ExtractInheritedProxyInterfaceFromSyntaxContext(
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
            sourceLocation: GetSourceLocation(typeSymbol),
            metadataIdentity: TypeMetadataIdentity.Create(typeSymbol));
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

    internal static SourceLocationModel GetSourceLocation(ISymbol? symbol)
    {
        var sourceLocation = symbol?.Locations.FirstOrDefault(static location => location.IsInSource);
        return sourceLocation is null
            ? new SourceLocationModel(sourceOrderGroup: 1, filePath: string.Empty, position: int.MaxValue)
            : new SourceLocationModel(
                sourceOrderGroup: 0,
                filePath: sourceLocation.SourceTree?.FilePath ?? string.Empty,
                position: sourceLocation.SourceSpan.Start);
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

    private static GenerateFieldIds GetFieldIdsOptionFromType(INamedTypeSymbol typeSymbol, LibraryTypes libraryTypes)
    {
        var attribute = typeSymbol.GetAttribute(libraryTypes.GenerateSerializerAttribute);
        if (attribute is null)
        {
            return GenerateFieldIds.None;
        }

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "GenerateFieldIds")
            {
                var value = namedArgument.Value.Value;
                return value is null ? GenerateFieldIds.None : (GenerateFieldIds)(int)value;
            }
        }

        return GenerateFieldIds.None;
    }

    private static IEnumerable<IMemberDescription> CollectDataMembers(FieldIdAssignmentHelper fieldIdAssignmentHelper)
    {
        var members = new Dictionary<(uint, bool), IMemberDescription>();

        foreach (var member in fieldIdAssignmentHelper.Members)
        {
            if (!fieldIdAssignmentHelper.TryGetSymbolKey(member, out var key))
            {
                continue;
            }

            var (id, isConstructorParameter) = key;

            if (member is IPropertySymbol property && !members.ContainsKey((id, isConstructorParameter)))
            {
                members[(id, isConstructorParameter)] = new PropertyDescription(id, isConstructorParameter, property);
            }

            if (member is IFieldSymbol field)
            {
                if (!members.TryGetValue((id, isConstructorParameter), out var existing) || existing is IPropertyDescription)
                {
                    members[(id, isConstructorParameter)] = new FieldDescription(id, isConstructorParameter, field);
                }
            }
        }

        return members.Values;
    }
}
