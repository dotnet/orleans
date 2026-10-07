using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal sealed class RpcResponsePlan
{
    internal sealed record Result(
        ITypeSymbol Type,
        string? Codec,
        string? Copier,
        SerializerFactoryGenerator.Graph? Graph,
        SerializerFactoryGenerator.Graph? ModelRoot,
        SerializerFactoryGenerator.Failure? Failure,
        ITypeSymbol? Dictionary);

    private RpcResponsePlan(ImmutableArray<Result> results)
    {
        Results = results.ToImmutableDictionary(static result => result.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            StringComparer.Ordinal);
        Names = RpcResponseHolderGenerator.GetNames(results.Where(static result => result.Codec is not null)
            .Select(static result => result.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }

    internal ImmutableDictionary<string, Result> Results { get; }
    internal ImmutableArray<(string TypeName, string HolderName)> Names { get; }

    internal static RpcResponsePlan Create(Compilation compilation, ImmutableArray<ProxyInterfaceModel> proxies,
        SourceGeneratorOptions options, CancellationToken cancellationToken)
    {
        if (proxies.IsDefaultOrEmpty) return new([]);
        compilation = WithBindingTree(compilation);
        var services = new GeneratorServices(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        var resolver = new TypeSymbolResolver(compilation);
        var types = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var proxy in proxies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolver.TryResolveProxyInterface(proxy, cancellationToken, out var interfaceType)) continue;
            foreach (var method in interfaceType.GetDeclaredInstanceMembers<IMethodSymbol>()
                .Concat(interfaceType.AllInterfaces.SelectMany(static type => type.GetDeclaredInstanceMembers<IMethodSymbol>())))
            {
                if (method.MethodKind == MethodKind.Ordinary && method.ReturnType is INamedTypeSymbol { TypeArguments.Length: 1 } returnType
                    && (SymbolEqualityComparer.Default.Equals(returnType.OriginalDefinition, services.LibraryTypes.Task_1)
                        || SymbolEqualityComparer.Default.Equals(returnType.OriginalDefinition, services.LibraryTypes.ValueTask_1)))
                    types.Add(returnType.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.None));
            }
        }

        var responseDefinition = compilation.GetTypeByMetadataName("Orleans.Serialization.Invocation.Response`1")!;
        var dictionaryDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        var results = ImmutableArray.CreateBuilder<Result>();
        foreach (var type in types.OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            if (ContainsTypeParameter(type)) continue;
            if (SerializerFactoryGenerator.TryCreate(services, [responseDefinition.Construct(type)], cancellationToken,
                out var graph, out var failure, useDefaultFactories: true))
            {
                var dictionary = graph.Registrations.Keys.OfType<INamedTypeSymbol>().FirstOrDefault(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, dictionaryDefinition));
                var registration = graph.Registrations[type];
                results.Add(new(type, dictionary is null ? registration.Codec : null,
                    dictionary is null ? registration.Copier : null, graph, null, null, dictionary));
            }
            else
            {
                SerializerFactoryGenerator.Graph? modelRoot = type is INamedTypeSymbol named
                    ? SerializerFactoryGenerator.CreateRpcModelRoot(services, named, cancellationToken) : null;
                string? codec = null;
                string? copier = null;
                if (modelRoot is not null && (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                    || compilation.GetTypeByMetadataName($"{SerializerGenerator.GetGeneratedNamespaceName((INamedTypeSymbol)type)}.{SerializerGenerator.GetSimpleClassName(type.Name)}") is not null))
                {
                    codec = modelRoot.Registrations[type].Codec;
                    copier = modelRoot.Registrations[type].Copier;
                }
                else if (RpcResponseHolderGenerator.TryDescribe(services, type, out var availableCodec, out var availableCopier))
                {
                    codec = availableCodec;
                    copier = availableCopier;
                }
                results.Add(new(type, codec, copier, null, modelRoot, failure, null));
            }
        }
        return new(results.ToImmutable());
    }

    internal static Compilation WithBindingTree(Compilation compilation)
        => compilation.SyntaxTrees.Any() ? compilation
            : compilation.AddSyntaxTrees(CSharpSyntaxTree.Create(SyntaxFactory.CompilationUnit()));

    internal static bool ContainsTypeParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol or IErrorTypeSymbol
            || type is IArrayTypeSymbol array && ContainsTypeParameter(array.ElementType)
            || type is INamedTypeSymbol named && (named.TypeArguments.Any(ContainsTypeParameter)
                || named.ContainingType is { } containing && ContainsTypeParameter(containing));
}
