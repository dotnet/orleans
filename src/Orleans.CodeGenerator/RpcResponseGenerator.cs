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
        RpcResponsePlan plan,
        CancellationToken cancellationToken)
    {
        var proxies = preparation.ProxyOutputModels;
        if (proxies.IsDefaultOrEmpty)
        {
            return [];
        }

        compilation = RpcResponsePlan.WithBindingTree(compilation);

        var services = new GeneratorServices(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        var resolver = new TypeSymbolResolver(compilation);
        var responseNames = plan.Names.ToImmutableDictionary(static entry => entry.TypeName, static entry => entry.HolderName, StringComparer.Ordinal);
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
                    && !RpcResponsePlan.ContainsTypeParameter(copier.TypeArguments[0]) && !arguments.ContainsKey(copier.TypeArguments[0]))
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
                        if (!RpcResponsePlan.ContainsTypeParameter(parameterType) && !services.LibraryTypes.IsShallowCopyable(parameterType)
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
                    Report(method, returnType, "the selected return adapter requires an explicit response result contract");
                    continue;
                }

                var resultType = named.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.None);
                if (RpcResponsePlan.ContainsTypeParameter(resultType))
                {
                    Report(method, resultType, "the result contains an unresolved type parameter");
                    continue;
                }

                if (!results.ContainsKey(resultType))
                {
                    results.Add(resultType, method);
                }
            }
        }

        var supportedResults = new List<ITypeSymbol>();
        var responseHolders = new Dictionary<ITypeSymbol, (string Codec, string Copier)>(SymbolEqualityComparer.Default);
        var coveredConstructionTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var metadataModelRoots = new List<SerializerFactoryGenerator.Graph>();
        var argumentRoots = new List<SerializerFactoryGenerator.Graph>();
        var dictionaryDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        foreach (var entry in results.OrderBy(static entry => entry.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            var resultType = entry.Key;
            var method = entry.Value;
            var description = plan.Results[resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)];
            if (description.Codec is { } holderCodec && description.Copier is { } holderCopier)
                responseHolders.Add(resultType, (holderCodec, holderCopier));
            if (description.Graph is { } candidate)
            {
                if (description.Dictionary is { } dictionary)
                {
                    Report(method, dictionary, "dictionary comparers are selected per value and require an explicit closed registration preserving the comparer contract");
                    continue;
                }

                supportedResults.Add(resultType);
                foreach (var known in candidate.Registrations.Keys) coveredConstructionTypes.Add(known);
            }
            else
            {
                if (!options.ValidateRpcResponseFactories && description.ModelRoot is { } metadataRoot)
                {
                    metadataModelRoots.Add(metadataRoot);
                    continue;
                }

                Report(method, description.Failure!.Type, description.Failure.Reason);
            }
        }

        foreach (var argument in arguments.OrderBy(static entry => entry.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            if (coveredConstructionTypes.Contains(argument.Key)) continue;
            if (SerializerFactoryGenerator.TryCreate(services, [argument.Key], cancellationToken, out var argumentGraph, out var failure, useDefaultFactories: true)
                && !argumentGraph.Registrations.Keys.OfType<INamedTypeSymbol>().Any(type => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, dictionaryDefinition)))
            {
                argumentRoots.Add(argumentGraph);
                foreach (var known in argumentGraph.Registrations.Keys) coveredConstructionTypes.Add(known);
            }
            else if (!options.ValidateRpcResponseFactories)
            {
                metadataModelRoots.Add(SerializerFactoryGenerator.CreateRpcConstructionRoot(services, argument.Key, cancellationToken));
            }
            else
            {
                ReportArgument(argument.Value, argument.Key, failure?.Reason ?? "the argument graph requires an explicit closed construction contract");
            }
        }

        if (supportedResults.Count == 0 && metadataModelRoots.Count == 0 && argumentRoots.Count == 0 && responseHolders.Count == 0 && !hasResponseMethods)
        {
            return output.ToImmutable();
        }

        SerializerFactoryGenerator.Graph? graph = null;
        if (supportedResults.Count > 0 && !SerializerFactoryGenerator.TryCombine(services,
            supportedResults.Select(type => plan.Results[type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)].Graph!),
            cancellationToken, out graph, out var graphFailure))
        {
            output.Add(SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                UnsupportedResponse, Location.None, compilation.AssemblyName, graphFailure.Type.ToDisplayString(), graphFailure.Reason)));
        }

        var generatedNamespace = $"{GeneratedCodeUtilities.CodeGeneratorName}.{Identifier.SanitizeIdentifierName(compilation.AssemblyName ?? "Assembly").EscapeIdentifier()}";
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable disable");
        source.AppendLine("using global::Orleans.Serialization.Codecs;");
        source.AppendLine("using global::Orleans.Serialization.GeneratedCodeHelpers;");
        source.AppendLine($"[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof({generatedNamespace}.RpcResponseFactories))]");
        source.AppendLine($"namespace {generatedNamespace}");
        source.AppendLine("{");
        source.AppendLine("internal sealed class RpcResponseFactories : global::Orleans.Serialization.SerializerContext");
        source.AppendLine("{");
        source.AppendLine("protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options)");
        source.AppendLine("{");
        foreach (var holder in responseHolders)
        {
            var name = responseNames[holder.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)];
            var factory = name + "Factory";
            var type = holder.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var resolve = $"{factory}.Resolve(provider)";
            source.AppendLine($"options.AddDefaultSerializerService<{factory}>(static provider => new {factory}(provider));");
            source.AppendLine($"options.AddDefaultSerializer<{name}>(static provider => {resolve}, static provider => {resolve});");
            source.AppendLine($"options.AddRawResponseReader<{type}>(static provider => {resolve});");
        }
        if (graph is not null)
        {
            source.AppendLine(graph.ConfigurationStatements);
        }

        foreach (var argumentRoot in argumentRoots)
        {
            source.AppendLine(argumentRoot.ConfigurationStatements);
        }

        if (metadataModelRoots.Count > 0)
        {
            foreach (var metadataRoot in metadataModelRoots)
            {
                source.AppendLine(metadataRoot.ConfigurationStatements);
            }
        }
        if (hasResponseMethods)
        {
            source.AppendLine("options.AddDefaultSerializerService<ResponseFieldCodec>(static provider => new ResponseFieldCodec());");
            source.AppendLine("options.AddDefaultSerializerService<ResponseFieldCopier>(static provider => new ResponseFieldCopier());");
            source.AppendLine("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ResponseFieldCodec>(null!, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ResponseFieldCopier>(null!, provider));");
            source.AppendLine("options.AddDefaultSerializerService<CompletedResponseActivator>(static provider => new CompletedResponseActivator());");
            source.AppendLine("options.AddDefaultSerializerService<global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse>(static provider => new global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<CompletedResponseActivator>(null!, provider)));");
            source.AppendLine("options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<global::Orleans.Serialization.Invocation.CompletedResponse>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<global::Orleans.Serialization.Invocation.CompletedResponse>());");
            source.AppendLine("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.CompletedResponse>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse>(null!, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<global::Orleans.Serialization.Invocation.CompletedResponse>>(null!, provider));");
            source.AppendLine("options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.CompletedResponse));");
        }

        source.AppendLine("}");
        source.AppendLine("private sealed class CompletedResponseActivator : global::Orleans.Serialization.Activators.IActivator<global::Orleans.Serialization.Invocation.CompletedResponse>");
        source.AppendLine("{");
        source.AppendLine("public CompletedResponseActivator() { }");
        source.AppendLine("public global::Orleans.Serialization.Invocation.CompletedResponse Create() => global::Orleans.Serialization.Invocation.CompletedResponse.Instance;");
        source.AppendLine("}");
        source.AppendLine("private sealed class ResponseFieldCodec : global::Orleans.Serialization.Serializers.AbstractTypeSerializer<global::Orleans.Serialization.Invocation.Response>");
        source.AppendLine("{");
        source.AppendLine("public ResponseFieldCodec() { }");
        source.AppendLine("}");
        source.AppendLine("private sealed class ResponseFieldCopier : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.Invocation.Response>");
        source.AppendLine("{");
        source.AppendLine("public ResponseFieldCopier() { }");
        source.AppendLine("[return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull(\"input\")]");
        source.AppendLine("public global::Orleans.Serialization.Invocation.Response DeepCopy(global::Orleans.Serialization.Invocation.Response input, global::Orleans.Serialization.Cloning.CopyContext context)");
        source.AppendLine("{");
        source.AppendLine("if (context is null) throw new global::System.ArgumentNullException(nameof(context));");
        source.AppendLine("if (input is global::Orleans.Serialization.Invocation.CompletedResponse or global::Orleans.Serialization.Invocation.ExceptionResponse) return input;");
        source.AppendLine("return (global::Orleans.Serialization.Invocation.Response)global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(input, context);");
        source.AppendLine("}");
        source.AppendLine("}");
        source.AppendLine("}");
        foreach (var holder in responseHolders)
        {
            var name = responseNames[holder.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)];
            source.AppendLine(RpcResponseHolderGenerator.Generate(services, holder.Key, name, holder.Value.Codec, holder.Value.Copier));
        }
        source.AppendLine("}");
        var unit = CSharpSyntaxTree.ParseText(source.ToString(),
            cancellationToken: cancellationToken).GetCompilationUnitRoot(cancellationToken);
        var provider = unit.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static declaration => declaration.Identifier.ValueText == "RpcResponseFactories");
        unit = unit.ReplaceNode(provider, provider.AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes()));
        output.Add(SourceOutputResult.FromSource(new GeneratedSourceEntry(
            $"{compilation.AssemblyName}.orleans.rpcresponses.g.cs", unit.NormalizeWhitespace().ToFullString())));
        return output.ToImmutable();

        void Report(IMethodSymbol method, ITypeSymbol resultType, string reason)
        {
            if (options.ValidateRpcResponseFactories)
            {
                output.Add(SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                    UnsupportedResponse, method.Locations.FirstOrDefault(), method.ToDisplayString(), resultType.ToDisplayString(), reason)));
            }

        }

        void ReportArgument(IMethodSymbol method, ITypeSymbol argumentType, string reason)
        {
            if (options.ValidateRpcResponseFactories)
            {
                output.Add(SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                    UnsupportedArgument, method.Locations.FirstOrDefault(), method.ToDisplayString(), argumentType.ToDisplayString(), reason)));
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
