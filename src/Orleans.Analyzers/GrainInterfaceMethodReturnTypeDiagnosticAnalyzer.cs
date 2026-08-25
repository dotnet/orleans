using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class GrainInterfaceMethodReturnTypeDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ORLEANS0009";
        public const string Title = "Grain interface methods must return a registered grain-call return type";
        public const string MessageFormat = "Grain interface methods must return a registered grain-call return type";
        public const string Category = "Usage";
        public const string InvalidMappingDiagnosticId = "ORLEANS0026";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            new LocalizableResourceString(nameof(Resources.GrainInterfaceMethodReturnTypeTitle), Resources.ResourceManager, typeof(Resources)),
            new LocalizableResourceString(nameof(Resources.GrainInterfaceMethodReturnTypeMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: new LocalizableResourceString(
                nameof(Resources.GrainInterfaceMethodReturnTypeDescription),
                Resources.ResourceManager,
                typeof(Resources)),
            helpLinkUri: Constants.GetDiagnosticHelpLink(DiagnosticId));
        private static readonly DiagnosticDescriptor InvalidMappingRule = new(
            InvalidMappingDiagnosticId,
            new LocalizableResourceString(nameof(Resources.InvalidInvokableBaseTypeMappingTitle), Resources.ResourceManager, typeof(Resources)),
            new LocalizableResourceString(nameof(Resources.InvalidInvokableBaseTypeMappingMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: new LocalizableResourceString(
                nameof(Resources.InvalidInvokableBaseTypeMappingDescription),
                Resources.ResourceManager,
                typeof(Resources)),
            helpLinkUri: Constants.GetDiagnosticHelpLink(InvalidMappingDiagnosticId));

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

                var generateMethodSerializersAttribute = context.Compilation.GetTypeByMetadataName("Orleans.GenerateMethodSerializersAttribute");
                var proxyContexts = GetProxyContexts(
                    context.Compilation.Assembly.GlobalNamespace,
                    baseInterface,
                    generateMethodSerializersAttribute);
                var resolver = new InvokableBaseTypeResolver(context.Compilation);
                context.RegisterSymbolAction(
                    context => AnalyzeMethod(context, proxyContexts, resolver),
                    SymbolKind.Method);
            });
        }

        private static void AnalyzeMethod(
            SymbolAnalysisContext context,
            ImmutableArray<ProxyContext> proxyContexts,
            InvokableBaseTypeResolver resolver)
        {
            var symbol = (IMethodSymbol)context.Symbol;
            if (symbol.ContainingType.TypeKind != TypeKind.Interface || symbol.IsStatic)
            {
                return;
            }

            ResolverDiagnostic? diagnostic = null;
            foreach (var proxyContext in proxyContexts)
            {
                if (!TryGetContextMethod(proxyContext.InterfaceType, symbol, out var contextMethod))
                {
                    continue;
                }

                if (resolver.TryResolve(
                    proxyContext.ProxyBaseType,
                    contextMethod,
                    proxyContext.InterfaceType,
                    out _,
                    out diagnostic))
                {
                    continue;
                }

                break;
            }

            if (diagnostic is null)
            {
                return;
            }

            var syntaxReference = symbol.DeclaringSyntaxReferences[0];
            if (diagnostic.Kind == ResolverDiagnosticKind.InvalidMapping)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidMappingRule,
                    diagnostic.Location ?? Location.Create(syntaxReference.SyntaxTree, syntaxReference.Span),
                    diagnostic.Message));
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                Location.Create(syntaxReference.SyntaxTree, syntaxReference.Span)));
        }

        private static bool TryGetContextMethod(
            INamedTypeSymbol proxyInterface,
            IMethodSymbol method,
            out IMethodSymbol contextMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(
                proxyInterface.OriginalDefinition,
                method.ContainingType.OriginalDefinition))
            {
                contextMethod = method;
                return true;
            }

            foreach (var inheritedInterface in proxyInterface.AllInterfaces)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                    inheritedInterface.OriginalDefinition,
                    method.ContainingType.OriginalDefinition))
                {
                    continue;
                }

                foreach (var candidate in inheritedInterface.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, method.OriginalDefinition))
                    {
                        contextMethod = candidate;
                        return true;
                    }
                }
            }

            contextMethod = null!;
            return false;
        }

        private static ImmutableArray<ProxyContext> GetProxyContexts(
            INamespaceSymbol globalNamespace,
            INamedTypeSymbol baseInterface,
            INamedTypeSymbol? generateMethodSerializersAttribute)
        {
            if (generateMethodSerializersAttribute is null)
            {
                return [];
            }

            var result = new List<ProxyContext>();
            AddNamespace(globalNamespace);
            return [.. result
                .OrderBy(static entry => entry.SourceOrderGroup)
                .ThenBy(static entry => entry.FilePath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Position)
                .ThenBy(
                    static entry => entry.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    StringComparer.Ordinal)];

            void AddNamespace(INamespaceSymbol @namespace)
            {
                foreach (var member in @namespace.GetMembers())
                {
                    if (member is INamespaceSymbol childNamespace)
                    {
                        AddNamespace(childNamespace);
                    }
                    else if (member is INamedTypeSymbol type)
                    {
                        AddType(type);
                    }
                }
            }

            void AddType(INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Interface
                    && type.AllInterfaces.Any(implemented =>
                        SymbolEqualityComparer.Default.Equals(implemented, baseInterface))
                    && InvokableBaseTypeResolver.TryGetProxyBaseType(
                        type,
                        generateMethodSerializersAttribute,
                        out var proxyBaseType,
                        out _))
                {
                    var location = type.Locations.FirstOrDefault(static candidate => candidate.IsInSource);
                    result.Add(new ProxyContext(
                        type,
                        proxyBaseType,
                        location is null ? 1 : 0,
                        location?.SourceTree?.FilePath ?? string.Empty,
                        location?.SourceSpan.Start ?? int.MaxValue));
                }

                foreach (var nestedType in type.GetTypeMembers())
                {
                    AddType(nestedType);
                }
            }
        }

        private sealed record ProxyContext(
            INamedTypeSymbol InterfaceType,
            INamedTypeSymbol ProxyBaseType,
            int SourceOrderGroup,
            string FilePath,
            int Position);
    }
}
