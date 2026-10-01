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
        var results = new Dictionary<ITypeSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        var output = ImmutableArray.CreateBuilder<SourceOutputResult>();
        foreach (var proxy in proxies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolver.TryResolveProxyInterface(proxy.ProxyInterface, cancellationToken, out var interfaceType))
            {
                continue;
            }

            foreach (var method in interfaceType.GetMembers().OfType<IMethodSymbol>()
                .Concat(interfaceType.AllInterfaces.SelectMany(static type => type.GetMembers().OfType<IMethodSymbol>()))
                .Where(static method => method.MethodKind == MethodKind.Ordinary))
            {
                var returnType = method.ReturnType;
                if (returnType.SpecialType == SpecialType.System_Void
                    || SymbolEqualityComparer.Default.Equals(returnType, services.LibraryTypes.Task)
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
        var dictionaryDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        foreach (var entry in results.OrderBy(static entry => entry.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            var resultType = entry.Key;
            var method = entry.Value;
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
            }
            else
            {
                Report(method, failure.Type, failure.Reason);
            }
        }

        if (supportedResults.Count == 0)
        {
            return output.ToImmutable();
        }

        if (!SerializerFactoryGenerator.TryCreate(services, supportedResults.Select(type => responseDefinition.Construct(type)), cancellationToken, out var graph, out var graphFailure, useDefaultFactories: true))
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
        source.AppendLine($"[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof({generatedNamespace}.RpcResponseFactories))]");
        source.AppendLine($"namespace {generatedNamespace}");
        source.AppendLine("{");
        source.AppendLine("internal sealed class RpcResponseFactories : global::Orleans.Serialization.SerializerContext");
        source.AppendLine("{");
        source.AppendLine("protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options)");
        source.AppendLine("{");
        source.AppendLine("#if NET5_0_OR_GREATER");
        source.AppendLine("if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;");
        source.AppendLine(graph.ConfigurationStatements);
        source.AppendLine("options.AddDefaultSerializerService<ResponseFieldCodec>(static provider => new ResponseFieldCodec());");
        source.AppendLine("options.AddDefaultSerializerService<ResponseFieldCopier>(static provider => new ResponseFieldCopier());");
        source.AppendLine("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ResponseFieldCodec>(null!, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ResponseFieldCopier>(null!, provider));");

        source.AppendLine("#endif");
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
        => type is ITypeParameterSymbol
            || type is IArrayTypeSymbol array && ContainsTypeParameter(array.ElementType)
            || type is INamedTypeSymbol named && (named.TypeArguments.Any(ContainsTypeParameter)
                || named.ContainingType is { } containing && ContainsTypeParameter(containing));
}
