using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class RpcResponseGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedResponse = new(
        DiagnosticRuleId.UnsupportedRpcResponseFactory,
        new LocalizableResourceString("UnsupportedRpcResponseFactoryTitle", Resources.ResourceManager, typeof(Resources)),
        new LocalizableResourceString("UnsupportedRpcResponseFactoryMessageFormat", Resources.ResourceManager, typeof(Resources)),
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedArgument = new(
        DiagnosticRuleId.UnsupportedRpcResponseFactory,
        new LocalizableResourceString("UnsupportedRpcArgumentFactoryTitle", Resources.ResourceManager, typeof(Resources)),
        new LocalizableResourceString("UnsupportedRpcArgumentFactoryMessageFormat", Resources.ResourceManager, typeof(Resources)),
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static ImmutableArray<SourceOutputResult> Generate(
        Compilation compilation,
        ProxyOutputPreparationResult preparation,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        var proxies = preparation.ProxyOutputModels;
        if (proxies.IsDefaultOrEmpty)
        {
            return [];
        }

        if (!compilation.SyntaxTrees.Any())
        {
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.Create(SyntaxFactory.CompilationUnit()));
        }

        var services = new GeneratorServices(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        var resolver = new TypeSymbolResolver(compilation);

        var binding = compilation.GetSemanticModel(compilation.SyntaxTrees.First());
        var constructorServices = GetConstructorServices(preparation.SourceOutputs, cancellationToken);
        var results = new Dictionary<ITypeSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        var arguments = new Dictionary<ITypeSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        var hasResponseMethods = false;
        var output = ImmutableArray.CreateBuilder<SourceOutputResult>();
        foreach (var proxy in proxies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolver.TryResolveProxyInterface(proxy.ProxyInterface, cancellationToken, out var interfaceType))
            {
                continue;
            }

            var methods = interfaceType.GetDeclaredInstanceMembers<IMethodSymbol>()
                .Concat(interfaceType.AllInterfaces.SelectMany(static type => type.GetDeclaredInstanceMembers<IMethodSymbol>()))
                .Where(static method => method.MethodKind == MethodKind.Ordinary).ToImmutableArray();
            constructorServices.TryGetValue(interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), out var requests);
            foreach (var request in requests.IsDefault ? [] : requests)
            {
                if (binding.GetSpeculativeTypeInfo(0, request, SpeculativeBindingOption.BindAsTypeOrNamespace).Type is INamedTypeSymbol service
                    && service.AllInterfaces.Concat([service]).FirstOrDefault(type =>
                        SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, services.LibraryTypes.DeepCopier_1)) is { } copier
                    && !SerializerFactoryGenerator.ContainsTypeParameter(copier.TypeArguments[0]) && !arguments.ContainsKey(copier.TypeArguments[0]))
                {
                    arguments.Add(copier.TypeArguments[0], methods[0]);
                }
            }

            foreach (var method in methods)
            {
                if (method.TypeParameters.Length == 0)
                {
                    foreach (var parameter in method.Parameters)
                    {
                        var parameterType = parameter.Type.WithNullableAnnotation(NullableAnnotation.None);
                        if (!SerializerFactoryGenerator.ContainsTypeParameter(parameterType) && !services.LibraryTypes.IsShallowCopyable(parameterType)
                            && !arguments.ContainsKey(parameterType))
                        {
                            arguments.Add(parameterType, method);
                        }
                    }
                }

                var returnType = method.ReturnType;
                if (returnType.SpecialType == SpecialType.System_Void)
                {
                    continue;
                }

                hasResponseMethods = true;
                if (SymbolEqualityComparer.Default.Equals(returnType, services.LibraryTypes.Task)
                    || SymbolEqualityComparer.Default.Equals(returnType, services.LibraryTypes.ValueTask))
                {
                    continue;
                }

                if (returnType is not INamedTypeSymbol named
                    || !(SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, services.LibraryTypes.Task_1)
                        || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, services.LibraryTypes.ValueTask_1)))
                {
                    Report(UnsupportedResponse, method, returnType, "the selected return adapter requires an explicit response result contract");
                    continue;
                }

                var resultType = named.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.None);
                if (SerializerFactoryGenerator.ContainsTypeParameter(resultType))
                {
                    Report(UnsupportedResponse, method, resultType, "the result contains an unresolved type parameter");
                    continue;
                }

                if (!results.ContainsKey(resultType))
                {
                    results.Add(resultType, method);
                }
            }
        }

        var responseDefinition = compilation.GetTypeByMetadataName("Orleans.Serialization.Invocation.Response`1")!;
        var dictionaryDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        var resultGraphs = new List<SerializerFactoryGenerator.Graph>();
        var coveredConstructionTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var metadataModelRoots = new List<SerializerFactoryGenerator.Graph>();
        var argumentRoots = new List<SerializerFactoryGenerator.Graph>();
        foreach (var entry in results.OrderBy(static entry => entry.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            var resultType = entry.Key;
            var method = entry.Value;
            if (SerializerFactoryGenerator.TryCreate(services, [responseDefinition.Construct(resultType)], cancellationToken,
                out var resultGraph, out var failure, useDefaultFactories: true))
            {
                if (FindDictionary(resultGraph) is { } dictionary)
                {
                    Report(UnsupportedResponse, method, dictionary, "dictionary comparers are selected per value and require an explicit closed registration preserving the comparer contract");
                    continue;
                }

                resultGraphs.Add(resultGraph);
                coveredConstructionTypes.UnionWith(resultGraph.Registrations.Keys);
            }
            else if (!options.ValidateRpcResponseFactories && resultType is INamedTypeSymbol named
                && SerializerFactoryGenerator.CreateRpcModelRoot(services, named, cancellationToken) is { } metadataRoot)
            {
                metadataModelRoots.Add(metadataRoot);
            }
            else
            {
                Report(UnsupportedResponse, method, failure!.Type, failure.Reason);
            }
        }

        foreach (var argument in arguments.OrderBy(static entry => entry.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            if (coveredConstructionTypes.Contains(argument.Key)) continue;
            if (SerializerFactoryGenerator.TryCreate(services, [argument.Key], cancellationToken, out var argumentGraph, out var failure, useDefaultFactories: true)
                && FindDictionary(argumentGraph) is null)
            {
                argumentRoots.Add(argumentGraph);
                coveredConstructionTypes.UnionWith(argumentGraph.Registrations.Keys);
            }
            else if (!options.ValidateRpcResponseFactories)
            {
                metadataModelRoots.Add(SerializerFactoryGenerator.CreateRpcConstructionRoot(services, argument.Key, cancellationToken));
            }
            else
            {
                Report(UnsupportedArgument, argument.Value, argument.Key, failure?.Reason ?? "the argument graph requires an explicit closed construction contract");
            }
        }

        if (resultGraphs.Count == 0 && metadataModelRoots.Count == 0 && argumentRoots.Count == 0 && !hasResponseMethods)
        {
            return output.ToImmutable();
        }

        SerializerFactoryGenerator.Graph? graph = null;
        if (resultGraphs.Count > 0 && !SerializerFactoryGenerator.TryCombine(services, resultGraphs, cancellationToken, out graph, out var graphFailure))
        {
            output.Add(SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                UnsupportedResponse, Location.None, compilation.AssemblyName, graphFailure.Type.ToDisplayString(), graphFailure.Reason)));
        }

        var generatedNamespace = $"{GeneratedCodeUtilities.CodeGeneratorName}.{Identifier.SanitizeIdentifierName(compilation.AssemblyName ?? "Assembly").EscapeIdentifier()}";
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable disable");
        source.AppendLine($"[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof({generatedNamespace}.RpcResponseFactories))]");
        source.AppendLine($"namespace {generatedNamespace}");
        source.AppendLine("{");
        source.AppendLine("internal sealed class RpcResponseFactories : global::Orleans.Serialization.SerializerContext");
        source.AppendLine("{");
        source.AppendLine("protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options)");
        source.AppendLine("{");
        if (graph is not null)
        {
            source.AppendLine(graph.ConfigurationStatements);
        }

        foreach (var root in argumentRoots.Concat(metadataModelRoots))
        {
            source.AppendLine(root.ConfigurationStatements);
        }

        if (hasResponseMethods)
        {
            source.AppendLine("options.AddResponseSerializers();");
        }

        source.AppendLine("}");
        source.AppendLine("}");
        source.AppendLine("}");
        var unit = CSharpSyntaxTree.ParseText(source.ToString(),
            cancellationToken: cancellationToken).GetCompilationUnitRoot(cancellationToken);
        var provider = unit.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        unit = unit.ReplaceNode(provider, provider.AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes()));
        output.Add(SourceOutputResult.FromSource(new GeneratedSourceEntry(
            $"{compilation.AssemblyName}.orleans.rpcresponses.g.cs", unit.NormalizeWhitespace().ToFullString())));
        return output.ToImmutable();

        // Dictionary comparers are chosen per value, so a static default graph cannot preserve them.
        ITypeSymbol? FindDictionary(SerializerFactoryGenerator.Graph candidate)
            => candidate.Registrations.Keys.OfType<INamedTypeSymbol>().FirstOrDefault(type =>
                SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, dictionaryDefinition));

        void Report(DiagnosticDescriptor descriptor, IMethodSymbol method, ITypeSymbol type, string reason)
        {
            if (options.ValidateRpcResponseFactories)
            {
                output.Add(SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                    descriptor, method.Locations.FirstOrDefault(), method.ToDisplayString(), type.ToDisplayString(), reason)));
            }
        }
    }
    private static ImmutableDictionary<string, ImmutableArray<TypeSyntax>> GetConstructorServices(
        ImmutableArray<SourceOutputResult> sources, CancellationToken cancellationToken)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<TypeSyntax>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.SourceEntry is not { } entry) continue;
            var root = CSharpSyntaxTree.ParseText(entry.SourceText, cancellationToken: cancellationToken).GetRoot(cancellationToken);
            foreach (var proxy in root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText.StartsWith("Proxy_", StringComparison.Ordinal)))
            {
                var requests = proxy.Members.OfType<ConstructorDeclarationSyntax>()
                    .SelectMany(static constructor => constructor.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "GetService" } })
                    .Select(static invocation => ((GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name)
                        .TypeArgumentList.Arguments.Single()).ToImmutableArray();
                if (proxy.BaseList is not null)
                    foreach (var contract in proxy.BaseList.Types) result[contract.Type.ToString()] = requests;
            }
        }
        return result.ToImmutable();
    }
}
