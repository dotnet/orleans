using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Model.Incremental;
using Orleans.CodeGenerator.SyntaxGeneration;

#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#nullable disable
namespace Orleans.CodeGenerator
{
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

            var serializableTypes = serializableTypeResults
                .Where(static result => result.Model is not null)
                .Select(static (result, _) => result.Model!);

            var collectedTypes = serializableTypes
                .Collect()
                .WithComparer(ImmutableArrayComparer<SerializableTypeModel>.Instance)
                .WithTrackingName(CollectedSerializableTypesTrackingName);

            context.RegisterSourceOutput(serializableTypeResults, static (productionContext, result) =>
            {
                if (result.Diagnostic is { } diagnostic)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });

            // Attribute-driven discovery of [GenerateMethodSerializers] interfaces, plus a
            // constrained syntax provider for interfaces which inherit the attribute from a base interface.
            var directProxyInterfaces = context.SyntaxProvider
                .ForAttributeWithMetadataName<ProxyInterfaceModel>(
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
                .WithComparer(ImmutableArrayComparer<ProxyInterfaceModel>.Instance)
                .WithTrackingName(InheritedProxyInterfacesTrackingName);

            var collectedProxies = directProxyInterfaces
                .Collect()
                .WithComparer(ImmutableArrayComparer<ProxyInterfaceModel>.Instance)
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
            var refAssemblyData = compilationProvider
                .Combine(generatorOptions)
                .Select(static (input, ct) => ModelExtractor.ExtractReferenceAssemblyData(
                    input.Left,
                    CreateCodeGeneratorOptions(input.Right),
                    ct))
                .WithTrackingName(ReferenceAssemblyDataTrackingName);

            // Combine source/reference models before metadata generation.
            var metadataAggregate = collectedTypes
                .Combine(collectedProxies)
                .Combine(refAssemblyData)
                .Select(static (input, ct) => ModelExtractor.CreateMetadataAggregate(
                    input.Right.AssemblyName,
                    input.Left.Left,
                    input.Left.Right,
                    input.Right))
                .WithTrackingName(MetadataAggregateTrackingName);

            var serializerOutputs = serializableTypeResults
                .Where(static result => result.SourceOutput.HasValue)
                .Select(static (result, _) => result.SourceOutput.GetValueOrDefault())
                .Collect()
                .WithComparer(ImmutableArrayComparer<SourceOutputResult>.Instance)
                .Select(static (input, _) => DeduplicateSourceOutputs(input))
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

                var model = ModelExtractor.ExtractSerializableTypeModel(typeDescription, ModelExtractor.GetSourceLocation(symbol));
                var sourceOutput = CreateSerializableSourceOutput(compilation, codeGeneratorOptions, libraryTypes, typeDescription);
                return SerializableTypeResult.FromModelAndSource(model, sourceOutput);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                return SerializableTypeResult.FromDiagnostic(analysisException.Diagnostic);
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
                    return ImmutableArray<SourceOutputResult>.Empty;
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
                    return ImmutableArray<SourceOutputResult>.Empty;
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
                        defaultCopiers));
                }

                return DeduplicateSourceOutputs(sourceEntries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                return ImmutableArray.Create(SourceOutputResult.FromDiagnostic(analysisException.Diagnostic));
            }
        }

        private static SourceOutputResult CreateSerializableSourceOutput(
            Compilation compilation,
            CodeGeneratorOptions options,
            LibraryTypes libraryTypes,
            ISerializableTypeDescription typeDescription)
        {
            var generatorServices = new GeneratorServices(compilation, options, libraryTypes);
            return CreateSerializableSourceOutput(
                compilation.AssemblyName ?? "assembly",
                typeDescription,
                new SerializerGenerator(generatorServices),
                new CopierGenerator(generatorServices),
                new ActivatorGenerator(generatorServices),
                new Dictionary<ISerializableTypeDescription, TypeSyntax>());
        }

        private static SourceOutputResult CreateSerializableSourceOutput(
            string assemblyName,
            ISerializableTypeDescription typeDescription,
            SerializerGenerator serializerGenerator,
            CopierGenerator copierGenerator,
            ActivatorGenerator activatorGenerator,
            Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers)
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
                    typeDescription.TypeSyntax.ToString(),
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
                PopulateProxyInterfaces(proxyContext, resolver, ImmutableArray.Create(model), cancellationToken);

                var assemblyName = compilation.AssemblyName ?? "assembly";
                var interfaceDescription = GetProxyInterfaceDescription(proxyContext, resolver, model, cancellationToken);
                var proxyGenerator = new ProxyGenerator(generatorServices, new CopierGenerator(generatorServices));
                var (proxyClass, _) = proxyGenerator.Generate(interfaceDescription);
                var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
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

                var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
                AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, proxyClass);

                var serializerGenerator = new SerializerGenerator(generatorServices);
                var copierGenerator = new CopierGenerator(generatorServices);
                var activatorGenerator = new ActivatorGenerator(generatorServices);

                foreach (var invokable in generatedInvokables)
                {
                    if (!ownedInvokableMetadataNames.Contains(invokable.MetadataName)
                        && !(emitDeclaredMethodsFallback
                            && SymbolEqualityComparer.Default.Equals(invokable.MethodDescription.ContainingInterface, interfaceDescription.InterfaceType)))
                    {
                        continue;
                    }

                    AddMember(namespacedMembers, invokable.GeneratedNamespace, invokable.ClassDeclarationSyntax);
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
                var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                var ownedInvokableMetadataNames = new HashSet<string>(
                    proxyOutputModel.OwnedInvokableMetadataNames,
                    StringComparer.Ordinal);
                var emitDeclaredMethodsFallback = proxyOutputModel.UseDeclaredInvokableFallback;
                var generatedInvokables = GetGeneratedInvokables(proxyContext, interfaceDescription).ToImmutableArray();
                var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
                AddMember(namespacedMembers, interfaceDescription.GeneratedNamespace, proxyClass);

                var serializerGenerator = new SerializerGenerator(generatorServices);
                var copierGenerator = new CopierGenerator(generatorServices);
                var activatorGenerator = new ActivatorGenerator(generatorServices);
                var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();

                foreach (var invokable in generatedInvokables)
                {
                    if (!ownedInvokableMetadataNames.Contains(invokable.MetadataName)
                        && !(emitDeclaredMethodsFallback
                            && SymbolEqualityComparer.Default.Equals(invokable.MethodDescription.ContainingInterface, interfaceDescription.InterfaceType)))
                    {
                        continue;
                    }

                    AddMember(namespacedMembers, invokable.GeneratedNamespace, invokable.ClassDeclarationSyntax);
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
                        ImmutableArray<ProxyOutputModel>.Empty,
                        ImmutableArray<SourceOutputResult>.Empty);
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
                return ImmutableArray<SourceOutputResult>.Empty;
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
                return ImmutableArray<ProxyOutputModel>.Empty;
            }

            var assemblyName = compilation.AssemblyName ?? "assembly";
            var proxyEntries = proxyContext.MetadataModel.InvokableInterfaces.Values
                .Where(desc => SymbolEqualityComparer.Default.Equals(desc.InterfaceType.ContainingAssembly, compilation.Assembly))
                .OrderBy(static desc => desc.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .Select(desc => (HintName: CreateProxyHintName(assemblyName, desc.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), Description: desc))
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

            return models
                .Distinct()
                .OrderBy(static model => model.InterfaceType.SyntaxString, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.MetadataName, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
                .ThenBy(static model => model.GeneratedNamespace, StringComparer.Ordinal)
                .ThenBy(static model => model.Name, StringComparer.Ordinal)
                .Select(model =>
                {
                    var interfaceDescription = GetProxyInterfaceDescription(proxyContext, resolver, model, cancellationToken);
                    var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
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

                    return new ProxyOutputModel(
                        model,
                        ownedInvokableMetadataNames,
                        useDeclaredInvokableFallback);
                })
                .ToImmutableArray();
        }

        private static SourceOutputResult CreateMetadataSourceOutput(
            MetadataAggregateModel metadataModel,
            GeneratorOptions options)
        {
            try
            {
                AttachDebuggerIfRequested(options);
                var metadataGenerator = new MetadataGenerator(metadataModel, metadataModel.AssemblyName);
                var metadataClass = metadataGenerator.GenerateMetadata();
                var metadataNamespace = $"{GeneratedCodeUtilities.CodeGeneratorName}.{SyntaxGeneration.Identifier.SanitizeIdentifierName(metadataModel.AssemblyName ?? "Assembly")}";
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

            if (assemblyAttributes.Count > 0)
            {
                assemblyAttributes[0] = assemblyAttributes[0]
                    .WithLeadingTrivia(
                        SyntaxFactory.TriviaList(
                            SyntaxFactory.Trivia(
                                SyntaxFactory.PragmaWarningDirectiveTrivia(
                                    SyntaxFactory.Token(SyntaxKind.DisableKeyword),
                                    SyntaxFactory.SeparatedList<ExpressionSyntax>(
                                        new[]
                                        {
                                            CreatePragmaWarning("CS1591"),
                                            CreatePragmaWarning("RS0016"),
                                            CreatePragmaWarning("RS0041"),
                                        }),
                                    isActive: true))));
            }

            return SyntaxFactory.List(assemblyAttributes);

            static ExpressionSyntax CreatePragmaWarning(string warningCode)
            {
                var syntaxToken = SyntaxFactory.Literal(
                    SyntaxFactory.TriviaList(),
                    warningCode,
                    warningCode,
                    SyntaxFactory.TriviaList());

                return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, syntaxToken);
            }
        }

        private static ISerializableTypeDescription CreateSerializableTypeDescription(IGeneratorServices services, INamedTypeSymbol symbol)
            => CreateSerializableTypeDescription(services.Compilation, services.LibraryTypes, services.Options, symbol);

        private static ISerializableTypeDescription CreateSerializableTypeDescription(Compilation compilation, LibraryTypes libraryTypes, CodeGeneratorOptions options, INamedTypeSymbol symbol)
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
                throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason));
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
                return ImmutableArray<IParameterSymbol>.Empty;
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

            return ImmutableArray<IParameterSymbol>.Empty;
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

        private sealed class TypeSymbolResolver
        {
            private readonly Compilation _compilation;
            private FallbackIndex _fallbackIndex;

            public TypeSymbolResolver(Compilation compilation)
            {
                _compilation = compilation;
            }

            public bool TryResolveSerializableType(
                SerializableTypeModel model,
                CancellationToken cancellationToken,
                out INamedTypeSymbol symbol)
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
                out INamedTypeSymbol symbol)
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
                out INamedTypeSymbol symbol)
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
                out IAssemblySymbol assembly)
            {
                if (IsMatchingAssembly(_compilation.Assembly, metadataIdentity))
                {
                    assembly = _compilation.Assembly;
                    return true;
                }

                IAssemblySymbol assemblyByName = null;
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
                out INamedTypeSymbol symbol)
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

            private bool TryResolveMetadataName(string metadataName, out INamedTypeSymbol symbol)
            {
                symbol = _compilation.GetTypeByMetadataName(metadataName);
                if (symbol is null && TryGetSpecialType(metadataName, out var specialType))
                {
                    symbol = _compilation.GetSpecialType(specialType);
                }

                return symbol is not null;
            }

            private static bool TryGetMetadataName(string typeSyntax, bool allowGenericSyntax, out string metadataName)
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

            private sealed class FallbackIndex
            {
                public FallbackIndex(Dictionary<string, INamedTypeSymbol> typesByKey, List<INamedTypeSymbol> allTypes)
                {
                    TypesByKey = typesByKey;
                    AllTypes = allTypes;
                }

                public Dictionary<string, INamedTypeSymbol> TypesByKey { get; }
                public List<INamedTypeSymbol> AllTypes { get; }
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

        private static ImmutableArray<SourceOutputResult> DeduplicateSourceOutputs(
            ImmutableArray<SourceOutputResult>.Builder sourceEntries)
            => DeduplicateSourceOutputs(sourceEntries.ToImmutable());

        private static ImmutableArray<SourceOutputResult> DeduplicateSourceOutputs(
            ImmutableArray<SourceOutputResult> sourceEntries)
        {
            var emittedHintNames = new HashSet<string>(StringComparer.Ordinal);
            var result = ImmutableArray.CreateBuilder<SourceOutputResult>(sourceEntries.Length);
            foreach (var sourceEntry in sourceEntries
                .OrderBy(static entry => entry.SourceEntry?.HintName ?? string.Empty, StringComparer.Ordinal))
            {
                if (sourceEntry.SourceEntry is { } entry
                    && !emittedHintNames.Add(entry.HintName))
                {
                    continue;
                }

                result.Add(sourceEntry);
            }

            return result.MoveToImmutable();
        }

        private static GeneratedSourceEntry CreateSerializableSourceEntry(
            string assemblyName,
            string typeName,
            ClassDeclarationSyntax serializer,
            ClassDeclarationSyntax copier,
            ClassDeclarationSyntax activator,
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
                CreateSerializableHintName(assemblyName, typeName),
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

        private static SourceText CreateSourceText(CompilationUnitSyntax unit)
            => SourceText.From(CreateSourceString(unit), Encoding.UTF8);

        private static string CreateSourceString(CompilationUnitSyntax unit)
        {
            return unit.NormalizeWhitespace().ToFullString();
        }

        private static CompilationUnitSyntax CreateCompilationUnit(
            Dictionary<string, List<MemberDeclarationSyntax>> namespacedMembers,
            SyntaxList<AttributeListSyntax> attributeLists = default)
        {
            var unit = SyntaxFactory.CompilationUnit().WithAttributeLists(attributeLists);
            var usingDirectives = SyntaxFactory.List(new[]
            {
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::Orleans.Serialization.Codecs")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")),
            });
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

        private static string CreateSerializableHintName(string assemblyName, string typeName)
            => $"{assemblyName}.orleans.ser.{SanitizeHintComponent(typeName)}.g.cs";

        private static string CreateProxyHintName(string assemblyName, string interfaceName)
            => $"{assemblyName}.orleans.proxy.{SanitizeHintComponent(interfaceName)}.g.cs";

        private static string CreateMetadataHintName(string assemblyName)
            => $"{assemblyName}.orleans.metadata.g.cs";

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
                .Where(generatedInvokable => generatedInvokable is not null
                    && proxyContext.Compilation.GetTypeByMetadataName(generatedInvokable.MetadataName) is null)
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

            public bool Equals(GeneratorOptions other)
                => GenerateFieldIds == other.GenerateFieldIds
                    && GenerateCompatibilityInvokers == other.GenerateCompatibilityInvokers
                    && AttachDebugger == other.AttachDebugger;

            public override bool Equals(object obj) => obj is GeneratorOptions other && Equals(other);

            public override int GetHashCode()
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
            public SourceText SourceText => Microsoft.CodeAnalysis.Text.SourceText.From(Source ?? string.Empty, Encoding.UTF8);

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

        private readonly struct SourceOutputResult(GeneratedSourceEntry? sourceEntry, Diagnostic diagnostic) : IEquatable<SourceOutputResult>
        {
            public GeneratedSourceEntry? SourceEntry { get; } = sourceEntry;
            public Diagnostic Diagnostic { get; } = diagnostic;

            public static SourceOutputResult FromSource(GeneratedSourceEntry sourceEntry) => new(sourceEntry, null);
            public static SourceOutputResult FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);

            public bool Equals(SourceOutputResult other)
                => Nullable.Equals(SourceEntry, other.SourceEntry)
                    && AreDiagnosticsEqual(Diagnostic, other.Diagnostic);

            public override bool Equals(object obj) => obj is SourceOutputResult other && Equals(other);

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

        private readonly struct SerializableTypeResult(SerializableTypeModel model, SourceOutputResult? sourceOutput, Diagnostic diagnostic) : IEquatable<SerializableTypeResult>
        {
            public SerializableTypeModel Model { get; } = model;
            public SourceOutputResult? SourceOutput { get; } = sourceOutput;
            public Diagnostic Diagnostic { get; } = diagnostic;

            public static SerializableTypeResult FromModelAndSource(SerializableTypeModel model, SourceOutputResult sourceOutput)
                => new(model, sourceOutput, diagnostic: null);

            public static SerializableTypeResult FromDiagnostic(Diagnostic diagnostic)
                => new(model: null, sourceOutput: null, diagnostic);

            public bool Equals(SerializableTypeResult other)
                => EqualityComparer<SerializableTypeModel>.Default.Equals(Model, other.Model)
                    && Nullable.Equals(SourceOutput, other.SourceOutput)
                    && AreDiagnosticsEqual(Diagnostic, other.Diagnostic);

            public override bool Equals(object obj) => obj is SerializableTypeResult other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = Model?.GetHashCode() ?? 0;
                    hash = hash * 31 + SourceOutput.GetHashCode();
                    hash = hash * 31 + GetDiagnosticHashCode(Diagnostic);
                    return hash;
                }
            }
        }

        private readonly struct ProxyOutputPreparationResult(
            ImmutableArray<ProxyOutputModel> proxyOutputModels,
            ImmutableArray<SourceOutputResult> sourceOutputs,
            Diagnostic diagnostic) : IEquatable<ProxyOutputPreparationResult>
        {
            public ImmutableArray<ProxyOutputModel> ProxyOutputModels { get; } = proxyOutputModels;
            public ImmutableArray<SourceOutputResult> SourceOutputs { get; } = sourceOutputs;
            public Diagnostic Diagnostic { get; } = diagnostic;

            public static ProxyOutputPreparationResult FromModelsAndSources(
                ImmutableArray<ProxyOutputModel> proxyOutputModels,
                ImmutableArray<SourceOutputResult> sourceOutputs)
                => new(proxyOutputModels, sourceOutputs, diagnostic: null);

            public static ProxyOutputPreparationResult FromDiagnostic(Diagnostic diagnostic)
                => new(ImmutableArray<ProxyOutputModel>.Empty, ImmutableArray<SourceOutputResult>.Empty, diagnostic);

            public bool Equals(ProxyOutputPreparationResult other)
                => ImmutableArrayValueComparer.Equals(ProxyOutputModels, other.ProxyOutputModels)
                    && ImmutableArrayValueComparer.Equals(SourceOutputs, other.SourceOutputs)
                    && AreDiagnosticsEqual(Diagnostic, other.Diagnostic);

            public override bool Equals(object obj) => obj is ProxyOutputPreparationResult other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = ImmutableArrayValueComparer.GetHashCode(ProxyOutputModels);
                    hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(SourceOutputs);
                    hash = hash * 31 + GetDiagnosticHashCode(Diagnostic);
                    return hash;
                }
            }
        }

        private static bool AreDiagnosticsEqual(Diagnostic left, Diagnostic right)
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

        private static int GetDiagnosticHashCode(Diagnostic diagnostic)
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
}
#pragma warning restore RS1035 // Do not use APIs banned for analyzers
