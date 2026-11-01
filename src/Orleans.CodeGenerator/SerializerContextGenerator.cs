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
        var includeDefinition = compilation.GetTypeByMetadataName("Orleans.IncludeSerializerContextAttribute`1");
        var roots = new List<ITypeSymbol>();
        var linkedContexts = new List<INamedTypeSymbol>();
        var providedRoots = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var visitedContexts = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { symbol };
        var activeContexts = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { symbol };
        foreach (var attribute in symbol.GetAttributes().Where(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass?.OriginalDefinition, attributeDefinition)))
        {
            var type = attribute.AttributeClass!.TypeArguments[0];
            if (HasInvalidTypeArgument(type))
                return [new(SourceOutputResult.FromSource(declaration), default)];
            if (type is INamedTypeSymbol named && IsContext(named))
            {
                var diagnostic = Diagnostic.Create(UnsupportedType, location, symbol.Name,
                    named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    "use IncludeSerializerContextAttribute to link a context; GenerateSerializerContextAttribute declares model roots");
                return [new(new SourceOutputResult(declaration, diagnostic), default)];
            }
            roots.Add(type);
        }
        foreach (var attribute in symbol.GetAttributes().Where(attribute =>
            includeDefinition is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass?.OriginalDefinition, includeDefinition)))
        {
            var type = attribute.AttributeClass!.TypeArguments[0];
            if (HasInvalidTypeArgument(type))
                return [new(SourceOutputResult.FromSource(declaration), default)];
            if (type is not INamedTypeSymbol named || !IsContext(named))
            {
                var diagnostic = Diagnostic.Create(UnsupportedType, location, symbol.Name,
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "include a generated serializer context");
                return [new(new SourceOutputResult(declaration, diagnostic), default)];
            }
            var alreadyVisited = visitedContexts.Contains(named);
            if (VisitContext(named, 0) is { } linkFailure)
            {
                var diagnostic = Diagnostic.Create(UnsupportedType, location, symbol.Name,
                    linkFailure.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), linkFailure.Reason);
                return [new(new SourceOutputResult(declaration, diagnostic), default)];
            }
            if (!alreadyVisited) linkedContexts.Add(named);
        }

        var services = new GeneratorServices(compilation, SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options));
        if (!SerializerFactoryGenerator.TryCreate(services, roots, providedRoots, cancellationToken, out var graph, out var failure))
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
            SourceOutputResult.FromSource(CreateImplementation(symbol, compilation.AssemblyName, graph.ConfigurationStatements, linkedContexts)))];

        bool IsContext(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, symbol.BaseType)) return true;
            }
            return false;
        }

        SerializerFactoryGenerator.Failure? VisitContext(INamedTypeSymbol linked, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activeContexts.Contains(linked))
                return new(linked, "serializer-context includes must be acyclic so each supplied root has an owning context");
            if (visitedContexts.Contains(linked)) return null;
            if (depth >= 128 || visitedContexts.Count >= 1024)
                return new(linked, "the linked context graph exceeds 128 nested dependencies or 1024 contexts");
            if (linked.IsAbstract || linked.IsGenericType || linked.ContainingType is not null
                || !SymbolEqualityComparer.Default.Equals(linked.BaseType, symbol.BaseType)
                || !compilation.IsSymbolAccessibleWithin(linked, symbol)
                || !linked.InstanceConstructors.Any(constructor => constructor.Parameters.Length == 0
                    && compilation.IsSymbolAccessibleWithin(constructor, symbol)))
                return new(linked, "link a concrete, non-generic generated context with accessible parameterless construction");

            var attributes = linked.GetAttributes().Where(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass?.OriginalDefinition, attributeDefinition)
                || includeDefinition is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass?.OriginalDefinition, includeDefinition)).ToArray();
            if (attributes.Length == 0)
                return new(linked, "the linked context must declare roots using GenerateSerializerContextAttribute or links using IncludeSerializerContextAttribute");
            visitedContexts.Add(linked);
            activeContexts.Add(linked);
            foreach (var attribute in attributes)
            {
                var type = attribute.AttributeClass!.TypeArguments[0];
                if (HasInvalidTypeArgument(type))
                    return new(linked, "linked contexts must declare closed, available root types");
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass.OriginalDefinition, includeDefinition))
                {
                    if (type is not INamedTypeSymbol nested || !IsContext(nested))
                        return new(linked, "include a generated serializer context");
                    if (VisitContext(nested, depth + 1) is { } nestedFailure) return nestedFailure;
                }
                else
                {
                    if (type is INamedTypeSymbol named && IsContext(named))
                        return new(linked, "use IncludeSerializerContextAttribute to link a context; GenerateSerializerContextAttribute declares model roots");
                    providedRoots.Add(type.WithNullableAnnotation(NullableAnnotation.None));
                }
            }
            activeContexts.Remove(linked);
            return null;
        }
    }

    private static GeneratedSourceEntry CreateDeclaration(INamedTypeSymbol symbol, string? assemblyName)
    {
        var writer = CreateClassWriter(symbol);
        writer.WriteLine(GeneratedCodeUtilities.GeneratedMemberAttributeText);
        writer.WriteLine("protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options) => ConfigureGeneratedSerializerContext(options);");
        // An optional partial hook preserves the concrete context's shape when a host omits implementation outputs.
        writer.WriteLine("partial void ConfigureGeneratedSerializerContext(global::Orleans.Serialization.Configuration.TypeManifestOptions options);");
        writer.CloseBlock();
        return new GeneratedSourceEntry(
            $"{assemblyName}.orleans.context-declaration.{symbol.ToDisplayString()}.g.cs",
            writer.ToString());
    }

    private static GeneratedSourceEntry CreateImplementation(
        INamedTypeSymbol symbol, string? assemblyName, string configurationStatements, List<INamedTypeSymbol> linkedContexts)
    {
        var writer = CreateClassWriter(symbol);
        writer.WriteLine("partial void ConfigureGeneratedSerializerContext(global::Orleans.Serialization.Configuration.TypeManifestOptions options)");
        writer.OpenBlock();
        foreach (var linked in linkedContexts)
        {
            writer.WriteLine($"((global::Microsoft.Extensions.Options.IConfigureOptions<global::Orleans.Serialization.Configuration.TypeManifestOptions>)new {CodeText.Type(linked.ToTypeSyntax())}()).Configure(options);");
        }
        writer.WriteFragment(configurationStatements);
        writer.CloseBlock();
        writer.CloseBlock();
        return new GeneratedSourceEntry(
            $"{assemblyName}.orleans.context.{symbol.ToDisplayString()}.g.cs",
            writer.ToString());
    }

    // Opens the authored partial class. Its body is indented once; a file-scoped namespace adds no indentation.
    private static SourceWriter CreateClassWriter(INamedTypeSymbol symbol)
    {
        var writer = new SourceWriter();
        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine("#nullable enable");
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
        {
            writer.WriteLine($"namespace {symbol.ContainingNamespace.ToDisplayString()};");
        }

        writer.WriteLine($"partial class {symbol.Name.EscapeIdentifier()}");
        writer.OpenBlock();
        return writer;
    }

    private static bool HasInvalidTypeArgument(ITypeSymbol type)
        => type.TypeKind is TypeKind.Error or TypeKind.TypeParameter
            || type is IArrayTypeSymbol array && HasInvalidTypeArgument(array.ElementType)
            || type is INamedTypeSymbol named
                && (named.IsUnboundGenericType
                    || named.TypeArguments.Any(HasInvalidTypeArgument)
                    || named.ContainingType is { } containing && HasInvalidTypeArgument(containing));
}
