using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializerContextGenerator
{
    private static readonly DiagnosticDescriptor InvalidContext = new(
        DiagnosticRuleId.InvalidSerializerContext, "Invalid serializer context",
        "Serializer context '{0}' must be a non-generic, top-level partial class directly derived from Orleans.Serialization.SerializerContext",
        "Usage", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        DiagnosticRuleId.UnsupportedSerializerContextType, "Unsupported serializer context dependency",
        "Serializer context '{0}' cannot register '{1}': {2}",
        "Usage", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static ImmutableArray<SerializerContextOutput> Generate(
        GeneratorAttributeSyntaxContext context, SourceGeneratorOptions options, CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var location = symbol.Locations.FirstOrDefault();
        if (symbol.IsFileLocal || symbol.IsGenericType || symbol.ContainingType is not null
            || symbol.BaseType?.ToDisplayString() != "Orleans.Serialization.SerializerContext"
            || symbol.IsAbstract || symbol.IsStatic
            || symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellationToken) is not ClassDeclarationSyntax declaration
                || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            return [new(SourceOutputResult.FromDiagnostic(Diagnostic.Create(InvalidContext, location, symbol.Name)), default)];
        }

        var compilation = context.SemanticModel.Compilation;
        var declaration = CreateDeclaration(symbol, compilation.AssemblyName);
        var attributeDefinition = compilation.GetTypeByMetadataName("Orleans.GenerateSerializerContextAttribute`1");
        var roots = new List<ITypeSymbol>();
        foreach (var attribute in symbol.GetAttributes().Where(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass?.OriginalDefinition, attributeDefinition)))
        {
            var type = attribute.AttributeClass!.TypeArguments[0];
            if (HasInvalidTypeArgument(type))
                return [new(SourceOutputResult.FromSource(declaration), default)];
            roots.Add(type);
        }

        var services = new GeneratorServices(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        if (!SerializerFactoryGenerator.TryCreate(services, roots, cancellationToken, out var graph, out var failure))
        {
            var diagnostic = Diagnostic.Create(UnsupportedType, location, symbol.Name,
                failure.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), failure.Reason);
            // A producer's implementation outputs can be absent from an otherwise valid editor compilation.
            return [failure.RequiresImplementation
                ? new(SourceOutputResult.FromSource(declaration), SourceOutputResult.FromDiagnostic(diagnostic))
                : new(new SourceOutputResult(declaration, diagnostic), default)];
        }

        return [new(
            SourceOutputResult.FromSource(declaration),
            SourceOutputResult.FromSource(CreateImplementation(symbol, compilation.AssemblyName, graph.ConfigurationStatements)))];
    }

    private static GeneratedSourceEntry CreateDeclaration(INamedTypeSymbol symbol, string? assemblyName)
    {
        var result = CreateClassBuilder(symbol);
        result.AppendLine(GeneratedCodeUtilities.GetGeneratedMemberAttributes().ToFullString());
        result.AppendLine("protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options) => ConfigureGeneratedSerializerContext(options);");
        // An optional partial hook preserves the concrete context's shape when a host omits implementation outputs.
        result.AppendLine("partial void ConfigureGeneratedSerializerContext(global::Orleans.Serialization.Configuration.TypeManifestOptions options);");
        result.AppendLine("}");
        return new GeneratedSourceEntry(
            $"{assemblyName}.orleans.context-declaration.{symbol.ToDisplayString()}.g.cs",
            SyntaxFactory.ParseCompilationUnit(result.ToString()).NormalizeWhitespace().ToFullString());
    }

    private static GeneratedSourceEntry CreateImplementation(INamedTypeSymbol symbol, string? assemblyName, string configurationStatements)
    {
        var implementation = CreateClassBuilder(symbol);
        implementation.AppendLine("partial void ConfigureGeneratedSerializerContext(global::Orleans.Serialization.Configuration.TypeManifestOptions options) {");
        implementation.Append(configurationStatements);
        implementation.AppendLine("} }");
        return new GeneratedSourceEntry(
            $"{assemblyName}.orleans.context.{symbol.ToDisplayString()}.g.cs",
            SyntaxFactory.ParseCompilationUnit(implementation.ToString()).NormalizeWhitespace().ToFullString());
    }

    private static StringBuilder CreateClassBuilder(INamedTypeSymbol symbol)
    {
        var result = new StringBuilder();
        result.AppendLine("// <auto-generated/>");
        result.AppendLine("#nullable enable");
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
            result.Append("namespace ").Append(symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
        result.Append("partial class ").Append(symbol.Name.EscapeIdentifier()).AppendLine(" {");
        return result;
    }

    private static bool HasInvalidTypeArgument(ITypeSymbol type)
        => type.TypeKind is TypeKind.Error or TypeKind.TypeParameter
            || type is IArrayTypeSymbol array && HasInvalidTypeArgument(array.ElementType)
            || type is INamedTypeSymbol named
                && (named.IsUnboundGenericType
                    || named.TypeArguments.Any(HasInvalidTypeArgument)
                    || named.ContainingType is { } containing && HasInvalidTypeArgument(containing));
}
