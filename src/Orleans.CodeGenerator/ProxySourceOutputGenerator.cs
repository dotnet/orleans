using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class ProxySourceOutputGenerator
{
    internal static SourceOutputResult CreateProxySourceOutput(
        Compilation compilation,
        TypeSymbolResolver resolver,
        ProxyOutputModel proxyOutputModel,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);
            var codeGeneratorOptions = SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options);
            var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
            var proxyContext = new ProxyGenerationContext(compilation, codeGeneratorOptions);
            var model = proxyOutputModel.ProxyInterface;
            PopulateProxyInterfaces(proxyContext, resolver, [model], cancellationToken);

            var assemblyName = compilation.AssemblyName ?? "assembly";
            var interfaceDescription = GetProxyInterfaceDescription(proxyContext, resolver, model, cancellationToken);
            var proxyGenerator = new ProxyGenerator(generatorServices, new CopierGenerator(generatorServices));
            var (proxyClass, _) = proxyGenerator.Generate(interfaceDescription);
            var targetHintName = GeneratedSourceOutput.CreateProxyHintName(assemblyName, interfaceDescription);
            var ownedInvokableMetadataNames = new HashSet<string>(
                proxyOutputModel.OwnedInvokableMetadataNames,
                StringComparer.Ordinal);
            var emitDeclaredMethodsFallback = proxyOutputModel.UseDeclaredInvokableFallback;
            var generatedInvokables = GetGeneratedInvokables(proxyContext, interfaceDescription).ToImmutableArray();
            var generatedInvokableClassNames = new HashSet<string>(
                generatedInvokables.Select(static invokable => invokable.ClassDeclarationSyntax.Identifier.ValueText),
                StringComparer.Ordinal);
            var additionalInvokableClasses = proxyContext.GetEmittedMembers()
                .Where(entry => entry.Member is ClassDeclarationSyntax classDeclaration
                    && !string.Equals(classDeclaration.Identifier.ValueText, proxyClass.Identifier.ValueText, StringComparison.Ordinal)
                    && !generatedInvokableClassNames.Contains(classDeclaration.Identifier.ValueText))
                .Select(entry => (entry.Namespace, ClassDeclaration: (ClassDeclarationSyntax)entry.Member))
                .OrderBy(static entry => entry.Namespace, StringComparer.Ordinal)
                .ThenBy(static entry => entry.ClassDeclaration.Identifier.ValueText, StringComparer.Ordinal)
                .ToImmutableArray();

            var serializerGenerator = new SerializerGenerator(generatorServices);
            var copierGenerator = new CopierGenerator(generatorServices);
            var activatorGenerator = new ActivatorGenerator(generatorServices);
            var emittedInvokables = generatedInvokables
                .Where(invokable => ShouldEmitInvokable(
                    invokable,
                    interfaceDescription.InterfaceType,
                    ownedInvokableMetadataNames,
                    emitDeclaredMethodsFallback))
                .ToImmutableArray();

            var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
            foreach (var invokable in emittedInvokables)
            {
                GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, invokable.ClassDeclarationSyntax);
            }

            GeneratedSourceOutput.AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, proxyClass);

            foreach (var invokable in emittedInvokables)
            {
                GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, serializerGenerator.Generate(invokable));

                var copier = invokable.IsShallowCopyable && proxyContext.MetadataModel.DefaultCopiers.ContainsKey(invokable)
                    ? null
                    : copierGenerator.GenerateCopier(invokable, proxyContext.MetadataModel.DefaultCopiers);
                if (copier is not null)
                {
                    GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, copier);
                }

                if (ActivatorGenerator.ShouldGenerateActivator(invokable))
                {
                    GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, activatorGenerator.GenerateActivator(invokable));
                }
            }

            foreach (var (generatedNamespace, classDeclaration) in additionalInvokableClasses)
            {
                GeneratedSourceOutput.AddMember(namespacedMembers, generatedNamespace, classDeclaration);
            }

            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(targetHintName, GeneratedSourceOutput.CreateSourceString(GeneratedSourceOutput.CreateCompilationUnit(namespacedMembers))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SourceOutputResult.FromDiagnostic(analysisException.Diagnostic);
        }
    }

    internal static SourceOutputResult CreateProxySourceOutput(
        ProxyGenerationContext proxyContext,
        IGeneratorServices generatorServices,
        TypeSymbolResolver resolver,
        string assemblyName,
        ProxyOutputModel proxyOutputModel,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = proxyOutputModel.ProxyInterface;
            var interfaceDescription = GetProxyInterfaceDescription(proxyContext, resolver, model, cancellationToken);
            var proxyGenerator = new ProxyGenerator(generatorServices, new CopierGenerator(generatorServices));
            var (proxyClass, _) = proxyGenerator.Generate(interfaceDescription);
            var targetHintName = GeneratedSourceOutput.CreateProxyHintName(assemblyName, interfaceDescription);
            var ownedInvokableMetadataNames = new HashSet<string>(
                proxyOutputModel.OwnedInvokableMetadataNames,
                StringComparer.Ordinal);
            var emitDeclaredMethodsFallback = proxyOutputModel.UseDeclaredInvokableFallback;
            var serializerGenerator = new SerializerGenerator(generatorServices);
            var copierGenerator = new CopierGenerator(generatorServices);
            var activatorGenerator = new ActivatorGenerator(generatorServices);
            var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();
            var generatedInvokables = GetGeneratedInvokables(proxyContext, interfaceDescription).ToImmutableArray();
            var emittedInvokables = generatedInvokables
                .Where(invokable => ShouldEmitInvokable(
                    invokable,
                    interfaceDescription.InterfaceType,
                    ownedInvokableMetadataNames,
                    emitDeclaredMethodsFallback))
                .ToImmutableArray();

            var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
            foreach (var invokable in emittedInvokables)
            {
                GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, invokable.ClassDeclarationSyntax);
            }

            GeneratedSourceOutput.AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, proxyClass);

            foreach (var invokable in emittedInvokables)
            {
                GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, serializerGenerator.Generate(invokable));

                var copier = invokable.IsShallowCopyable && defaultCopiers.ContainsKey(invokable)
                    ? null
                    : copierGenerator.GenerateCopier(invokable, defaultCopiers);
                if (copier is not null)
                {
                    GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, copier);
                }

                if (ActivatorGenerator.ShouldGenerateActivator(invokable))
                {
                    GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, activatorGenerator.GenerateActivator(invokable));
                }
            }

            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(targetHintName, GeneratedSourceOutput.CreateSourceString(GeneratedSourceOutput.CreateCompilationUnit(namespacedMembers))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SourceOutputResult.FromDiagnostic(analysisException.Diagnostic);
        }
    }

    internal static ProxyOutputPreparationResult CreateProxyOutputPreparation(
        Compilation compilation,
        ImmutableArray<ProxyInterfaceModel> models,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (models.IsDefaultOrEmpty)
            {
                return ProxyOutputPreparationResult.FromModelsAndSources(
                    [],
                    []);
            }

            var codeGeneratorOptions = SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options);
            var libraryTypes = LibraryTypes.FromCompilation(compilation, codeGeneratorOptions);
            var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions, libraryTypes);
            var proxyContext = new ProxyGenerationContext(compilation, codeGeneratorOptions, libraryTypes);
            var resolver = new TypeSymbolResolver(compilation);
            PopulateProxyInterfaces(proxyContext, resolver, models, cancellationToken);

            var proxyOutputModels = CreateProxyOutputModels(
                compilation,
                proxyContext,
                resolver,
                models,
                cancellationToken);

            return ProxyOutputPreparationResult.FromModelsAndSources(
                proxyOutputModels,
                CreateProxySourceOutputs(
                    compilation,
                    proxyContext,
                    generatorServices,
                    resolver,
                    proxyOutputModels,
                    options,
                    cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return ProxyOutputPreparationResult.FromDiagnostic(analysisException.Diagnostic);
        }
    }

    internal static ImmutableArray<SourceOutputResult> CreateProxySourceOutputs(
        Compilation compilation,
        ProxyGenerationContext proxyContext,
        IGeneratorServices generatorServices,
        TypeSymbolResolver resolver,
        ImmutableArray<ProxyOutputModel> proxyOutputModels,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (proxyOutputModels.IsDefaultOrEmpty)
        {
            return [];
        }

        var sourceOutputs = ImmutableArray.CreateBuilder<SourceOutputResult>(proxyOutputModels.Length);
        if (options.GenerateCompatibilityInvokers)
        {
            foreach (var proxyOutputModel in proxyOutputModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceOutputs.Add(CreateProxySourceOutput(compilation, resolver, proxyOutputModel, options, cancellationToken));
            }
        }
        else
        {
            SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);
            var assemblyName = compilation.AssemblyName ?? "assembly";
            foreach (var proxyOutputModel in proxyOutputModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceOutputs.Add(CreateProxySourceOutput(
                    proxyContext,
                    generatorServices,
                    resolver,
                    assemblyName,
                    proxyOutputModel,
                    cancellationToken));
            }
        }

        return GeneratedSourceOutput.DeduplicateSourceOutputs(sourceOutputs);
    }

    internal static ImmutableArray<ProxyOutputModel> CreateProxyOutputModels(
        Compilation compilation,
        ProxyGenerationContext proxyContext,
        TypeSymbolResolver resolver,
        ImmutableArray<ProxyInterfaceModel> models,
        CancellationToken cancellationToken)
    {
        if (models.IsDefaultOrEmpty)
        {
            return [];
        }

        var assemblyName = compilation.AssemblyName ?? "assembly";
        var proxyEntries = proxyContext.MetadataModel.InvokableInterfaces.Values
            .Where(desc => SymbolEqualityComparer.Default.Equals(desc.InterfaceType.ContainingAssembly, compilation.Assembly))
            .OrderBy(static desc => desc.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
            .Select(desc => (HintName: GeneratedSourceOutput.CreateProxyHintName(assemblyName, desc), Description: desc))
            .ToImmutableArray();

        var invokableOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in proxyEntries.OrderBy(static entry => entry.HintName, StringComparer.Ordinal))
        {
                foreach (var invokable in GetGeneratedInvokables(proxyContext, entry.Description))
            {
                if (!invokableOwners.TryGetValue(invokable.MetadataName, out _))
                {
                    invokableOwners.Add(invokable.MetadataName, entry.HintName);
                }
            }
        }

        return [.. ModelExtractor.DeduplicateProxyInterfaces(models)
            .OrderBy(static model => model.SourceLocation.SourceOrderGroup)
            .ThenBy(static model => model.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static model => model.SourceLocation.Position)
            .ThenBy(static model => model.InterfaceType.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static model => model.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static model => model.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static model => model.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static model => model.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static model => model.Name, StringComparer.Ordinal)
            .Select(model =>
            {
                var interfaceDescription = GetProxyInterfaceDescription(proxyContext, resolver, model, cancellationToken);
                var targetHintName = GeneratedSourceOutput.CreateProxyHintName(assemblyName, interfaceDescription);
                var generatedInvokables = GetGeneratedInvokables(proxyContext, interfaceDescription)
                    .ToImmutableArray();
                var ownedInvokableMetadataNames = generatedInvokables
                    .Select(invokable => invokable.MetadataName)
                    .Where(metadataName => invokableOwners.TryGetValue(metadataName, out var ownerHintName)
                        && string.Equals(ownerHintName, targetHintName, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToImmutableArray();
                var useDeclaredInvokableFallback =
                    generatedInvokables.Length == 0
                        ? model.Methods.Any(method => method.ContainingInterfaceType.Equals(model.InterfaceType))
                        : ownedInvokableMetadataNames.Length == 0
                            && !generatedInvokables.Any(invokable => invokableOwners.ContainsKey(invokable.MetadataName));
                var ownedInvokableMetadataNameSet = new HashSet<string>(ownedInvokableMetadataNames, StringComparer.Ordinal);
                var ownedInvokableActivatorMetadataNames = generatedInvokables
                    .Where(invokable => ShouldEmitInvokable(
                        invokable,
                        interfaceDescription.InterfaceType,
                        ownedInvokableMetadataNameSet,
                        useDeclaredInvokableFallback))
                    .Where(static invokable => ActivatorGenerator.ShouldGenerateActivator(invokable))
                    .Select(static invokable => invokable.MetadataName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToImmutableArray();

                return new ProxyOutputModel(
                    model,
                    ownedInvokableMetadataNames,
                    ownedInvokableActivatorMetadataNames,
                    useDeclaredInvokableFallback);
            })];
    }

    internal static bool ShouldEmitInvokable(
        GeneratedInvokableDescription invokable,
        INamedTypeSymbol interfaceType,
        HashSet<string> ownedInvokableMetadataNames,
        bool useDeclaredInvokableFallback)
        => ownedInvokableMetadataNames.Contains(invokable.MetadataName)
            || useDeclaredInvokableFallback
                && SymbolEqualityComparer.Default.Equals(invokable.MethodDescription.ContainingInterface, interfaceType);

    internal static void PopulateProxyInterfaces(
        ProxyGenerationContext proxyContext,
        TypeSymbolResolver resolver,
        ImmutableArray<ProxyInterfaceModel> models,
        CancellationToken cancellationToken)
    {
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var resolvedInterfaces = new List<(ProxyInterfaceModel Model, INamedTypeSymbol Symbol, int SourceOrderGroup, string FilePath, int Position)>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modelKey = $"{model.MetadataIdentity.AssemblyIdentity}|{model.MetadataIdentity.AssemblyName}|{model.MetadataIdentity.MetadataName}|{model.InterfaceType.SyntaxString}|{model.GeneratedNamespace}|{model.Name}";
            if (!processed.Add(modelKey))
            {
                continue;
            }

            if (!resolver.TryResolveProxyInterface(model, cancellationToken, out var interfaceType))
            {
                throw new InvalidOperationException($"Unable to resolve proxy interface '{model.InterfaceType.SyntaxString}'.");
            }

            var sourceLocation = interfaceType.Locations.FirstOrDefault(static location => location.IsInSource);
            resolvedInterfaces.Add((
                model,
                interfaceType,
                SourceOrderGroup: sourceLocation is null ? 1 : 0,
                FilePath: sourceLocation?.SourceTree?.FilePath ?? string.Empty,
                Position: sourceLocation?.SourceSpan.Start ?? int.MaxValue));
        }

        foreach (var entry in resolvedInterfaces
            .OrderBy(static entry => entry.SourceOrderGroup)
            .ThenBy(static entry => entry.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Position)
            .ThenBy(static entry => entry.Model.InterfaceType.SyntaxString, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            proxyContext.VisitInterface(entry.Symbol.OriginalDefinition);
        }
    }

    internal static ProxyInterfaceDescription GetProxyInterfaceDescription(
        ProxyGenerationContext proxyContext,
        TypeSymbolResolver resolver,
        ProxyInterfaceModel model,
        CancellationToken cancellationToken)
    {
        if (!resolver.TryResolveProxyInterface(model, cancellationToken, out var interfaceType)
            || !proxyContext.TryGetInvokableInterfaceDescription(interfaceType.OriginalDefinition, out var description))
        {
            throw new InvalidOperationException($"Unable to resolve proxy interface '{model.InterfaceType.SyntaxString}'.");
        }

        return description;
    }

    internal static IEnumerable<GeneratedInvokableDescription> GetGeneratedInvokables(
        ProxyGenerationContext proxyContext,
        ProxyInterfaceDescription interfaceDescription)
    {
        return interfaceDescription.Methods
            .Select(static method => method.InvokableKey)
            .Distinct()
            .Select(key => proxyContext.MetadataModel.GeneratedInvokables.TryGetValue(key, out var generatedInvokable) ? generatedInvokable : null)
            .OfType<GeneratedInvokableDescription>()
            .Where(generatedInvokable => proxyContext.Compilation.GetTypeByMetadataName(generatedInvokable.MetadataName) is null)
            .OrderBy(static generatedInvokable => generatedInvokable.MetadataName, StringComparer.Ordinal);
    }
}



