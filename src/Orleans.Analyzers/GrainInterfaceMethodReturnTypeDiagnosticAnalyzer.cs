using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class GrainInterfaceMethodReturnTypeDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        private const string BaseInterfaceName = "Orleans.Runtime.IAddressable";

        public const string DiagnosticId = "ORLEANS0009";
        public const string Title = "Grain interfaces methods must return a compatible type";
        public const string MessageFormat = $"Grain interfaces methods must return a compatible type, such as Task, Task<T>, ValueTask, ValueTask<T>, or void";
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(context =>
            {
                if (context.Compilation.GetTypeByMetadataName(BaseInterfaceName) is not { } baseInterface)
                {
                    return;
                }

                var builder = ImmutableHashSet.CreateBuilder<ITypeSymbol>(SymbolEqualityComparer.Default);

                AddIfNotNull(builder, context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"));
                AddIfNotNull(builder, context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1"));
                AddIfNotNull(builder, context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"));
                AddIfNotNull(builder, context.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"));
                AddIfNotNull(builder, context.Compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1"));
                AddIfNotNull(builder, context.Compilation.GetSpecialType(SpecialType.System_Void));

                context.RegisterSymbolAction(context => AnalyzeMethod(context, baseInterface, builder.ToImmutable()), SymbolKind.Method);
            });


            static void AddIfNotNull(ImmutableHashSet<ITypeSymbol>.Builder builder, INamedTypeSymbol symbol)
            {
                if (symbol is not null)
                {
                    builder.Add(symbol);
                }
            }
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol baseInterface, ImmutableHashSet<ITypeSymbol> supportedTypes)
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

            if (!isIAddressableInterface || supportedTypes.Contains(symbol.ReturnType.OriginalDefinition))
                return;

            var syntaxReference = symbol.DeclaringSyntaxReferences;
            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(syntaxReference[0].SyntaxTree, syntaxReference[0].Span)));
        }
    }
}
