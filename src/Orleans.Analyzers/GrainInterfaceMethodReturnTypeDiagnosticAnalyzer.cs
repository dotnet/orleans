using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class GrainInterfaceMethodReturnTypeDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ORLEANS0009";
        public const string Title = "Grain interfaces methods must return a compatible type";
        public const string MessageFormat = "Grain interface methods must return a registered grain-call return type";
        public const string Category = "Usage";
        public const string InvalidMappingDiagnosticId = "ORLEANS0026";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
        private static readonly DiagnosticDescriptor InvalidMappingRule = new(
            InvalidMappingDiagnosticId,
            "Invalid invokable base type mapping",
            "{0}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule, InvalidMappingRule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(context =>
            {
                if (context.Compilation.GetTypeByMetadataName(Constants.IAddressibleFullyQualifiedName) is not { } baseInterface)
                {
                    return;
                }

                var resolver = new InvokableBaseTypeResolver(context.Compilation);
                var generateMethodSerializersAttribute = context.Compilation.GetTypeByMetadataName("Orleans.GenerateMethodSerializersAttribute");
                context.RegisterSymbolAction(
                    context => AnalyzeMethod(context, baseInterface, generateMethodSerializersAttribute, resolver),
                    SymbolKind.Method);
            });
        }

        private static void AnalyzeMethod(
            SymbolAnalysisContext context,
            INamedTypeSymbol baseInterface,
            INamedTypeSymbol? generateMethodSerializersAttribute,
            InvokableBaseTypeResolver resolver)
        {
            var symbol = (IMethodSymbol)context.Symbol;

            if (symbol.ContainingType.TypeKind != TypeKind.Interface) return;

            // allow static interface methods to return any type
            if (symbol.IsStatic)
                return;

            var isIAddressableInterface = false;
            foreach (var implementedInterface in symbol.ContainingType.AllInterfaces)
            {
                if (implementedInterface.Equals(baseInterface, SymbolEqualityComparer.Default))
                {
                    isIAddressableInterface = true;
                    break;
                }
            }

            if (!isIAddressableInterface)
                return;

            var proxyBaseTypes = GetProxyBaseTypes(symbol.ContainingType, generateMethodSerializersAttribute);
            ResolverDiagnostic? mappingDiagnostic = null;
            foreach (var proxyBaseType in proxyBaseTypes)
            {
                if (resolver.TryResolve(proxyBaseType, symbol, out _, out var diagnostic))
                {
                    return;
                }

                if (diagnostic is not null
                    && !diagnostic.Message.StartsWith("No invokable base type is registered", StringComparison.Ordinal))
                {
                    mappingDiagnostic ??= diagnostic;
                }
            }

            var syntaxReference = symbol.DeclaringSyntaxReferences;
            if (mappingDiagnostic is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidMappingRule,
                    mappingDiagnostic.Location ?? Location.Create(syntaxReference[0].SyntaxTree, syntaxReference[0].Span),
                    mappingDiagnostic.Message));
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(syntaxReference[0].SyntaxTree, syntaxReference[0].Span)));
        }

        private static ImmutableArray<INamedTypeSymbol> GetProxyBaseTypes(
            INamedTypeSymbol interfaceType,
            INamedTypeSymbol? generateMethodSerializersAttribute)
        {
            if (generateMethodSerializersAttribute is null)
            {
                return [];
            }

            var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            foreach (var candidate in interfaceType.AllInterfaces.Add(interfaceType))
            {
                foreach (var attribute in candidate.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generateMethodSerializersAttribute)
                        && attribute.ConstructorArguments.Length > 0
                        && attribute.ConstructorArguments[0].Value is INamedTypeSymbol proxyBaseType
                        && !result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, proxyBaseType)))
                    {
                        result.Add(proxyBaseType.OriginalDefinition);
                    }
                }
            }

            return [.. result.OrderBy(
                static type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)];
        }
    }
}
