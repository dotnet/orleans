using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class ProxySourceOutputGenerator
{
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
            var proxyPlan = proxyContext.GetProxyPlan(interfaceDescription);
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

            var namespacedMembers = new Dictionary<string, List<Action<SourceWriter>>>(StringComparer.Ordinal);
            foreach (var invokable in emittedInvokables)
            {
                var plan = invokable.EmissionPlan;
                GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, writer => InvokableGenerator.Write(writer, plan));
            }

            GeneratedSourceOutput.AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, writer => ProxyGenerator.Write(writer, proxyPlan));

            foreach (var invokable in emittedInvokables)
            {
                var serializerPlan = serializerGenerator.CreatePlan(invokable);
                GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, writer => SerializerGenerator.Write(writer, serializerPlan));

                var copierPlan = invokable.IsShallowCopyable && defaultCopiers.ContainsKey(invokable)
                    ? null
                    : copierGenerator.CreatePlan(invokable, defaultCopiers);
                if (copierPlan is not null)
                {
                    GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, writer => CopierGenerator.Write(writer, copierPlan));
                }

                if (ActivatorGenerator.ShouldGenerateActivator(invokable))
                {
                    var activatorPlan = activatorGenerator.CreatePlan(invokable);
                    GeneratedSourceOutput.AddMember(namespacedMembers, invokable.GeneratedNamespace, writer => ActivatorGenerator.Write(writer, activatorPlan));
                }
            }

            var generatedMemberMetadataNames = new HashSet<string>(
                generatedInvokables.Select(static invokable => invokable.MetadataName),
                StringComparer.Ordinal);
            generatedMemberMetadataNames.Add(proxyPlan.GetMetadataName(interfaceDescription.GeneratedNamespace));
            foreach (var (generatedNamespace, plan) in proxyContext.GetCompatibilityDeclarations(interfaceDescription.InterfaceType)
                .Where(entry => !generatedMemberMetadataNames.Contains(entry.Plan.GetMetadataName(entry.Namespace)))
                .OrderBy(static entry => entry.Namespace, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Plan.ClassName, StringComparer.Ordinal))
            {
                GeneratedSourceOutput.AddMember(namespacedMembers, generatedNamespace, writer => InvokableGenerator.Write(writer, plan));
            }

            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(targetHintName, GeneratedSourceOutput.CreateSourceString(namespacedMembers)));
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
            var diagnostics = GetCancellationTokenParameterDiagnostics(proxyContext);

            return ProxyOutputPreparationResult.FromModelsAndSources(
                proxyOutputModels,
                CreateProxySourceOutputs(
                    compilation,
                    proxyContext,
                    generatorServices,
                    resolver,
                    proxyOutputModels,
                    options,
                    cancellationToken),
                diagnostics);
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

        return GeneratedSourceOutput.DeduplicateSourceOutputs(sourceOutputs);
    }

    private static ImmutableArray<Diagnostic> GetCancellationTokenParameterDiagnostics(ProxyGenerationContext proxyContext)
    {
        var inspectedMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var parameters = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        foreach (var invokable in proxyContext.MetadataModel.GeneratedInvokables.Values)
        {
            var method = invokable.MethodDescription.Method.OriginalDefinition;
            if (!inspectedMethods.Add(method) || method.Parameters.Length < 2)
            {
                continue;
            }

            for (var index = 0; index < method.Parameters.Length - 1; index++)
            {
                var parameter = method.Parameters[index];
                if (parameter.Locations.Any(static location => location.IsInSource)
                    && SymbolEqualityComparer.Default.Equals(proxyContext.LibraryTypes.CancellationToken, parameter.Type))
                {
                    parameters.Add(parameter);
                }
            }
        }

        return [.. parameters
            .OrderBy(static parameter => parameter.Locations.First().SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static parameter => parameter.Locations.First().SourceSpan.Start)
            .Select(CancellationTokenNotLastDiagnostic.CreateDiagnostic)];
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
                var emittedInvokables = generatedInvokables
                    .Where(invokable => ShouldEmitInvokable(
                        invokable,
                        interfaceDescription.InterfaceType,
                        ownedInvokableMetadataNameSet,
                        useDeclaredInvokableFallback))
                    .ToImmutableArray();
                var emittedInvokableMetadataNames = emittedInvokables
                    .Select(static invokable => invokable.MetadataName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToImmutableArray();
                var ownedInvokableActivatorMetadataNames = emittedInvokables
                    .Where(static invokable => ActivatorGenerator.ShouldGenerateActivator(invokable))
                    .Select(static invokable => invokable.MetadataName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToImmutableArray();
                var compatibilityInvokableAliases = proxyContext
                    .GetCompatibilityInvokableAliases(interfaceDescription.InterfaceType)
                    .Distinct()
                    .OrderBy(static alias => ReferenceAssemblyModelExtractor.GetCompoundTypeAliasOrderKey(alias), StringComparer.Ordinal)
                    .ThenBy(static alias => alias.TargetType.SyntaxString, StringComparer.Ordinal)
                    .ToImmutableArray();

                return new ProxyOutputModel(
                    model,
                    ownedInvokableMetadataNames,
                    emittedInvokableMetadataNames,
                    ownedInvokableActivatorMetadataNames,
                    compatibilityInvokableAliases,
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
        var resolvedInterfaces = new List<(
            ProxyInterfaceModel Model,
            INamedTypeSymbol Symbol,
            INamedTypeSymbol ProxyBaseType,
            int SourceOrderGroup,
            string FilePath,
            int Position)>();
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

            if (!resolver.TryResolveType(
                model.ProxyBase.ProxyBaseType,
                model.ProxyBase.MetadataIdentity,
                cancellationToken,
                out var proxyBaseType))
            {
                throw new InvalidOperationException($"Unable to resolve proxy base type '{model.ProxyBase.ProxyBaseType.SyntaxString}'.");
            }

            var sourceLocation = interfaceType.Locations.FirstOrDefault(static location => location.IsInSource);
            resolvedInterfaces.Add((
                model,
                interfaceType,
                proxyBaseType,
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
            proxyContext.VisitInterface(
                entry.Symbol.OriginalDefinition,
                entry.ProxyBaseType.OriginalDefinition,
                entry.Model.ProxyBase.IsExtension);
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
