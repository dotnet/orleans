using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Model;

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


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generatorOptions = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => SourceGeneratorOptionsParser.ParseOptions(provider.GlobalOptions))
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
            .Select(static (input, ct) => SerializableSourceOutputGenerator.CreateSerializableTypeResult(
                input.Left,
                input.Right,
                ct))
            .WithTrackingName(SerializableTypeResultsTrackingName);

        var collectedSerializableTypeResults = serializableTypeResults
            .Collect()
            .Select(static (input, _) => GeneratedSourceOutput.DeduplicateSerializableTypeResults(input))
            .WithComparer(ImmutableArrayComparer<SerializableTypeResult>.Instance);

        var collectedTypes = collectedSerializableTypeResults
            .Select(static (input, _) => GeneratedSourceOutput.GetSerializableTypeModels(input))
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
            .Select(static (input, ct) => ProxySourceOutputGenerator.CreateProxyOutputPreparation(input.Left.Right, input.Left.Left, input.Right, ct))
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
            .Select(static (input, ct) => ReferenceAssemblyDataProvider.CreateReferenceAssemblyDataResult(
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
            .Select(static (input, ct) => SerializableSourceOutputGenerator.CreateSerializableSourceOutputs(
                input.Left.Right,
                input.Left.Left,
                input.Right,
                ct))
            .WithComparer(ImmutableArrayComparer<SourceOutputResult>.Instance)
            .WithTrackingName(SerializerOutputsTrackingName);

        context.RegisterSourceOutput(serializerOutputs.SelectMany(static (input, _) => input), static (productionContext, input) =>
        {
            GeneratedSourceOutput.EmitSourceOutputResult(productionContext, input);
        });

        var referencedSerializerOutputs = refAssemblyData
            .Combine(compilationProvider)
            .Combine(generatorOptions)
            .Select(static (input, ct) => SerializableSourceOutputGenerator.CreateReferencedSerializableSourceOutputs(
                input.Left.Right,
                input.Left.Left,
                input.Right,
                ct))
            .WithComparer(ImmutableArrayComparer<SourceOutputResult>.Instance)
            .WithTrackingName(ReferencedSerializerOutputsTrackingName);

        context.RegisterSourceOutput(referencedSerializerOutputs.SelectMany(static (input, _) => input), static (productionContext, input) =>
        {
            GeneratedSourceOutput.EmitSourceOutputResult(productionContext, input);
        });

        var proxyOutputs = preparedProxyOutputs
            .SelectMany(static (result, _) => result.SourceOutputs)
            .WithTrackingName(ProxyOutputsTrackingName);

        context.RegisterSourceOutput(proxyOutputs, static (productionContext, input) =>
        {
            GeneratedSourceOutput.EmitSourceOutputResult(productionContext, input);
        });

        context.RegisterSourceOutput(assemblyNameProvider, static (productionContext, assemblyName) =>
        {
            productionContext.AddSource($"{assemblyName}.orleans.g.cs", SourceText.From(string.Empty, Encoding.UTF8));
        });

        var metadataOutputs = metadataAggregate
            .Combine(generatorOptions)
            .Select(static (input, _) => MetadataSourceOutputGenerator.CreateMetadataSourceOutput(input.Left, input.Right))
            .WithTrackingName(MetadataOutputsTrackingName);

        context.RegisterSourceOutput(metadataOutputs, static (productionContext, input) =>
        {
            GeneratedSourceOutput.EmitSourceOutputResult(productionContext, input);
        });
    }
}
