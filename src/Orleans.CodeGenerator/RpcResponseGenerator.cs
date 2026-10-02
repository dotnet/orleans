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

    internal static ImmutableArray<SourceOutputResult> Generate(
        Compilation compilation,
        ImmutableArray<ProxyOutputModel> proxies,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (proxies.IsDefaultOrEmpty)
        {
            return [];
        }

        var services = new GeneratorServices(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        var responseDefinition = compilation.GetTypeByMetadataName("Orleans.Serialization.Invocation.Response`1")!;
        var resolver = new TypeSymbolResolver(compilation);
        var proxyContext = new ProxyGenerationContext(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        ProxySourceOutputGenerator.PopulateProxyInterfaces(proxyContext, resolver,
            proxies.Select(static proxy => proxy.ProxyInterface).ToImmutableArray(), cancellationToken);
        var binding = compilation.GetSemanticModel(compilation.SyntaxTrees.First());
        var results = new Dictionary<ITypeSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        var arguments = new Dictionary<ITypeSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        var hasCompletionMethods = false;
        var output = ImmutableArray.CreateBuilder<SourceOutputResult>();
        foreach (var proxy in proxies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolver.TryResolveProxyInterface(proxy.ProxyInterface, cancellationToken, out var interfaceType))
            {
                continue;
            }

            var description = ProxySourceOutputGenerator.GetProxyInterfaceDescription(proxyContext, resolver, proxy.ProxyInterface, cancellationToken);
            var (proxyClass, _) = new ProxyGenerator(proxyContext, new CopierGenerator(proxyContext)).Generate(description);
            foreach (var request in proxyClass.Members.OfType<ConstructorDeclarationSyntax>()
                .SelectMany(static constructor => constructor.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "GetService" } })
                .Select(static invocation => ((GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name).TypeArgumentList.Arguments.Single()))
            {
                if (binding.GetSpeculativeTypeInfo(0, request, SpeculativeBindingOption.BindAsTypeOrNamespace).Type is INamedTypeSymbol service
                    && service.AllInterfaces.Concat([service]).FirstOrDefault(type =>
                        SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, services.LibraryTypes.DeepCopier_1)) is { } copier
                    && !ContainsTypeParameter(copier.TypeArguments[0]) && !arguments.ContainsKey(copier.TypeArguments[0]))
                {
                    arguments.Add(copier.TypeArguments[0], description.Methods[0].Method);
                }
            }

            foreach (var method in interfaceType.GetDeclaredInstanceMembers<IMethodSymbol>()
                .Concat(interfaceType.AllInterfaces.SelectMany(static type => type.GetDeclaredInstanceMembers<IMethodSymbol>()))
                .Where(static method => method.MethodKind == MethodKind.Ordinary))
            {
                if (method.TypeParameters.Length == 0)
                {
                    foreach (var parameter in method.Parameters)
                    {
                        var parameterType = parameter.Type.WithNullableAnnotation(NullableAnnotation.None);
                        if (!ContainsTypeParameter(parameterType) && !services.LibraryTypes.IsShallowCopyable(parameterType)
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

                if (SymbolEqualityComparer.Default.Equals(returnType, services.LibraryTypes.Task)
                    || SymbolEqualityComparer.Default.Equals(returnType, services.LibraryTypes.ValueTask))
                {
                    hasCompletionMethods = true;
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
                if (ContainsTypeParameter(resultType))
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
            if (RpcResponseHolderGenerator.TryDescribe(services, resultType, out var holderCodec, out var holderCopier))
                responseHolders.Add(resultType, (holderCodec, holderCopier));
            if (SerializerFactoryGenerator.TryCreate(services, [responseDefinition.Construct(resultType)], cancellationToken, out var candidate, out var failure))
            {
                var dictionary = candidate.Registrations.Keys.OfType<INamedTypeSymbol>()
                    .FirstOrDefault(type => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, dictionaryDefinition));
                if (dictionary is not null)
                {
                    Report(method, dictionary, "dictionary comparers are selected per value and require an explicit closed registration preserving the comparer contract");
                    continue;
                }

                supportedResults.Add(resultType);
                foreach (var known in candidate.Registrations.Keys) coveredConstructionTypes.Add(known);
            }
            else
            {
                if (!options.ValidateRpcResponseFactories && resultType is INamedTypeSymbol named
                    && SerializerFactoryGenerator.CreateRpcModelRoot(services, named, cancellationToken) is { } metadataRoot)
                {
                    metadataModelRoots.Add(metadataRoot);
                    continue;
                }

                Report(method, failure.Type, failure.Reason);
            }
        }

        var hasResponseRoots = supportedResults.Count > 0 || metadataModelRoots.Count > 0 || hasCompletionMethods;
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
                Report(argument.Value, argument.Key, failure?.Reason ?? "the argument graph requires an explicit closed construction contract");
            }
        }

        if (supportedResults.Count == 0 && metadataModelRoots.Count == 0 && argumentRoots.Count == 0 && responseHolders.Count == 0 && !hasCompletionMethods)
        {
            return output.ToImmutable();
        }

        SerializerFactoryGenerator.Graph? graph = null;
        if (supportedResults.Count > 0 && !SerializerFactoryGenerator.TryCreate(services, supportedResults.Select(type => responseDefinition.Construct(type)), cancellationToken, out graph, out var graphFailure, useDefaultFactories: true))
        {
            if (options.ValidateRpcResponseFactories)
            {
                output.Add(SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                    UnsupportedResponse, Location.None, compilation.AssemblyName, graphFailure.Type.ToDisplayString(), graphFailure.Reason)));
            }

            return output.ToImmutable();
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
            var name = RpcResponseHolderGenerator.GetName(holder.Key);
            var factory = name + "Factory";
            var type = holder.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var resolve = $"{factory}.Resolve(provider)";
            source.AppendLine($"options.AddDefaultSerializerService<{factory}>(static provider => new {factory}(provider));");
            source.AppendLine($"options.AddDefaultSerializer<{name}>(static provider => {resolve}, static provider => {resolve});");
            source.AppendLine($"options.AddRawResponseReader<{type}>(static provider => {resolve});");
        }
        source.AppendLine("#if NET5_0_OR_GREATER");
        source.AppendLine("if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;");
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
        if (hasResponseRoots)
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

        source.AppendLine("#endif");
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
            source.AppendLine(RpcResponseHolderGenerator.Generate(services, holder.Key, holder.Value.Codec, holder.Value.Copier));
        }
        source.AppendLine("}");
        var unit = CSharpSyntaxTree.ParseText(source.ToString(),
            options: new CSharpParseOptions(preprocessorSymbols: ["NET5_0_OR_GREATER"]),
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
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol or IErrorTypeSymbol
            || type is IArrayTypeSymbol array && ContainsTypeParameter(array.ElementType)
            || type is INamedTypeSymbol named && (named.TypeArguments.Any(ContainsTypeParameter)
                || named.ContainingType is { } containing && ContainsTypeParameter(containing));
}
