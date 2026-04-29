using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

[Generator]
public sealed class OrleansSerializationSourceGenerator : IIncrementalGenerator
{
    internal const string GeneratorOptionsTrackingName = "Orleans.GeneratorOptions";
    internal const string AssemblyNameTrackingName = "Orleans.AssemblyName";
    internal const string SerializableTypeResultsTrackingName = "Orleans.SerializableTypeResults";
    internal const string CollectedSerializableTypesTrackingName = "Orleans.CollectedSerializableTypes";
    internal const string DirectProxyInterfacesTrackingName = "Orleans.DirectProxyInterfaces";
    internal const string InheritedProxyInterfacesTrackingName = "Orleans.InheritedProxyInterfaces";
    internal const string CollectedProxyInterfacesTrackingName = "Orleans.CollectedProxyInterfaces";
    internal const string PreparedProxyOutputsTrackingName = "Orleans.PreparedProxyOutputs";
    internal const string ReferenceAssemblyDataTrackingName = "Orleans.ReferenceAssemblyData";
    internal const string MetadataAggregateTrackingName = "Orleans.MetadataAggregate";
    internal const string SerializerOutputsTrackingName = "Orleans.SerializerOutputs";
    internal const string ReferencedSerializerOutputsTrackingName = "Orleans.ReferencedSerializerOutputs";
    internal const string ProxyOutputsTrackingName = "Orleans.ProxyOutputs";
    internal const string MetadataOutputsTrackingName = "Orleans.MetadataOutputs";

    private const string GeneratedCodeWarningDisable = "#pragma warning disable CS1591, RS0016, RS0041";
    private const string GeneratedCodeWarningRestore = "#pragma warning restore CS1591, RS0016, RS0041";
    private static int _debuggerLaunchState;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generatorOptions = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ParseOptions(provider.GlobalOptions))
            .WithTrackingName(GeneratorOptionsTrackingName);
        var compilationProvider = context.CompilationProvider;
        var assemblyNameProvider = compilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? "assembly")
            .WithTrackingName(AssemblyNameTrackingName);

        // Incremental discovery of [GenerateSerializer] types
        var serializableTypeContexts = context.SyntaxProvider
            .ForAttributeWithMetadataName<GeneratorAttributeSyntaxContext>(
                "Orleans.GenerateSerializerAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax or EnumDeclarationSyntax,
                transform: static (ctx, _) => ctx);

        var serializableTypeResults = serializableTypeContexts
            .Combine(generatorOptions)
            .Select(static (input, ct) => CreateSerializableTypeResult(
                input.Left,
                input.Right,
                ct))
            .WithTrackingName(SerializableTypeResultsTrackingName);

        var collectedSerializableTypeResults = serializableTypeResults
            .Collect()
            .Select(static (input, _) => DeduplicateSerializableTypeResults(input))
            .WithComparer(ImmutableArrayComparer<SerializableTypeResult>.Instance);

        var collectedTypes = collectedSerializableTypeResults
            .Select(static (input, _) => GetSerializableTypeModels(input))
            .WithComparer(ImmutableArrayComparer<SerializableTypeModel>.Instance)
            .WithTrackingName(CollectedSerializableTypesTrackingName);

        context.RegisterSourceOutput(collectedSerializableTypeResults.SelectMany(static (input, _) => input), static (productionContext, result) =>
        {
            if (result.Diagnostic is { } diagnostic)
            {
                productionContext.ReportDiagnostic(diagnostic);
            }
        });

        // Attribute-driven discovery of [GenerateMethodSerializers] interfaces, plus a
        // constrained syntax provider for interfaces which inherit the attribute from a base interface.
        var directProxyInterfaces = context.SyntaxProvider
            .ForAttributeWithMetadataName<ProxyInterfaceModel?>(
                "Orleans.GenerateMethodSerializersAttribute",
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, ct) => ModelExtractor.ExtractProxyInterfaceFromAttributeContext(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(DirectProxyInterfacesTrackingName);

        var inheritedProxyInterfaces = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is InterfaceDeclarationSyntax { BaseList: not null },
                transform: static (ctx, ct) => ModelExtractor.ExtractInheritedProxyInterfaceFromSyntaxContext(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (input, _) => ModelExtractor.NormalizeProxyInterfaceModels(input))
            .WithComparer(ImmutableArrayComparer<ProxyInterfaceModel>.Instance)
            .WithTrackingName(InheritedProxyInterfacesTrackingName);

        var collectedDirectProxyInterfaces = directProxyInterfaces
            .Collect()
            .Select(static (input, _) => ModelExtractor.NormalizeProxyInterfaceModels(input))
            .WithComparer(ImmutableArrayComparer<ProxyInterfaceModel>.Instance);

        var collectedProxies = collectedDirectProxyInterfaces
            .Combine(inheritedProxyInterfaces)
            .Select(static (input, _) => ModelExtractor.MergeProxyInterfaces(input.Left, input.Right))
            .WithComparer(ImmutableArrayComparer<ProxyInterfaceModel>.Instance)
            .WithTrackingName(CollectedProxyInterfacesTrackingName);

        var preparedProxyOutputs = collectedProxies
            .Combine(compilationProvider)
            .Combine(generatorOptions)
            .Select(static (input, ct) => CreateProxyOutputPreparation(input.Left.Right, input.Left.Left, input.Right, ct))
            .WithTrackingName(PreparedProxyOutputsTrackingName);

        context.RegisterSourceOutput(preparedProxyOutputs, static (productionContext, input) =>
        {
            if (input.Diagnostic is { } diagnostic)
            {
                productionContext.ReportDiagnostic(diagnostic);
            }
        });

        // Extract reference assembly data (application parts, well-known type IDs, aliases)
        var refAssemblyDataResults = compilationProvider
            .Combine(generatorOptions)
            .Select(static (input, ct) => CreateReferenceAssemblyDataResult(
                input.Left,
                input.Right,
                ct))
            .WithTrackingName(ReferenceAssemblyDataTrackingName);

        context.RegisterSourceOutput(refAssemblyDataResults, static (productionContext, result) =>
        {
            if (!result.Diagnostics.IsDefaultOrEmpty)
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            }
        });

        var refAssemblyData = refAssemblyDataResults
            .Select(static (result, _) => result.Model);

        var preparedProxyOutputModels = preparedProxyOutputs
            .Select(static (result, _) => result.ProxyOutputModels)
            .WithComparer(ImmutableArrayComparer<ProxyOutputModel>.Instance);

        // Combine source/reference models before metadata generation.
        var metadataAggregate = collectedTypes
            .Combine(preparedProxyOutputModels)
            .Combine(refAssemblyData)
            .Select(static (input, ct) => ModelExtractor.CreateMetadataAggregate(
                input.Right.AssemblyName,
                input.Left.Left,
                input.Left.Right,
                input.Right))
            .WithTrackingName(MetadataAggregateTrackingName);

        var serializerOutputs = collectedTypes
            .Combine(compilationProvider)
            .Combine(generatorOptions)
            .Select(static (input, ct) => CreateSerializableSourceOutputs(
                input.Left.Right,
                input.Left.Left,
                input.Right,
                ct))
            .WithComparer(ImmutableArrayComparer<SourceOutputResult>.Instance)
            .WithTrackingName(SerializerOutputsTrackingName);

        context.RegisterSourceOutput(serializerOutputs.SelectMany(static (input, _) => input), static (productionContext, input) =>
        {
            EmitSourceOutputResult(productionContext, input);
        });

        var referencedSerializerOutputs = refAssemblyData
            .Combine(compilationProvider)
            .Combine(generatorOptions)
            .Select(static (input, ct) => CreateReferencedSerializableSourceOutputs(
                input.Left.Right,
                input.Left.Left,
                input.Right,
                ct))
            .WithComparer(ImmutableArrayComparer<SourceOutputResult>.Instance)
            .WithTrackingName(ReferencedSerializerOutputsTrackingName);

        context.RegisterSourceOutput(referencedSerializerOutputs.SelectMany(static (input, _) => input), static (productionContext, input) =>
        {
            EmitSourceOutputResult(productionContext, input);
        });

        var proxyOutputs = preparedProxyOutputs
            .SelectMany(static (result, _) => result.SourceOutputs)
            .WithTrackingName(ProxyOutputsTrackingName);

        context.RegisterSourceOutput(proxyOutputs, static (productionContext, input) =>
        {
            EmitSourceOutputResult(productionContext, input);
        });

        context.RegisterSourceOutput(assemblyNameProvider, static (productionContext, assemblyName) =>
        {
            productionContext.AddSource($"{assemblyName}.orleans.g.cs", SourceText.From(string.Empty, Encoding.UTF8));
        });

        var metadataOutputs = metadataAggregate
            .Combine(generatorOptions)
            .Select(static (input, _) => CreateMetadataSourceOutput(input.Left, input.Right))
            .WithTrackingName(MetadataOutputsTrackingName);

        context.RegisterSourceOutput(metadataOutputs, static (productionContext, input) =>
        {
            EmitSourceOutputResult(productionContext, input);
        });
    }

    private static SerializableTypeResult CreateSerializableTypeResult(
        GeneratorAttributeSyntaxContext context,
        GeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return default;
        }

        var sourceLocation = ModelExtractor.GetSourceLocation(symbol);
        var metadataIdentity = TypeMetadataIdentity.Create(symbol);
        var typeSyntax = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttachDebuggerIfRequested(options);

            var compilation = context.SemanticModel.Compilation;
            var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
            var libraryTypes = LibraryTypes.FromCompilation(compilation, codeGeneratorOptions);
            var typeDescription = CreateSerializableTypeDescription(compilation, libraryTypes, codeGeneratorOptions, symbol);
            if (typeDescription is null)
            {
                return default;
            }

            var model = ModelExtractor.ExtractSerializableTypeModel(typeDescription, sourceLocation);
            return SerializableTypeResult.FromModel(model);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SerializableTypeResult.FromDiagnostic(
                analysisException.Diagnostic,
                metadataIdentity,
                sourceLocation,
                typeSyntax);
        }
    }

    private static ImmutableArray<SourceOutputResult> CreateSerializableSourceOutputs(
        Compilation compilation,
        ImmutableArray<SerializableTypeModel> models,
        GeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (models.IsDefaultOrEmpty)
        {
            return [];
        }

        AttachDebuggerIfRequested(options);
        var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
        var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
        var resolver = new TypeSymbolResolver(compilation);
        var assemblyName = compilation.AssemblyName ?? "assembly";
        var sourceEntries = ImmutableArray.CreateBuilder<SourceOutputResult>();
        var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();
        var serializerGenerator = new SerializerGenerator(generatorServices);
        var copierGenerator = new CopierGenerator(generatorServices);
        var activatorGenerator = new ActivatorGenerator(generatorServices);

        foreach (var model in ModelExtractor.DeduplicateSerializableTypes(models))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!resolver.TryResolveSerializableType(model, cancellationToken, out var symbol)
                    || !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
                {
                    continue;
                }

                var typeDescription = CreateSerializableTypeDescription(generatorServices, symbol);
                if (typeDescription is null)
                {
                    continue;
                }

                sourceEntries.Add(CreateSerializableSourceOutput(
                    assemblyName,
                    typeDescription,
                    serializerGenerator,
                    copierGenerator,
                    activatorGenerator,
                    defaultCopiers,
                    model.MetadataIdentity,
                    model.TypeSyntax.SyntaxString,
                    model.GeneratedNamespace,
                    model.TypeParameters.Length));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                sourceEntries.Add(SourceOutputResult.FromDiagnostic(analysisException.Diagnostic));
            }
        }

        return DeduplicateSourceOutputs(sourceEntries);
    }

    private static ReferenceAssemblyDataResult CreateReferenceAssemblyDataResult(
        Compilation compilation,
        GeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var model = ModelExtractor.ExtractReferenceAssemblyData(
                compilation,
                CreateCodeGeneratorOptions(options),
                cancellationToken,
                out var diagnostics);

            return ReferenceAssemblyDataResult.FromModelAndDiagnostics(model, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return ReferenceAssemblyDataResult.FromModelAndDiagnostics(
                CreateEmptyReferenceAssemblyModel(compilation.AssemblyName ?? string.Empty),
                [analysisException.Diagnostic]);
        }
    }

    private static ImmutableArray<SourceOutputResult> CreateReferencedSerializableSourceOutputs(
        Compilation compilation,
        ReferenceAssemblyModel referenceData,
        GeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (referenceData is null || referenceData.ReferencedSerializableTypes.IsDefaultOrEmpty)
            {
                return [];
            }

            var processedModelTypes = new HashSet<string>(StringComparer.Ordinal);
            var modelsToResolve = ImmutableArray.CreateBuilder<SerializableTypeModel>();
            foreach (var model in referenceData.ReferencedSerializableTypes
                .Distinct()
                .OrderBy(static model => model.TypeSyntax.SyntaxString, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.MetadataName, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
                .ThenBy(static model => model.GeneratedNamespace, StringComparer.Ordinal)
                .ThenBy(static model => model.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCurrentCompilationAssembly(model.MetadataIdentity, compilation))
                {
                    continue;
                }

                var modelTypeKey = $"{model.MetadataIdentity.AssemblyIdentity}|{model.MetadataIdentity.AssemblyName}|{model.MetadataIdentity.MetadataName}|{model.GeneratedNamespace}|{model.Name}|{model.TypeSyntax.SyntaxString}";
                if (processedModelTypes.Add(modelTypeKey))
                {
                    modelsToResolve.Add(model);
                }
            }

            if (modelsToResolve.Count == 0)
            {
                return [];
            }

            AttachDebuggerIfRequested(options);
            var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
            var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
            var resolver = new TypeSymbolResolver(compilation);
            var assemblyName = compilation.AssemblyName ?? "assembly";
            var sourceEntries = ImmutableArray.CreateBuilder<SourceOutputResult>();
            var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();
            var serializerGenerator = new SerializerGenerator(generatorServices);
            var copierGenerator = new CopierGenerator(generatorServices);
            var activatorGenerator = new ActivatorGenerator(generatorServices);

            foreach (var model in modelsToResolve)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!resolver.TryResolveSerializableType(model, cancellationToken, out var symbol)
                    || SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
                {
                    continue;
                }

                var typeDescription = CreateSerializableTypeDescription(generatorServices, symbol);
                if (typeDescription is null)
                {
                    continue;
                }

                sourceEntries.Add(CreateSerializableSourceOutput(
                    assemblyName,
                    typeDescription,
                    serializerGenerator,
                    copierGenerator,
                    activatorGenerator,
                    defaultCopiers,
                    model.MetadataIdentity,
                    model.TypeSyntax.SyntaxString,
                    model.GeneratedNamespace,
                    model.TypeParameters.Length));
            }

            return DeduplicateSourceOutputs(sourceEntries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return [SourceOutputResult.FromDiagnostic(analysisException.Diagnostic)];
        }
    }

    private static SourceOutputResult CreateSerializableSourceOutput(
        string assemblyName,
        ISerializableTypeDescription typeDescription,
        SerializerGenerator serializerGenerator,
        CopierGenerator copierGenerator,
        ActivatorGenerator activatorGenerator,
        Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers,
        TypeMetadataIdentity metadataIdentity,
        string typeSyntax,
        string hintGeneratedNamespace,
        int genericArity)
    {
        var serializer = serializerGenerator.Generate(typeDescription);
        var copier = typeDescription.IsShallowCopyable && defaultCopiers.ContainsKey(typeDescription)
            ? null
            : copierGenerator.GenerateCopier(typeDescription, defaultCopiers);
        var activatorClass = ActivatorGenerator.ShouldGenerateActivator(typeDescription)
            ? activatorGenerator.GenerateActivator(typeDescription)
            : null;

        return SourceOutputResult.FromSource(
            CreateSerializableSourceEntry(
                assemblyName,
                typeSyntax,
                metadataIdentity,
                hintGeneratedNamespace,
                genericArity,
                serializer,
                copier,
                activatorClass,
                typeDescription.GeneratedNamespace));
    }

    private static SourceOutputResult CreateProxySourceOutput(
        Compilation compilation,
        TypeSymbolResolver resolver,
        ProxyOutputModel proxyOutputModel,
        GeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            AttachDebuggerIfRequested(options);
            var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
            var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
            var proxyContext = new ProxyGenerationContext(compilation, codeGeneratorOptions);
            var model = proxyOutputModel.ProxyInterface;
            PopulateProxyInterfaces(proxyContext, resolver, [model], cancellationToken);

            var assemblyName = compilation.AssemblyName ?? "assembly";
            var interfaceDescription = GetProxyInterfaceDescription(proxyContext, resolver, model, cancellationToken);
            var proxyGenerator = new ProxyGenerator(generatorServices, new CopierGenerator(generatorServices));
            var (proxyClass, _) = proxyGenerator.Generate(interfaceDescription);
            var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription);
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
                AddMember(namespacedMembers, invokable.GeneratedNamespace, invokable.ClassDeclarationSyntax);
            }

            AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, proxyClass);

            foreach (var invokable in emittedInvokables)
            {
                AddMember(namespacedMembers, invokable.GeneratedNamespace, serializerGenerator.Generate(invokable));

                var copier = invokable.IsShallowCopyable && proxyContext.MetadataModel.DefaultCopiers.ContainsKey(invokable)
                    ? null
                    : copierGenerator.GenerateCopier(invokable, proxyContext.MetadataModel.DefaultCopiers);
                if (copier is not null)
                {
                    AddMember(namespacedMembers, invokable.GeneratedNamespace, copier);
                }

                if (ActivatorGenerator.ShouldGenerateActivator(invokable))
                {
                    AddMember(namespacedMembers, invokable.GeneratedNamespace, activatorGenerator.GenerateActivator(invokable));
                }
            }

            foreach (var (generatedNamespace, classDeclaration) in additionalInvokableClasses)
            {
                AddMember(namespacedMembers, generatedNamespace, classDeclaration);
            }

            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(targetHintName, CreateSourceString(CreateCompilationUnit(namespacedMembers))));
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

    private static SourceOutputResult CreateProxySourceOutput(
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
            var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription);
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
                AddMember(namespacedMembers, invokable.GeneratedNamespace, invokable.ClassDeclarationSyntax);
            }

            AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, proxyClass);

            foreach (var invokable in emittedInvokables)
            {
                AddMember(namespacedMembers, invokable.GeneratedNamespace, serializerGenerator.Generate(invokable));

                var copier = invokable.IsShallowCopyable && defaultCopiers.ContainsKey(invokable)
                    ? null
                    : copierGenerator.GenerateCopier(invokable, defaultCopiers);
                if (copier is not null)
                {
                    AddMember(namespacedMembers, invokable.GeneratedNamespace, copier);
                }

                if (ActivatorGenerator.ShouldGenerateActivator(invokable))
                {
                    AddMember(namespacedMembers, invokable.GeneratedNamespace, activatorGenerator.GenerateActivator(invokable));
                }
            }

            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(targetHintName, CreateSourceString(CreateCompilationUnit(namespacedMembers))));
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

    private static ProxyOutputPreparationResult CreateProxyOutputPreparation(
        Compilation compilation,
        ImmutableArray<ProxyInterfaceModel> models,
        GeneratorOptions options,
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

            var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
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

    private static ImmutableArray<SourceOutputResult> CreateProxySourceOutputs(
        Compilation compilation,
        ProxyGenerationContext proxyContext,
        IGeneratorServices generatorServices,
        TypeSymbolResolver resolver,
        ImmutableArray<ProxyOutputModel> proxyOutputModels,
        GeneratorOptions options,
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
            AttachDebuggerIfRequested(options);
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

        return DeduplicateSourceOutputs(sourceOutputs);
    }

    private static ImmutableArray<ProxyOutputModel> CreateProxyOutputModels(
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
            .Select(desc => (HintName: CreateProxyHintName(assemblyName, desc), Description: desc))
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
                var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription);
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

    private static bool ShouldEmitInvokable(
        GeneratedInvokableDescription invokable,
        INamedTypeSymbol interfaceType,
        HashSet<string> ownedInvokableMetadataNames,
        bool useDeclaredInvokableFallback)
        => ownedInvokableMetadataNames.Contains(invokable.MetadataName)
            || useDeclaredInvokableFallback
                && SymbolEqualityComparer.Default.Equals(invokable.MethodDescription.ContainingInterface, interfaceType);

    private static SourceOutputResult CreateMetadataSourceOutput(
        MetadataAggregateModel metadataModel,
        GeneratorOptions options)
    {
        try
        {
            AttachDebuggerIfRequested(options);
            var metadataGenerator = new MetadataGenerator(metadataModel, metadataModel.AssemblyName);
            var metadataClass = metadataGenerator.GenerateMetadata();
            var metadataNamespace = $"{GeneratedCodeUtilities.CodeGeneratorName}.{Identifier.SanitizeIdentifierName(metadataModel.AssemblyName ?? "Assembly")}";
            var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
            AddMember(namespacedMembers, metadataNamespace, metadataClass);
            var assemblyAttributes = CreateAssemblyAttributes(
                metadataModel.ReferenceAssemblyData.ApplicationParts,
                metadataNamespace,
                metadataClass.Identifier.Text);

            var assemblyName = metadataModel.AssemblyName ?? "assembly";
            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(
                    CreateMetadataHintName(assemblyName),
                    CreateSourceString(CreateCompilationUnit(namespacedMembers, assemblyAttributes))));
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SourceOutputResult.FromDiagnostic(analysisException.Diagnostic);
        }
    }

    private static void PopulateProxyInterfaces(
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

    private static ProxyInterfaceDescription GetProxyInterfaceDescription(
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

    private static SyntaxList<AttributeListSyntax> CreateAssemblyAttributes(
        IEnumerable<string> applicationParts,
        string metadataNamespace,
        string metadataClassName)
    {
        var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateSyntax(
            SyntaxFactory.ParseName("global::Orleans.ApplicationPartAttribute"),
            applicationParts);
        var metadataAttribute = SyntaxFactory.AttributeList()
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
            .WithAttributes(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute"))
                        .AddArgumentListArguments(
                            SyntaxFactory.AttributeArgument(
                                SyntaxFactory.TypeOfExpression(
                                    SyntaxFactory.QualifiedName(
                                        SyntaxFactory.ParseName(metadataNamespace),
                                        SyntaxFactory.IdentifierName(metadataClassName)))))));
        assemblyAttributes.Add(metadataAttribute);

        return SyntaxFactory.List(assemblyAttributes);
    }

    private static ISerializableTypeDescription? CreateSerializableTypeDescription(IGeneratorServices services, INamedTypeSymbol symbol)
        => CreateSerializableTypeDescription(services.Compilation, services.LibraryTypes, services.Options, symbol);

    private static ISerializableTypeDescription? CreateSerializableTypeDescription(Compilation compilation, LibraryTypes libraryTypes, CodeGeneratorOptions options, INamedTypeSymbol symbol)
    {

        if (FSharpUtilities.IsUnionCase(libraryTypes, symbol, out var sumType) && sumType.HasAttribute(libraryTypes.GenerateSerializerAttribute))
        {
            if (!compilation.IsSymbolAccessibleWithin(sumType, compilation.Assembly))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType));
            }

            return new FSharpUtilities.FSharpUnionCaseTypeDescription(compilation, symbol, libraryTypes);
        }

        if (!symbol.HasAttribute(libraryTypes.GenerateSerializerAttribute))
        {
            return null;
        }

        if (HasReferenceAssemblyAttribute(symbol.ContainingAssembly))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(ReferenceAssemblyWithGenerateSerializerDiagnostic.CreateDiagnostic(symbol));
        }

        if (!compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(symbol));
        }

        if (FSharpUtilities.IsRecord(libraryTypes, symbol))
        {
            return new FSharpUtilities.FSharpRecordTypeDescription(compilation, symbol, libraryTypes);
        }

        var includePrimaryConstructorParameters = ShouldIncludePrimaryConstructorParameters(symbol, libraryTypes);
        var constructorParameters = ResolveConstructorParameters(symbol, includePrimaryConstructorParameters, libraryTypes);
        var implicitMemberSelectionStrategy = (options.GenerateFieldIds, GetGenerateFieldIdsOptionFromType(symbol, libraryTypes)) switch
        {
            (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
            (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
            _ => GenerateFieldIds.None,
        };
        var fieldIdAssignmentHelper = new FieldIdAssignmentHelper(symbol, constructorParameters, implicitMemberSelectionStrategy, libraryTypes);
        if (!fieldIdAssignmentHelper.IsValidForSerialization)
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason!));
        }

        return new SerializableTypeDescription(compilation, symbol, includePrimaryConstructorParameters, GetDataMembers(fieldIdAssignmentHelper), libraryTypes);
    }

    private static bool HasReferenceAssemblyAttribute(IAssemblySymbol assembly)
    {
        return assembly.GetAttributes().Any(attributeData => attributeData.AttributeClass is
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
        });
    }

    private static GenerateFieldIds GetGenerateFieldIdsOptionFromType(INamedTypeSymbol type, LibraryTypes libraryTypes)
    {
        var attribute = type.GetAttribute(libraryTypes.GenerateSerializerAttribute);
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

    private static bool ShouldIncludePrimaryConstructorParameters(INamedTypeSymbol type, LibraryTypes libraryTypes)
    {
        static bool? GetNamedOption(INamedTypeSymbol type, INamedTypeSymbol attributeType)
        {
            var attribute = type.GetAttribute(attributeType);
            if (attribute is null)
            {
                return null;
            }

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "IncludePrimaryConstructorParameters"
                    && namedArgument.Value.Kind == TypedConstantKind.Primitive
                    && namedArgument.Value.Value is bool value)
                {
                    return value;
                }
            }

            return null;
        }

        if (GetNamedOption(type, libraryTypes.GenerateSerializerAttribute) is bool includePrimaryCtorParameters)
        {
            return includePrimaryCtorParameters;
        }

        if (type.IsRecord)
        {
            return true;
        }

        var properties = type.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
        return type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Constructor && method.Parameters.Length > 0)
            .Any(ctor => ctor.Parameters.All(parameter =>
                properties.Any(property => property.Name.Equals(parameter.Name, StringComparison.Ordinal) && property.IsCompilerGenerated())));
    }

    private static ImmutableArray<IParameterSymbol> ResolveConstructorParameters(
        INamedTypeSymbol type,
        bool includePrimaryConstructorParameters,
        LibraryTypes libraryTypes)
    {
        if (!includePrimaryConstructorParameters)
        {
            return [];
        }

        if (type.IsRecord)
        {
            var potentialPrimaryConstructor = type.Constructors[0];
            if (!potentialPrimaryConstructor.IsImplicitlyDeclared && !potentialPrimaryConstructor.IsCompilerGenerated())
            {
                return potentialPrimaryConstructor.Parameters;
            }
        }
        else
        {
            var annotatedConstructors = type.Constructors.Where(ctor => ctor.HasAnyAttribute(libraryTypes.ConstructorAttributeTypes)).ToList();
            if (annotatedConstructors.Count == 1)
            {
                return annotatedConstructors[0].Parameters;
            }

            var properties = type.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
            var primaryConstructor = type.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(static method => method.MethodKind == MethodKind.Constructor && method.Parameters.Length > 0)
                .FirstOrDefault(ctor => ctor.Parameters.All(parameter =>
                    properties.Any(property => property.Name.Equals(parameter.Name, StringComparison.Ordinal) && property.IsCompilerGenerated())));
            if (primaryConstructor is not null)
            {
                return primaryConstructor.Parameters;
            }
        }

        return [];
    }

    private static IEnumerable<IMemberDescription> GetDataMembers(FieldIdAssignmentHelper fieldIdAssignmentHelper)
    {
        var members = new Dictionary<(uint Id, bool IsConstructorParameter), IMemberDescription>();
        foreach (var member in fieldIdAssignmentHelper.Members)
        {
            if (!fieldIdAssignmentHelper.TryGetSymbolKey(member, out var key))
            {
                continue;
            }

            var (id, isConstructorParameter) = key;
            if (member is IPropertySymbol property
                && !members.TryGetValue((id, isConstructorParameter), out _))
            {
                members[(id, isConstructorParameter)] = new PropertyDescription(id, isConstructorParameter, property);
            }

            if (member is IFieldSymbol field)
            {
                if (!members.TryGetValue((id, isConstructorParameter), out var existing)
                    || existing is PropertyDescription)
                {
                    members[(id, isConstructorParameter)] = new FieldDescription(id, isConstructorParameter, field);
                }
            }
        }

        return members.Values;
    }

    private static bool IsCurrentCompilationAssembly(TypeMetadataIdentity metadataIdentity, Compilation compilation)
    {
        if (metadataIdentity.IsEmpty)
        {
            return false;
        }

        var assemblyIdentity = compilation.Assembly.Identity;
        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity))
        {
            return string.Equals(metadataIdentity.AssemblyIdentity, assemblyIdentity.GetDisplayName(), StringComparison.Ordinal);
        }

        return !string.IsNullOrEmpty(metadataIdentity.AssemblyName)
            && string.Equals(metadataIdentity.AssemblyName, assemblyIdentity.Name, StringComparison.Ordinal);
    }

    private sealed class TypeSymbolResolver(Compilation compilation)
    {
        private readonly Compilation _compilation = compilation;
        private FallbackIndex? _fallbackIndex;

        public bool TryResolveSerializableType(
            SerializableTypeModel model,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out INamedTypeSymbol? symbol)
        {
            if (model is null)
            {
                symbol = null;
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (TryResolveMetadataIdentity(model.MetadataIdentity, cancellationToken, out symbol)
                || TryResolveTypeSyntax(model.TypeSyntax.SyntaxString, cancellationToken, out symbol))
            {
                return true;
            }

            foreach (var candidate in GetFallbackIndex(cancellationToken).AllTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.GetNamespaceAndNesting(), model.Namespace, StringComparison.Ordinal)
                    && candidate.GetAllTypeParameters().Count() == model.TypeParameters.Length)
                {
                    symbol = candidate;
                    return true;
                }
            }

            symbol = null;
            return false;
        }

        public bool TryResolveProxyInterface(
            ProxyInterfaceModel model,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out INamedTypeSymbol? symbol)
        {
            if (model is null)
            {
                symbol = null;
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (TryResolveMetadataIdentity(model.MetadataIdentity, cancellationToken, out symbol)
                || TryResolveTypeSyntax(model.InterfaceType.SyntaxString, cancellationToken, out symbol))
            {
                return symbol.TypeKind == TypeKind.Interface;
            }

            foreach (var candidate in GetFallbackIndex(cancellationToken).AllTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.TypeKind == TypeKind.Interface
                    && string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), model.InterfaceType.SyntaxString, StringComparison.Ordinal))
                {
                    symbol = candidate;
                    return true;
                }
            }

            symbol = null;
            return false;
        }

        private bool TryResolveMetadataIdentity(
            TypeMetadataIdentity metadataIdentity,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out INamedTypeSymbol? symbol)
        {
            if (metadataIdentity.IsEmpty)
            {
                symbol = null;
                return false;
            }

            if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity)
                || !string.IsNullOrEmpty(metadataIdentity.AssemblyName))
            {
                if (TryGetAssembly(metadataIdentity, cancellationToken, out var assembly))
                {
                    symbol = assembly.GetTypeByMetadataName(metadataIdentity.MetadataName);
                    return symbol is not null;
                }

                symbol = null;
                return false;
            }

            return TryResolveMetadataName(metadataIdentity.MetadataName, out symbol);
        }

        private bool TryGetAssembly(
            TypeMetadataIdentity metadataIdentity,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out IAssemblySymbol? assembly)
        {
            if (IsMatchingAssembly(_compilation.Assembly, metadataIdentity))
            {
                assembly = _compilation.Assembly;
                return true;
            }

            IAssemblySymbol? assemblyByName = null;
            foreach (var reference in _compilation.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol candidate)
                {
                    continue;
                }

                if (IsMatchingAssembly(candidate, metadataIdentity))
                {
                    assembly = candidate;
                    return true;
                }

                if (string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity)
                    && !string.IsNullOrEmpty(metadataIdentity.AssemblyName)
                    && string.Equals(candidate.Identity.Name, metadataIdentity.AssemblyName, StringComparison.Ordinal))
                {
                    if (assemblyByName is not null)
                    {
                        assembly = null;
                        return false;
                    }

                    assemblyByName = candidate;
                }
            }

            if (assemblyByName is not null)
            {
                assembly = assemblyByName;
                return true;
            }

            assembly = null;
            return false;
        }

        private static bool IsMatchingAssembly(IAssemblySymbol assembly, TypeMetadataIdentity metadataIdentity)
        {
            if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity))
            {
                return string.Equals(assembly.Identity.GetDisplayName(), metadataIdentity.AssemblyIdentity, StringComparison.Ordinal);
            }

            return !string.IsNullOrEmpty(metadataIdentity.AssemblyName)
                && string.Equals(assembly.Identity.Name, metadataIdentity.AssemblyName, StringComparison.Ordinal);
        }

        private bool TryResolveTypeSyntax(
            string typeSyntax,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out INamedTypeSymbol? symbol)
        {
            if (string.IsNullOrWhiteSpace(typeSyntax))
            {
                symbol = null;
                return false;
            }

            if (TryGetMetadataName(typeSyntax, allowGenericSyntax: false, out var metadataName)
                && TryResolveMetadataName(metadataName, out symbol))
            {
                return true;
            }

            var fallbackIndex = GetFallbackIndex(cancellationToken);
            if (fallbackIndex.TypesByKey.TryGetValue(NormalizeTypeKey(typeSyntax), out symbol))
            {
                return true;
            }

            return TryGetMetadataName(typeSyntax, allowGenericSyntax: true, out metadataName)
                && TryResolveMetadataName(metadataName, out symbol);
        }

        private bool TryResolveMetadataName(string metadataName, [NotNullWhen(true)] out INamedTypeSymbol? symbol)
        {
            symbol = _compilation.GetTypeByMetadataName(metadataName);
            if (symbol is null && TryGetSpecialType(metadataName, out var specialType))
            {
                symbol = _compilation.GetSpecialType(specialType);
            }

            return symbol is not null;
        }

        private static bool TryGetMetadataName(string typeSyntax, bool allowGenericSyntax, [NotNullWhen(true)] out string? metadataName)
        {
            metadataName = typeSyntax.Trim();
            if (metadataName.StartsWith("global::", StringComparison.Ordinal))
            {
                metadataName = metadataName.Substring("global::".Length);
            }

            var genericStart = metadataName.IndexOf('<');
            if (genericStart >= 0)
            {
                if (!allowGenericSyntax)
                {
                    metadataName = null;
                    return false;
                }

                metadataName = metadataName.Substring(0, genericStart);
            }

            metadataName = metadataName.Trim();
            if (metadataName.StartsWith("global::", StringComparison.Ordinal))
            {
                metadataName = metadataName.Substring("global::".Length);
            }

            metadataName = metadataName switch
            {
                "bool" => "System.Boolean",
                "byte" => "System.Byte",
                "sbyte" => "System.SByte",
                "short" => "System.Int16",
                "ushort" => "System.UInt16",
                "int" => "System.Int32",
                "uint" => "System.UInt32",
                "long" => "System.Int64",
                "ulong" => "System.UInt64",
                "float" => "System.Single",
                "double" => "System.Double",
                "decimal" => "System.Decimal",
                "char" => "System.Char",
                "string" => "System.String",
                "object" => "System.Object",
                _ => metadataName,
            };

            return !string.IsNullOrWhiteSpace(metadataName);
        }

        private static bool TryGetSpecialType(string metadataName, out SpecialType specialType)
        {
            specialType = metadataName switch
            {
                "System.Boolean" => SpecialType.System_Boolean,
                "System.Byte" => SpecialType.System_Byte,
                "System.SByte" => SpecialType.System_SByte,
                "System.Int16" => SpecialType.System_Int16,
                "System.UInt16" => SpecialType.System_UInt16,
                "System.Int32" => SpecialType.System_Int32,
                "System.UInt32" => SpecialType.System_UInt32,
                "System.Int64" => SpecialType.System_Int64,
                "System.UInt64" => SpecialType.System_UInt64,
                "System.Single" => SpecialType.System_Single,
                "System.Double" => SpecialType.System_Double,
                "System.Decimal" => SpecialType.System_Decimal,
                "System.Char" => SpecialType.System_Char,
                "System.String" => SpecialType.System_String,
                "System.Object" => SpecialType.System_Object,
                _ => SpecialType.None,
            };

            return specialType != SpecialType.None;
        }

        private FallbackIndex GetFallbackIndex(CancellationToken cancellationToken)
        {
            if (_fallbackIndex is { } fallbackIndex)
            {
                return fallbackIndex;
            }

            fallbackIndex = BuildFallbackIndex(cancellationToken);
            _fallbackIndex = fallbackIndex;
            return fallbackIndex;
        }

        private FallbackIndex BuildFallbackIndex(CancellationToken cancellationToken)
        {
            var typesByKey = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            var allTypes = new List<INamedTypeSymbol>();
            AddAssembly(_compilation.Assembly);

            foreach (var reference in _compilation.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                {
                    AddAssembly(assembly);
                }
            }

            return new FallbackIndex(typesByKey, allTypes);

            void AddAssembly(IAssemblySymbol assembly)
            {
                foreach (var type in assembly.GetDeclaredTypes())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddType(type);
                }
            }

            void AddType(INamedTypeSymbol type)
            {
                allTypes.Add(type);
                AddKey(type.ToOpenTypeSyntax().ToString(), type);
                AddKey(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), type);
                AddKey(type.ToDisplayString(), type);
            }

            void AddKey(string key, INamedTypeSymbol type)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                var normalizedKey = NormalizeTypeKey(key);
                if (!typesByKey.TryGetValue(normalizedKey, out _))
                {
                    typesByKey.Add(normalizedKey, type);
                }
            }
        }

        private sealed class FallbackIndex(Dictionary<string, INamedTypeSymbol> typesByKey, List<INamedTypeSymbol> allTypes)
        {
            public Dictionary<string, INamedTypeSymbol> TypesByKey { get; } = typesByKey;
            public List<INamedTypeSymbol> AllTypes { get; } = allTypes;
        }
    }

    private static string NormalizeTypeKey(string value)
        => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    private static void EmitSourceOutputResult(SourceProductionContext context, SourceOutputResult result)
    {
        if (result.Diagnostic is { } diagnostic)
        {
            context.ReportDiagnostic(diagnostic);
            return;
        }

        if (result.SourceEntry is { } sourceEntry)
        {
            context.AddSource(sourceEntry.HintName, sourceEntry.SourceText);
        }
    }

    private static ImmutableArray<SerializableTypeResult> DeduplicateSerializableTypeResults(
        ImmutableArray<SerializableTypeResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            return [];
        }

        var models = new Dictionary<string, SerializableTypeResult>(StringComparer.Ordinal);
        var diagnostics = new Dictionary<string, SerializableTypeResult>(StringComparer.Ordinal);
        foreach (var result in OrderSerializableTypeResultsForCanonicalSelection(results))
        {
            if (result.Model is not null)
            {
                var key = CreateSerializableTypeDedupeKey(result);
                if (!models.ContainsKey(key))
                {
                    models.Add(key, result);
                }
            }
            else if (result.Diagnostic is { } diagnostic)
            {
                var key = $"{CreateSerializableTypeDedupeKey(result)}|{diagnostic.Id}";
                if (!diagnostics.ContainsKey(key))
                {
                    diagnostics.Add(key, result);
                }
            }
        }

        return [.. OrderSerializableTypeResultsForEmission(models.Values.Concat(diagnostics.Values))];
    }

    private static ImmutableArray<SerializableTypeModel> GetSerializableTypeModels(
        ImmutableArray<SerializableTypeResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<SerializableTypeModel>();
        foreach (var result in results)
        {
            if (result.Model is { } model)
            {
                builder.Add(model);
            }
        }

        return ModelExtractor.DeduplicateSerializableTypes(builder.ToImmutable());
    }

    private static IOrderedEnumerable<SerializableTypeResult> OrderSerializableTypeResultsForCanonicalSelection(
        IEnumerable<SerializableTypeResult> results)
        => results
            .Where(static result => result.Model is not null || result.Diagnostic is not null)
            .OrderBy(static result => result.SourceLocation.SourceOrderGroup)
            .ThenBy(static result => result.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static result => result.SourceLocation.Position)
            .ThenBy(static result => result.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static result => result.TypeSyntax, StringComparer.Ordinal)
            .ThenBy(static result => result.Diagnostic?.Id ?? string.Empty, StringComparer.Ordinal);

    private static IOrderedEnumerable<SerializableTypeResult> OrderSerializableTypeResultsForEmission(
        IEnumerable<SerializableTypeResult> results)
        => results
            .OrderBy(static result => result.Model is null ? 1 : 0)
            .ThenBy(static result => result.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static result => result.TypeSyntax, StringComparer.Ordinal)
            .ThenBy(static result => result.Diagnostic?.Id ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static result => result.SourceLocation.SourceOrderGroup)
            .ThenBy(static result => result.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static result => result.SourceLocation.Position);

    private static string CreateSerializableTypeDedupeKey(SerializableTypeResult result)
        => CreateTypeDedupeKey(result.MetadataIdentity, result.TypeSyntax);

    private static string CreateTypeDedupeKey(TypeMetadataIdentity metadataIdentity, string typeSyntax)
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

        return string.Join("|", "S", typeSyntax ?? string.Empty);
    }

    private static ImmutableArray<SourceOutputResult> DeduplicateSourceOutputs(
        ImmutableArray<SourceOutputResult>.Builder sourceEntries)
        => DeduplicateSourceOutputs(sourceEntries.ToImmutable());

    private static ImmutableArray<SourceOutputResult> DeduplicateSourceOutputs(
        ImmutableArray<SourceOutputResult> sourceEntries)
    {
        var emittedSourcesByOriginalHintName = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var emittedSourceByHintName = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<SourceOutputResult>();
        foreach (var sourceOutput in sourceEntries)
        {
            if (sourceOutput.SourceEntry is not { } entry)
            {
                result.Add(sourceOutput);
                continue;
            }

            var source = entry.Source ?? string.Empty;
            if (!emittedSourcesByOriginalHintName.TryGetValue(entry.HintName, out var emittedSources))
            {
                emittedSources = new Dictionary<string, string>(StringComparer.Ordinal);
                emittedSourcesByOriginalHintName.Add(entry.HintName, emittedSources);
            }

            if (emittedSources.ContainsKey(source))
            {
                continue;
            }

            if (!emittedSourceByHintName.TryGetValue(entry.HintName, out var emittedSource))
            {
                emittedSources.Add(source, entry.HintName);
                emittedSourceByHintName.Add(entry.HintName, source);
                result.Add(sourceOutput);
                continue;
            }

            if (string.Equals(emittedSource, source, StringComparison.Ordinal))
            {
                emittedSources.Add(source, entry.HintName);
                continue;
            }

            var uniqueHintName = CreateDistinctSourceHintName(entry.HintName, source, emittedSourceByHintName);
            emittedSources.Add(source, uniqueHintName);
            emittedSourceByHintName.Add(uniqueHintName, source);
            result.Add(SourceOutputResult.FromSource(new GeneratedSourceEntry(uniqueHintName, source)));
        }

        return NormalizeSourceOutputs(result.ToImmutable());
    }

    private static ImmutableArray<SourceOutputResult> NormalizeSourceOutputs(ImmutableArray<SourceOutputResult> sourceOutputs)
        => StructuralEquality.Normalize(sourceOutputs);

    private static GeneratedSourceEntry CreateSerializableSourceEntry(
        string assemblyName,
        string typeName,
        TypeMetadataIdentity metadataIdentity,
        string hintGeneratedNamespace,
        int genericArity,
        ClassDeclarationSyntax serializer,
        ClassDeclarationSyntax? copier,
        ClassDeclarationSyntax? activator,
        string generatedNamespace)
    {
        var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
        AddMember(namespacedMembers, generatedNamespace, serializer);
        if (copier is not null)
        {
            AddMember(namespacedMembers, generatedNamespace, copier);
        }

        if (activator is not null)
        {
            AddMember(namespacedMembers, generatedNamespace, activator);
        }

        return new GeneratedSourceEntry(
            CreateSerializableHintName(assemblyName, typeName, metadataIdentity, hintGeneratedNamespace, genericArity),
            CreateSourceString(CreateCompilationUnit(namespacedMembers)));
    }

    private static void AddMember(
        Dictionary<string, List<MemberDeclarationSyntax>> namespacedMembers,
        string ns,
        MemberDeclarationSyntax member)
    {
        var namespaceName = ns ?? string.Empty;
        if (!namespacedMembers.TryGetValue(namespaceName, out var members))
        {
            members = [];
            namespacedMembers[namespaceName] = members;
        }

        members.Add(member);
    }

    private static string CreateSourceString(CompilationUnitSyntax unit)
    {
        return $"{GeneratedCodeWarningDisable}\r\n{unit.NormalizeWhitespace().ToFullString()}\r\n{GeneratedCodeWarningRestore}";
    }

    private static CompilationUnitSyntax CreateCompilationUnit(
        Dictionary<string, List<MemberDeclarationSyntax>> namespacedMembers,
        SyntaxList<AttributeListSyntax> attributeLists = default)
    {
        var unit = SyntaxFactory.CompilationUnit().WithAttributeLists(attributeLists);
        var usingDirectives = SyntaxFactory.List(
        [
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::Orleans.Serialization.Codecs")),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")),
        ]);
        var members = new List<MemberDeclarationSyntax>(namespacedMembers.Count);

        foreach (var pair in namespacedMembers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                members.AddRange(pair.Value);
                continue;
            }

            members.Add(
                SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(pair.Key))
                    .WithUsings(usingDirectives)
                    .WithMembers(SyntaxFactory.List(pair.Value)));
        }

        return unit.WithMembers(SyntaxFactory.List(members));
    }

    private static CodeGeneratorOptions CreateCodeGeneratorOptions(GeneratorOptions options)
    {
        return new CodeGeneratorOptions
        {
            GenerateFieldIds = options.GenerateFieldIds,
            GenerateCompatibilityInvokers = options.GenerateCompatibilityInvokers,
        };
    }

    private static string CreateSerializableHintName(
        string assemblyName,
        string typeName,
        TypeMetadataIdentity metadataIdentity,
        string generatedNamespace,
        int genericArity)
    {
        var hash = CreateHintNameHash(metadataIdentity, generatedNamespace, typeName, genericArity);

        return $"{assemblyName}.orleans.ser.{SanitizeHintComponent(typeName)}.{hash}.g.cs";
    }

    private static string CreateProxyHintName(string assemblyName, ProxyInterfaceDescription interfaceDescription)
    {
        var interfaceName = interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var hash = CreateHintNameHash(
            TypeMetadataIdentity.Create(interfaceDescription.InterfaceType),
            interfaceDescription.GeneratedNamespace,
            interfaceName,
            interfaceDescription.TypeParameters.Count);

        return $"{assemblyName}.orleans.proxy.{SanitizeHintComponent(interfaceName)}.{hash}.g.cs";
    }

    private static string CreateMetadataHintName(string assemblyName)
        => $"{assemblyName}.orleans.metadata.g.cs";

    private static string CreateHintNameHash(
        TypeMetadataIdentity metadataIdentity,
        string generatedNamespace,
        string syntaxString,
        int genericArity)
    {
        var builder = new StringBuilder();
        AppendHashComponent(builder, metadataIdentity.AssemblyIdentity);
        AppendHashComponent(builder, metadataIdentity.AssemblyName);
        AppendHashComponent(builder, metadataIdentity.MetadataName);
        AppendHashComponent(builder, generatedNamespace);
        AppendHashComponent(builder, syntaxString);
        AppendHashComponent(builder, genericArity.ToString(CultureInfo.InvariantCulture));

        return CreateStableHash(builder.ToString());
    }

    private static string CreateStableHash(string value)
        => HexConverter.ToString(XxHash32.Hash(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static void AppendHashComponent(StringBuilder builder, string value)
    {
        builder.Append(value?.Length ?? 0);
        builder.Append(':');
        builder.Append(value ?? string.Empty);
        builder.Append('|');
    }

    private static string CreateDistinctSourceHintName(
        string hintName,
        string source,
        Dictionary<string, string> emittedSourceByHintName)
    {
        var sourceHash = CreateStableHash(source);
        var candidate = InsertHintNameComponent(hintName, $"collision.{sourceHash}");
        if (!emittedSourceByHintName.ContainsKey(candidate))
        {
            return candidate;
        }

        for (var index = 1; ; index++)
        {
            candidate = InsertHintNameComponent(hintName, $"collision.{sourceHash}.{index}");
            if (!emittedSourceByHintName.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static string InsertHintNameComponent(string hintName, string component)
    {
        const string GeneratedSourceSuffix = ".g.cs";
        if (hintName.EndsWith(GeneratedSourceSuffix, StringComparison.Ordinal))
        {
            return $"{hintName.Substring(0, hintName.Length - GeneratedSourceSuffix.Length)}.{component}{GeneratedSourceSuffix}";
        }

        const string SourceSuffix = ".cs";
        if (hintName.EndsWith(SourceSuffix, StringComparison.Ordinal))
        {
            return $"{hintName.Substring(0, hintName.Length - SourceSuffix.Length)}.{component}{SourceSuffix}";
        }

        return $"{hintName}.{component}";
    }

    private static string SanitizeHintComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "generated";
        }

        var builder = new StringBuilder(value.Length);
        var previousCharacterWasUnderscore = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '.')
            {
                builder.Append(character);
                previousCharacterWasUnderscore = false;
            }
            else if (!previousCharacterWasUnderscore)
            {
                builder.Append('_');
                previousCharacterWasUnderscore = true;
            }
        }

        var result = builder.ToString().Trim('_', '.');
        return result.Length > 0 ? result : "generated";
    }

    private static IEnumerable<GeneratedInvokableDescription> GetGeneratedInvokables(
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

    private static void AttachDebuggerIfRequested(GeneratorOptions options)
    {
        if (!options.AttachDebugger || Debugger.IsAttached)
        {
            return;
        }

        if (Interlocked.Exchange(ref _debuggerLaunchState, 1) == 0)
        {
            Debugger.Launch();
        }
    }

    private static GeneratorOptions ParseOptions(AnalyzerConfigOptions globalOptions)
    {
        var result = new GeneratorOptions();

        if (globalOptions.TryGetValue("build_property.orleans_attachdebugger", out var attachDebuggerOption)
            && string.Equals("true", attachDebuggerOption, StringComparison.OrdinalIgnoreCase))
        {
            result.AttachDebugger = true;
        }

        if (globalOptions.TryGetValue("build_property.orleans_generatefieldids", out var generateFieldIds) && generateFieldIds is { Length: > 0 }
            && Enum.TryParse(generateFieldIds, out GenerateFieldIds fieldIdOption))
        {
            result.GenerateFieldIds = fieldIdOption;
        }

        if (globalOptions.TryGetValue("build_property.orleansgeneratecompatibilityinvokers", out var generateCompatInvokersValue)
            && bool.TryParse(generateCompatInvokersValue, out var genCompatInvokers))
        {
            result.GenerateCompatibilityInvokers = genCompatInvokers;
        }

        return result;
    }

    private struct GeneratorOptions : IEquatable<GeneratorOptions>
    {
        public GenerateFieldIds GenerateFieldIds { get; set; }
        public bool GenerateCompatibilityInvokers { get; set; }
        public bool AttachDebugger { get; set; }

        public readonly bool Equals(GeneratorOptions other)
            => GenerateFieldIds == other.GenerateFieldIds
                && GenerateCompatibilityInvokers == other.GenerateCompatibilityInvokers
                && AttachDebugger == other.AttachDebugger;

        public override readonly bool Equals(object obj) => obj is GeneratorOptions other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                var hash = (int)GenerateFieldIds;
                hash = hash * 31 + (GenerateCompatibilityInvokers ? 1 : 0);
                hash = hash * 31 + (AttachDebugger ? 1 : 0);
                return hash;
            }
        }
    }

    private readonly struct GeneratedSourceEntry(string hintName, string source) : IEquatable<GeneratedSourceEntry>
    {
        public string HintName { get; } = hintName;
        public string Source { get; } = source;
        public SourceText SourceText => SourceText.From(Source ?? string.Empty, Encoding.UTF8);

        public bool Equals(GeneratedSourceEntry other)
            => string.Equals(HintName, other.HintName, StringComparison.Ordinal)
                && string.Equals(Source, other.Source, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is GeneratedSourceEntry other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(HintName ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Source ?? string.Empty);
                return hash;
            }
        }
    }

    private static ReferenceAssemblyModel CreateEmptyReferenceAssemblyModel(string assemblyName)
        => new(
            assemblyName,
            EquatableArray<string>.Empty,
            EquatableArray<WellKnownTypeIdModel>.Empty,
            EquatableArray<TypeAliasModel>.Empty,
            EquatableArray<CompoundTypeAliasModel>.Empty,
            EquatableArray<SerializableTypeModel>.Empty,
            EquatableArray<ProxyInterfaceModel>.Empty,
            EquatableArray<RegisteredCodecModel>.Empty,
            EquatableArray<InterfaceImplementationModel>.Empty);

    private readonly struct SourceOutputResult(GeneratedSourceEntry? sourceEntry, Diagnostic? diagnostic) : IEquatable<SourceOutputResult>
    {
        public GeneratedSourceEntry? SourceEntry { get; } = sourceEntry;
        public Diagnostic? Diagnostic { get; } = diagnostic;

        public static SourceOutputResult FromSource(GeneratedSourceEntry sourceEntry) => new(sourceEntry, null);
        public static SourceOutputResult FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);

        public bool Equals(SourceOutputResult other)
            => Nullable.Equals(SourceEntry, other.SourceEntry)
                && AreDiagnosticsEqual(Diagnostic, other.Diagnostic);

        public override bool Equals(object? obj) => obj is SourceOutputResult other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = SourceEntry.GetHashCode();
                hash = hash * 31 + GetDiagnosticHashCode(Diagnostic);
                return hash;
            }
        }
    }

    private readonly struct ReferenceAssemblyDataResult(ReferenceAssemblyModel model, ImmutableArray<Diagnostic> diagnostics) : IEquatable<ReferenceAssemblyDataResult>
    {
        public ReferenceAssemblyModel Model { get; } = model;
        public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics.IsDefault ? [] : diagnostics;

        public static ReferenceAssemblyDataResult FromModelAndDiagnostics(ReferenceAssemblyModel model, ImmutableArray<Diagnostic> diagnostics)
            => new(model, diagnostics);

        public bool Equals(ReferenceAssemblyDataResult other)
            => EqualityComparer<ReferenceAssemblyModel>.Default.Equals(Model, other.Model)
                && AreDiagnosticSequencesEqual(Diagnostics, other.Diagnostics);

        public override bool Equals(object? obj) => obj is ReferenceAssemblyDataResult other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Model?.GetHashCode() ?? 0;
                hash = hash * 31 + GetDiagnosticSequenceHashCode(Diagnostics);
                return hash;
            }
        }
    }

    private readonly struct SerializableTypeResult(
        SerializableTypeModel? model,
        Diagnostic? diagnostic,
        TypeMetadataIdentity metadataIdentity,
        SourceLocationModel sourceLocation,
        string typeSyntax) : IEquatable<SerializableTypeResult>
    {
        public SerializableTypeModel? Model { get; } = model;
        public Diagnostic? Diagnostic { get; } = diagnostic;
        public TypeMetadataIdentity MetadataIdentity { get; } = metadataIdentity;
        public SourceLocationModel SourceLocation { get; } = sourceLocation;
        public string TypeSyntax { get; } = typeSyntax;

        public static SerializableTypeResult FromModel(SerializableTypeModel model)
            => new(
                model,
                diagnostic: null,
                model?.MetadataIdentity ?? TypeMetadataIdentity.Empty,
                model?.SourceLocation ?? default,
                model?.TypeSyntax.SyntaxString ?? string.Empty);

        public static SerializableTypeResult FromDiagnostic(
            Diagnostic diagnostic,
            TypeMetadataIdentity metadataIdentity,
            SourceLocationModel sourceLocation,
            string typeSyntax)
            => new(model: null, diagnostic, metadataIdentity, sourceLocation, typeSyntax ?? string.Empty);

        public bool Equals(SerializableTypeResult other)
            => Nullable.Equals(Model, other.Model)
                && AreDiagnosticsEqual(Diagnostic, other.Diagnostic)
                && MetadataIdentity.Equals(other.MetadataIdentity)
                && SourceLocation.Equals(other.SourceLocation)
                && string.Equals(TypeSyntax, other.TypeSyntax, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SerializableTypeResult other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Model?.GetHashCode() ?? 0;
                hash = hash * 31 + GetDiagnosticHashCode(Diagnostic);
                hash = hash * 31 + MetadataIdentity.GetHashCode();
                hash = hash * 31 + SourceLocation.GetHashCode();
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(TypeSyntax ?? string.Empty);
                return hash;
            }
        }
    }

    private readonly struct ProxyOutputPreparationResult(
        ImmutableArray<ProxyOutputModel> proxyOutputModels,
        ImmutableArray<SourceOutputResult> sourceOutputs,
        Diagnostic? diagnostic) : IEquatable<ProxyOutputPreparationResult>
    {
        public ImmutableArray<ProxyOutputModel> ProxyOutputModels { get; } = proxyOutputModels;
        public ImmutableArray<SourceOutputResult> SourceOutputs { get; } = sourceOutputs;
        public Diagnostic? Diagnostic { get; } = diagnostic;

        public static ProxyOutputPreparationResult FromModelsAndSources(
            ImmutableArray<ProxyOutputModel> proxyOutputModels,
            ImmutableArray<SourceOutputResult> sourceOutputs)
            => new(proxyOutputModels, sourceOutputs, diagnostic: null);

        public static ProxyOutputPreparationResult FromDiagnostic(Diagnostic diagnostic)
            => new([], [], diagnostic);

        public bool Equals(ProxyOutputPreparationResult other)
            => StructuralEquality.SequenceEqual(ProxyOutputModels, other.ProxyOutputModels)
                && StructuralEquality.SequenceEqual(SourceOutputs, other.SourceOutputs)
                && AreDiagnosticsEqual(Diagnostic, other.Diagnostic);

        public override bool Equals(object? obj) => obj is ProxyOutputPreparationResult other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StructuralEquality.GetSequenceHashCode(ProxyOutputModels);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(SourceOutputs);
                hash = hash * 31 + GetDiagnosticHashCode(Diagnostic);
                return hash;
            }
        }
    }

    private static bool AreDiagnosticSequencesEqual(ImmutableArray<Diagnostic> left, ImmutableArray<Diagnostic> right)
    {
        if (left.IsDefaultOrEmpty)
        {
            return right.IsDefaultOrEmpty;
        }

        if (right.IsDefaultOrEmpty || left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!AreDiagnosticsEqual(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetDiagnosticSequenceHashCode(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return 0;
        }

        unchecked
        {
            var hash = 0;
            foreach (var diagnostic in diagnostics)
            {
                hash = hash * 31 + GetDiagnosticHashCode(diagnostic);
            }

            return hash;
        }
    }

    private static bool AreDiagnosticsEqual(Diagnostic? left, Diagnostic? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && left.Severity == right.Severity
            && left.WarningLevel == right.WarningLevel
            && string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static int GetDiagnosticHashCode(Diagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return 0;
        }

        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(diagnostic.Id ?? string.Empty);
            hash = hash * 31 + (int)diagnostic.Severity;
            hash = hash * 31 + diagnostic.WarningLevel;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(diagnostic.ToString() ?? string.Empty);
            return hash;
        }
    }
}
