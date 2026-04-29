using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

/// <summary>
/// Extracts <see cref="Model.SerializableTypeModel"/> and other incremental pipeline models
/// from Roslyn symbols, producing value-type representations suitable for pipeline caching.
/// </summary>
internal static class ModelExtractor
{
    public static SerializableTypeModel ExtractSerializableTypeModel(
        ISerializableTypeDescription description,
        SourceLocationModel sourceLocation = default)
        => SerializableTypeModelExtractor.ExtractSerializableTypeModel(description, sourceLocation);

    public static ReferenceAssemblyModel ExtractReferenceAssemblyData(
        Compilation compilation,
        CodeGeneratorOptions options,
        CancellationToken cancellationToken)
        => ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(compilation, options, cancellationToken);

    internal static ReferenceAssemblyModel ExtractReferenceAssemblyData(
        Compilation compilation,
        CodeGeneratorOptions options,
        CancellationToken cancellationToken,
        out ImmutableArray<Diagnostic> diagnostics)
        => ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(compilation, options, cancellationToken, out diagnostics);

    public static MetadataAggregateModel CreateMetadataAggregate(
        string assemblyName,
        ImmutableArray<SerializableTypeModel> serializableTypes,
        ImmutableArray<ProxyInterfaceModel> proxyInterfaces,
        ReferenceAssemblyModel refData)
        => MetadataAggregateModelBuilder.CreateMetadataAggregate(assemblyName, serializableTypes, proxyInterfaces, refData);

    public static MetadataAggregateModel CreateMetadataAggregate(
        string assemblyName,
        ImmutableArray<SerializableTypeModel> serializableTypes,
        ImmutableArray<ProxyOutputModel> proxyOutputs,
        ReferenceAssemblyModel refData)
        => MetadataAggregateModelBuilder.CreateMetadataAggregate(assemblyName, serializableTypes, proxyOutputs, refData);

    internal static ImmutableArray<SerializableTypeModel> MergeSerializableTypes(
        ImmutableArray<SerializableTypeModel> source,
        ImmutableArray<SerializableTypeModel> referenced)
        => MetadataAggregateModelBuilder.MergeSerializableTypes(source, referenced);

    internal static ImmutableArray<ProxyInterfaceModel> MergeProxyInterfaces(
        ImmutableArray<ProxyInterfaceModel> source,
        ImmutableArray<ProxyInterfaceModel> referenced)
        => MetadataAggregateModelBuilder.MergeProxyInterfaces(source, referenced);

    internal static ImmutableArray<SerializableTypeModel> DeduplicateSerializableTypes(
        ImmutableArray<SerializableTypeModel> entries)
        => MetadataAggregateModelBuilder.DeduplicateSerializableTypes(entries);

    internal static ImmutableArray<ProxyInterfaceModel> DeduplicateProxyInterfaces(
        ImmutableArray<ProxyInterfaceModel> entries)
        => MetadataAggregateModelBuilder.DeduplicateProxyInterfaces(entries);

    internal static ImmutableArray<SerializableTypeModel> NormalizeSerializableTypeModels(
        ImmutableArray<SerializableTypeModel> entries)
        => MetadataAggregateModelBuilder.NormalizeSerializableTypeModels(entries);

    internal static ImmutableArray<ProxyInterfaceModel> NormalizeProxyInterfaceModels(
        ImmutableArray<ProxyInterfaceModel> entries)
        => MetadataAggregateModelBuilder.NormalizeProxyInterfaceModels(entries);

    public static RegisteredCodecModel ExtractRegisteredCodec(INamedTypeSymbol symbol, RegisteredCodecKind kind)
        => ReferenceAssemblyModelExtractor.ExtractRegisteredCodec(symbol, kind);

    internal static SerializableTypeModel? TryExtractSerializableTypeModel(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        LibraryTypes libraryTypes,
        CodeGeneratorOptions options,
        bool throwOnFailure = false)
        => SerializableTypeModelExtractor.TryExtractSerializableTypeModel(typeSymbol, compilation, libraryTypes, options, throwOnFailure);

    public static ProxyInterfaceModel? ExtractProxyInterfaceFromAttributeContext(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
        => ProxyInterfaceModelExtractor.ExtractProxyInterfaceFromAttributeContext(context, cancellationToken);

    public static ProxyInterfaceModel? ExtractProxyInterfaceModel(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        CancellationToken cancellationToken)
        => ProxyInterfaceModelExtractor.ExtractProxyInterfaceModel(typeSymbol, compilation, cancellationToken);

    public static ProxyInterfaceModel? ExtractInheritedProxyInterfaceFromSyntaxContext(
        GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
        => ProxyInterfaceModelExtractor.ExtractInheritedProxyInterfaceFromSyntaxContext(context, cancellationToken);

    internal static SourceLocationModel GetSourceLocation(ISymbol? symbol)
        => SymbolSourceLocationExtractor.GetSourceLocation(symbol);
}
