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
namespace Orleans.CodeGenerator
{
    [Generator]
    public sealed class OrleansSerializationSourceGenerator : IIncrementalGenerator
    {
        private static int _debuggerLaunchState;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var generatorOptions = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => ParseOptions(provider.GlobalOptions));
            var compilationProvider = context.CompilationProvider;

            // Incremental discovery of [GenerateSerializer] types
            var serializableTypeContexts = context.SyntaxProvider
                .ForAttributeWithMetadataName<GeneratorAttributeSyntaxContext>(
                    "Orleans.GenerateSerializerAttribute",
                    predicate: static (node, _) => node is TypeDeclarationSyntax or EnumDeclarationSyntax,
                    transform: static (ctx, _) => ctx);

            var serializableTypeResults = serializableTypeContexts
                .Combine(generatorOptions)
                .Select(static (input, ct) => ModelExtractor.ExtractFromAttributeContextWithDiagnostics(
                    input.Left,
                    CreateCodeGeneratorOptions(input.Right),
                    ct));

            var serializableTypes = serializableTypeResults
                .Where(static result => result.Model is not null)
                .Select(static (result, _) => result.Model!);

            var collectedTypes = serializableTypes.Collect();

            context.RegisterSourceOutput(serializableTypeResults, static (productionContext, result) =>
            {
                if (result.Diagnostic is { } diagnostic)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });

            // Attribute-driven discovery of [GenerateMethodSerializers] interfaces, plus a
            // compilation fallback for interfaces which inherit the attribute from a base interface.
            var directProxyInterfaces = context.SyntaxProvider
                .ForAttributeWithMetadataName<ProxyInterfaceModel>(
                    "Orleans.GenerateMethodSerializersAttribute",
                    predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                    transform: static (ctx, ct) => ModelExtractor.ExtractProxyInterfaceFromAttributeContext(ctx, ct))
                .Where(static model => model is not null)
                .Select(static (model, _) => model!);

            var inheritedProxyInterfaces = compilationProvider.Select(
                static (compilation, ct) => ModelExtractor.ExtractInheritedProxyInterfaceModels(compilation, ct));

            var collectedProxies = directProxyInterfaces
                .Collect()
                .Combine(inheritedProxyInterfaces)
                .Select(static (input, _) => ModelExtractor.MergeProxyInterfaces(input.Left, new EquatableArray<ProxyInterfaceModel>(input.Right)));
            var preparedProxyOutputs = collectedProxies
                .Combine(compilationProvider)
                .Combine(generatorOptions)
                .Select(static (input, ct) => CreateProxyOutputPreparation(input.Left.Right, input.Left.Left, input.Right, ct));

            context.RegisterSourceOutput(preparedProxyOutputs, static (productionContext, input) =>
            {
                if (input.Diagnostic is { } diagnostic)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });

            var proxyOutputModels = preparedProxyOutputs
                .SelectMany(static (result, _) => result.ProxyOutputModels.AsImmutableArray());

            // Extract reference assembly data (application parts, well-known type IDs, aliases)
            var refAssemblyData = compilationProvider
                .Combine(generatorOptions)
                .Select(static (input, ct) => ModelExtractor.ExtractReferenceAssemblyData(
                    input.Left,
                    CreateCodeGeneratorOptions(input.Right),
                    ct));

            // Combine with compilation for generation (still needs Compilation for now)
            var metadataAggregate = collectedTypes
                .Combine(collectedProxies)
                .Combine(refAssemblyData)
                .Select(static (input, ct) => ModelExtractor.CreateMetadataAggregate(
                    input.Right.AssemblyName,
                    input.Left.Left,
                    input.Left.Right,
                    input.Right));

            var serializerOutputs = serializableTypes
                .Combine(compilationProvider)
                .Combine(generatorOptions);

            context.RegisterSourceOutput(serializerOutputs, static (productionContext, input) =>
            {
                ExecuteSerializableOutput(productionContext, input.Left.Right, input.Left.Left, input.Right);
            });

            var referencedSerializerOutputs = refAssemblyData
                .Combine(compilationProvider)
                .Combine(generatorOptions);

            context.RegisterSourceOutput(referencedSerializerOutputs, static (productionContext, input) =>
            {
                ExecuteSerializableOutputs(
                    productionContext,
                    input.Left.Right,
                    GetReferencedSerializableTypes(input.Left.Right, input.Left.Left),
                    input.Right);
            });

            var proxyOutputs = proxyOutputModels
                .Combine(compilationProvider)
                .Combine(generatorOptions)
                .Select(static (input, ct) => CreateProxySourceOutput(input.Left.Right, input.Left.Left, input.Right, ct));

            context.RegisterSourceOutput(proxyOutputs, static (productionContext, input) =>
            {
                if (input.Diagnostic is { } diagnostic)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                    return;
                }

                if (input.SourceEntry is { } sourceEntry)
                {
                    productionContext.AddSource(sourceEntry.HintName, sourceEntry.SourceText);
                }
            });

            var metadataOutputs = metadataAggregate
                .Combine(compilationProvider)
                .Combine(generatorOptions);

            context.RegisterSourceOutput(metadataOutputs, static (productionContext, input) =>
            {
                ExecuteMetadataOutput(productionContext, input.Left.Left, input.Left.Right, input.Right);
            });

            context.RegisterSourceOutput(compilationProvider, static (productionContext, compilation) =>
            {
                var assemblyName = compilation.AssemblyName ?? "assembly";
                productionContext.AddSource($"{assemblyName}.orleans.g.cs", SourceText.From(string.Empty, Encoding.UTF8));
            });
        }

        private static void ExecuteSerializableOutput(
            SourceProductionContext context,
            Compilation compilation,
            SerializableTypeModel model,
            GeneratorOptions options)
            => ExecuteSerializableOutputs(context, compilation, ImmutableArray.Create(model), options);

        private static ImmutableArray<SerializableTypeModel> GetReferencedSerializableTypes(
            Compilation compilation,
            ReferenceAssemblyModel referenceData)
        {
            var resolver = new TypeSymbolResolver(compilation);
            var builder = ImmutableArray.CreateBuilder<SerializableTypeModel>();
            foreach (var model in referenceData.ReferencedSerializableTypes.AsImmutableArray())
            {
                if (!resolver.TryResolveSerializableType(model, out var symbol)
                    || SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
                {
                    continue;
                }

                builder.Add(model);
            }

            return builder.ToImmutable();
        }

        private static void ExecuteSerializableOutputs(
            SourceProductionContext context,
            Compilation compilation,
            ImmutableArray<SerializableTypeModel> models,
            GeneratorOptions options)
        {
            try
            {
                AttachDebuggerIfRequested(options);
                var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
                var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
                var resolver = new TypeSymbolResolver(compilation);
                var assemblyName = compilation.AssemblyName ?? "assembly";
                var processedModelTypes = new HashSet<string>(StringComparer.Ordinal);
                var sourceEntries = new List<GeneratedSourceEntry>();
                var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();

                var serializerGenerator = new SerializerGenerator(generatorServices);
                var copierGenerator = new CopierGenerator(generatorServices);
                var activatorGenerator = new ActivatorGenerator(generatorServices);

                foreach (var model in models
                    .Distinct()
                    .OrderBy(static model => model.TypeSyntax.SyntaxString, StringComparer.Ordinal)
                    .ThenBy(static model => model.GeneratedNamespace, StringComparer.Ordinal)
                    .ThenBy(static model => model.Name, StringComparer.Ordinal))
                {
                    var modelTypeKey = $"{model.GeneratedNamespace}|{model.Name}|{model.TypeSyntax.SyntaxString}";
                    if (!processedModelTypes.Add(modelTypeKey))
                    {
                        continue;
                    }

                    if (!resolver.TryResolveSerializableType(model, out var symbol))
                    {
                        continue;
                    }

                    var typeDescription = CreateSerializableTypeDescription(generatorServices, symbol);
                    if (typeDescription is null)
                    {
                        continue;
                    }

                    var serializer = serializerGenerator.Generate(typeDescription);
                    var copier = typeDescription.IsShallowCopyable && defaultCopiers.ContainsKey(typeDescription)
                        ? null
                        : copierGenerator.GenerateCopier(typeDescription, defaultCopiers);
                    var activatorClass = ActivatorGenerator.ShouldGenerateActivator(typeDescription)
                        ? activatorGenerator.GenerateActivator(typeDescription)
                        : null;

                    sourceEntries.Add(CreateSerializableSourceEntry(
                        assemblyName,
                        typeDescription.TypeSyntax.ToString(),
                        serializer,
                        copier,
                        activatorClass,
                        typeDescription.GeneratedNamespace));
                }

                EmitGeneratedSources(context, sourceEntries);
            }
            catch (Exception exception)
            {
                if (!HandleException(context, exception))
                {
                    throw;
                }
            }
        }

        private static ProxySourceOutputResult CreateProxySourceOutput(
            Compilation compilation,
            ProxyOutputModel proxyOutputModel,
            GeneratorOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                AttachDebuggerIfRequested(options);
                var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
                var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
                var codeGenerator = new CodeGenerator(compilation, codeGeneratorOptions);
                var model = proxyOutputModel.ProxyInterface;
                PopulateProxyInterfaces(codeGenerator, ImmutableArray.Create(model), cancellationToken);

                var assemblyName = compilation.AssemblyName ?? "assembly";
                var interfaceDescription = GetProxyInterfaceDescription(codeGenerator, model);
                var proxyGenerator = new ProxyGenerator(generatorServices, new CopierGenerator(generatorServices));
                var (proxyClass, _) = proxyGenerator.Generate(interfaceDescription);
                var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                var ownedInvokableMetadataNames = new HashSet<string>(
                    proxyOutputModel.OwnedInvokableMetadataNames.AsImmutableArray().Select(static value => value.Value),
                    StringComparer.Ordinal);
                var emitDeclaredMethodsFallback = proxyOutputModel.UseDeclaredInvokableFallback;
                var generatedInvokables = GetGeneratedInvokables(codeGenerator, interfaceDescription).ToImmutableArray();
                var generatedInvokableClassNames = new HashSet<string>(
                    generatedInvokables.Select(static invokable => invokable.ClassDeclarationSyntax.Identifier.ValueText),
                    StringComparer.Ordinal);
                var additionalInvokableClasses = codeGenerator.GetEmittedMembers()
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

                    var copier = invokable.IsShallowCopyable && codeGenerator.MetadataModel.DefaultCopiers.ContainsKey(invokable)
                        ? null
                        : copierGenerator.GenerateCopier(invokable, codeGenerator.MetadataModel.DefaultCopiers);
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

                return ProxySourceOutputResult.FromSource(
                    new GeneratedSourceEntry(targetHintName, CreateSourceText(CreateCompilationUnit(namespacedMembers))));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                return ProxySourceOutputResult.FromDiagnostic(analysisException.Diagnostic);
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
                return ProxyOutputPreparationResult.FromModels(
                    new EquatableArray<ProxyOutputModel>(CreateProxyOutputModels(compilation, models, options, cancellationToken)));
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

        private static ImmutableArray<ProxyOutputModel> CreateProxyOutputModels(
            Compilation compilation,
            ImmutableArray<ProxyInterfaceModel> models,
            GeneratorOptions options,
            CancellationToken cancellationToken)
        {
            if (models.IsDefaultOrEmpty)
            {
                return ImmutableArray<ProxyOutputModel>.Empty;
            }

            var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
            var codeGenerator = new CodeGenerator(compilation, codeGeneratorOptions);
            PopulateProxyInterfaces(codeGenerator, models, cancellationToken);

            var assemblyName = compilation.AssemblyName ?? "assembly";
            var proxyEntries = codeGenerator.MetadataModel.InvokableInterfaces.Values
                .Where(desc => SymbolEqualityComparer.Default.Equals(desc.InterfaceType.ContainingAssembly, compilation.Assembly))
                .OrderBy(static desc => desc.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .Select(desc => (HintName: CreateProxyHintName(assemblyName, desc.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), Description: desc))
                .ToImmutableArray();

            var invokableOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in proxyEntries.OrderBy(static entry => entry.HintName, StringComparer.Ordinal))
            {
                foreach (var invokable in GetGeneratedInvokables(codeGenerator, entry.Description))
                {
                    if (!invokableOwners.ContainsKey(invokable.MetadataName))
                    {
                        invokableOwners.Add(invokable.MetadataName, entry.HintName);
                    }
                }
            }

            return models
                .Distinct()
                .OrderBy(static model => model.InterfaceType.SyntaxString, StringComparer.Ordinal)
                .ThenBy(static model => model.GeneratedNamespace, StringComparer.Ordinal)
                .ThenBy(static model => model.Name, StringComparer.Ordinal)
                .Select(model =>
                {
                    var interfaceDescription = GetProxyInterfaceDescription(codeGenerator, model);
                    var targetHintName = CreateProxyHintName(assemblyName, interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    var generatedInvokables = GetGeneratedInvokables(codeGenerator, interfaceDescription)
                        .ToImmutableArray();
                    var ownedInvokableMetadataNames = generatedInvokables
                        .Select(invokable => invokable.MetadataName)
                        .Where(metadataName => invokableOwners.TryGetValue(metadataName, out var ownerHintName)
                            && string.Equals(ownerHintName, targetHintName, StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static value => value, StringComparer.Ordinal)
                        .Select(static value => new EquatableString(value))
                        .ToImmutableArray();
                    var useDeclaredInvokableFallback =
                        generatedInvokables.Length == 0
                            ? model.Methods.AsImmutableArray().Any(method => method.ContainingInterfaceType.Equals(model.InterfaceType))
                            : ownedInvokableMetadataNames.Length == 0
                                && !generatedInvokables.Any(invokable => invokableOwners.ContainsKey(invokable.MetadataName));

                    return new ProxyOutputModel(
                        model,
                        new EquatableArray<EquatableString>(ownedInvokableMetadataNames),
                        useDeclaredInvokableFallback);
                })
                .ToImmutableArray();
        }

        private static void ExecuteMetadataOutput(
            SourceProductionContext context,
            MetadataAggregateModel metadataModel,
            Compilation compilation,
            GeneratorOptions options)
        {
            try
            {
                AttachDebuggerIfRequested(options);
                var codeGeneratorOptions = CreateCodeGeneratorOptions(options);
                var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
                var metadataGenerator = new MetadataGenerator(generatorServices, metadataModel, metadataModel.AssemblyName);
                var metadataClass = metadataGenerator.GenerateMetadata();
                var metadataNamespace = $"{CodeGenerator.CodeGeneratorName}.{SyntaxGeneration.Identifier.SanitizeIdentifierName(metadataModel.AssemblyName ?? compilation.AssemblyName ?? "Assembly")}";
                var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
                AddMember(namespacedMembers, metadataNamespace, metadataClass);
                var assemblyAttributes = CreateAssemblyAttributes(generatorServices.LibraryTypes, compilation, metadataNamespace, metadataClass.Identifier.Text);

                var source = CreateSourceText(CreateCompilationUnit(namespacedMembers, assemblyAttributes));
                var assemblyName = compilation.AssemblyName ?? "assembly";
                context.AddSource($"{assemblyName}.orleans.metadata.g.cs", source);
            }
            catch (Exception exception)
            {
                if (!HandleException(context, exception))
                {
                    throw;
                }
            }
        }

        private static void PopulateProxyInterfaces(
            CodeGenerator codeGenerator,
            ImmutableArray<ProxyInterfaceModel> models,
            CancellationToken cancellationToken)
        {
            var resolver = new TypeSymbolResolver(codeGenerator.Compilation);
            var processed = new HashSet<string>(StringComparer.Ordinal);
            var resolvedInterfaces = new List<(ProxyInterfaceModel Model, INamedTypeSymbol Symbol, int SourceOrderGroup, string FilePath, int Position)>();
            foreach (var model in models)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modelKey = $"{model.InterfaceType.SyntaxString}|{model.GeneratedNamespace}|{model.Name}";
                if (!processed.Add(modelKey))
                {
                    continue;
                }

                if (!resolver.TryResolveProxyInterface(model, out var interfaceType))
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
                codeGenerator.VisitInterface(entry.Symbol.OriginalDefinition);
            }
        }

        private static ProxyInterfaceDescription GetProxyInterfaceDescription(CodeGenerator codeGenerator, ProxyInterfaceModel model)
        {
            var resolver = new TypeSymbolResolver(codeGenerator.Compilation);
            if (!resolver.TryResolveProxyInterface(model, out var interfaceType)
                || !codeGenerator.TryGetInvokableInterfaceDescription(interfaceType.OriginalDefinition, out var description))
            {
                throw new InvalidOperationException($"Unable to resolve proxy interface '{model.InterfaceType.SyntaxString}'.");
            }

            return description;
        }

        private static SyntaxList<AttributeListSyntax> CreateAssemblyAttributes(
            LibraryTypes libraryTypes,
            Compilation compilation,
            string metadataNamespace,
            string metadataClassName)
        {
            var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateSyntax(
                libraryTypes,
                GetApplicationParts(compilation, libraryTypes));
            var metadataAttribute = SyntaxFactory.AttributeList()
                .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
                .WithAttributes(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Attribute(libraryTypes.TypeManifestProviderAttribute.ToNameSyntax())
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

        private static IEnumerable<string> GetApplicationParts(Compilation compilation, LibraryTypes libraryTypes)
        {
            var result = new HashSet<string>(StringComparer.Ordinal)
            {
                compilation.Assembly.MetadataName,
            };

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                {
                    continue;
                }

                if (!assembly.GetAttributes(libraryTypes.ApplicationPartAttribute, out var attributes))
                {
                    continue;
                }

                result.Add(assembly.MetadataName);
                foreach (var attribute in attributes)
                {
                    if (attribute.ConstructorArguments.Length > 0
                        && attribute.ConstructorArguments[0].Value is string partName)
                    {
                        result.Add(partName);
                    }
                }
            }

            return result;
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

        private sealed class TypeSymbolResolver
        {
            private readonly Compilation _compilation;
            private readonly Dictionary<string, INamedTypeSymbol> _types = new(StringComparer.Ordinal);
            private readonly List<INamedTypeSymbol> _allTypes = new();

            public TypeSymbolResolver(Compilation compilation)
            {
                _compilation = compilation;
                AddAssembly(compilation.Assembly);

                foreach (var reference in compilation.References)
                {
                    if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                    {
                        AddAssembly(assembly);
                    }
                }
            }

            public bool TryResolveSerializableType(SerializableTypeModel model, out INamedTypeSymbol symbol)
            {
                if (TryResolve(model.TypeSyntax.SyntaxString, out symbol))
                {
                    return true;
                }

                symbol = _allTypes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.GetNamespaceAndNesting(), model.Namespace, StringComparison.Ordinal)
                    && candidate.GetAllTypeParameters().Count() == model.TypeParameters.Count);
                return symbol is not null;
            }

            public bool TryResolveProxyInterface(ProxyInterfaceModel model, out INamedTypeSymbol symbol)
            {
                if (TryResolve(model.InterfaceType.SyntaxString, out symbol))
                {
                    return symbol.TypeKind == TypeKind.Interface;
                }

                symbol = _allTypes.FirstOrDefault(candidate =>
                    candidate.TypeKind == TypeKind.Interface
                    && string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), model.InterfaceType.SyntaxString, StringComparison.Ordinal));
                return symbol is not null;
            }

            public bool TryResolve(string typeSyntax, out INamedTypeSymbol symbol)
            {
                if (string.IsNullOrWhiteSpace(typeSyntax))
                {
                    symbol = null;
                    return false;
                }

                if (_types.TryGetValue(NormalizeTypeKey(typeSyntax), out symbol))
                {
                    return true;
                }

                var metadataName = typeSyntax;
                if (metadataName.StartsWith("global::", StringComparison.Ordinal))
                {
                    metadataName = metadataName.Substring("global::".Length);
                }

                var genericStart = metadataName.IndexOf('<');
                if (genericStart >= 0)
                {
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

                symbol = _compilation.GetTypeByMetadataName(metadataName);
                if (symbol is null && TryGetSpecialType(metadataName, out var specialType))
                {
                    symbol = _compilation.GetSpecialType(specialType);
                }

                return symbol is not null;

                static bool TryGetSpecialType(string metadataName, out SpecialType specialType)
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
            }

            private void AddAssembly(IAssemblySymbol assembly)
            {
                foreach (var type in assembly.GetDeclaredTypes())
                {
                    AddType(type);
                }
            }

            private void AddType(INamedTypeSymbol type)
            {
                _allTypes.Add(type);
                AddKey(type.ToOpenTypeSyntax().ToString(), type);
                AddKey(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), type);
                AddKey(type.ToDisplayString(), type);
            }

            private void AddKey(string key, INamedTypeSymbol type)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                var normalizedKey = NormalizeTypeKey(key);
                if (!_types.ContainsKey(normalizedKey))
                {
                    _types[normalizedKey] = type;
                }
            }
        }

        private static string NormalizeTypeKey(string value)
            => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

        private static void EmitGeneratedSources(SourceProductionContext context, List<GeneratedSourceEntry> sourceEntries)
        {
            var emittedHintNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceEntry in sourceEntries.OrderBy(static entry => entry.HintName, StringComparer.Ordinal))
            {
                if (!emittedHintNames.Add(sourceEntry.HintName))
                {
                    continue;
                }

                context.AddSource(sourceEntry.HintName, sourceEntry.SourceText);
            }
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
                CreateSourceText(CreateCompilationUnit(namespacedMembers)));
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
        {
            var sourceString = unit.NormalizeWhitespace().ToFullString();
            return SourceText.From(sourceString, Encoding.UTF8);
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
            CodeGenerator legacyCodeGenerator,
            ProxyInterfaceDescription interfaceDescription)
        {
            return interfaceDescription.Methods
                .Select(static method => method.InvokableKey)
                .Distinct()
                .Select(key => legacyCodeGenerator.MetadataModel.GeneratedInvokables.TryGetValue(key, out var generatedInvokable) ? generatedInvokable : null)
                .Where(generatedInvokable => generatedInvokable is not null
                    && legacyCodeGenerator.Compilation.GetTypeByMetadataName(generatedInvokable.MetadataName) is null)
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

        private static bool HandleException(SourceProductionContext context, Exception exception)
        {
            if (exception is OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                context.ReportDiagnostic(analysisException.Diagnostic);
                return true;
            }

            context.ReportDiagnostic(UnhandledCodeGenerationExceptionDiagnostic.CreateDiagnostic(exception));
            Console.WriteLine(exception);
            Console.WriteLine(exception.StackTrace);
            return false;
        }

        private struct GeneratorOptions
        {
            public GenerateFieldIds GenerateFieldIds { get; set; }
            public bool GenerateCompatibilityInvokers { get; set; }
            public bool AttachDebugger { get; set; }
        }

        private readonly struct GeneratedSourceEntry(string hintName, SourceText sourceText)
        {
            public string HintName { get; } = hintName;
            public SourceText SourceText { get; } = sourceText;
        }

        private readonly struct ProxySourceOutputResult(GeneratedSourceEntry? sourceEntry, Diagnostic diagnostic)
        {
            public GeneratedSourceEntry? SourceEntry { get; } = sourceEntry;
            public Diagnostic Diagnostic { get; } = diagnostic;

            public static ProxySourceOutputResult FromSource(GeneratedSourceEntry sourceEntry) => new(sourceEntry, null);
            public static ProxySourceOutputResult FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);
        }

        private readonly struct ProxyOutputPreparationResult(EquatableArray<ProxyOutputModel> proxyOutputModels, Diagnostic diagnostic)
        {
            public EquatableArray<ProxyOutputModel> ProxyOutputModels { get; } = proxyOutputModels;
            public Diagnostic Diagnostic { get; } = diagnostic;

            public static ProxyOutputPreparationResult FromModels(EquatableArray<ProxyOutputModel> proxyOutputModels)
                => new(proxyOutputModels, diagnostic: null);

            public static ProxyOutputPreparationResult FromDiagnostic(Diagnostic diagnostic)
                => new(EquatableArray<ProxyOutputModel>.Empty, diagnostic);
        }

    }
}
#pragma warning restore RS1035 // Do not use APIs banned for analyzers
