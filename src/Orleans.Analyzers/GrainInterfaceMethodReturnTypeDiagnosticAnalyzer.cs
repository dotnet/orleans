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
        private static readonly (string[] Namespace, string MetadataName)[] SupportedReturnTypes = new[]
        {
            (new [] { "System", "Threading", "Tasks" }, "Task"),
            (new [] { "System", "Threading", "Tasks" }, "Task`1"),
            (new [] { "System", "Threading", "Tasks" }, "ValueTask"),
            (new [] { "System", "Threading", "Tasks" }, "ValueTask`1"),
            (new [] { "System" }, "Void")
        };
        public const string DiagnosticId = "ORLEANS0009";
        public const string Title = "Grain interfaces methods must return a compatible type";
        public const string MessageFormat = $"Grain interfaces methods must return a compatible type, such as Task, Task<T>, ValueTask, ValueTask<T>, or void";
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeSyntax, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeSyntax(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is not MethodDeclarationSyntax syntax) return;

            var symbol = context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken);

            if (symbol.ContainingType.TypeKind != TypeKind.Interface) return;

            var isIAddressableInterface = false;
            foreach (var implementedInterface in symbol.ContainingType.AllInterfaces)
            {
                if (BaseInterfaceName.Equals(implementedInterface.ToDisplayString(NullableFlowState.None), StringComparison.Ordinal))
                {
                    isIAddressableInterface = true;
                    break;
                }
            }

            if (!isIAddressableInterface) return;

            var isSupportedType = false;
            var returnType = symbol.ReturnType switch
            {
                INamedTypeSymbol { IsGenericType: true } generic => generic.ConstructedFrom,
                { } type => type,
            };

            if (returnType.ContainingNamespace is { } returnTypeNs)
            {
                foreach (var allowedReturnType in SupportedReturnTypes)
                {
                    var (ns, metadataName) = allowedReturnType;
                    if (metadataName.Equals(returnType.MetadataName, StringComparison.Ordinal) && NamespacesEqual(returnTypeNs, ns.AsSpan()))
                    {
                        isSupportedType = true;
                        break;
                    }
                }
            }

            if (isSupportedType) return;

            var syntaxReference = symbol.DeclaringSyntaxReferences;
            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(syntaxReference[0].SyntaxTree, syntaxReference[0].Span)));
        }

        private static bool NamespacesEqual(INamespaceSymbol left, ReadOnlySpan<string> right)
        {
            if (right.Length == 0) return left.IsGlobalNamespace;
            if (left.IsGlobalNamespace) return false;
            if (!string.Equals(left.Name, right[right.Length - 1], StringComparison.Ordinal)) return false;

            return NamespacesEqual(left.ContainingNamespace, right.Slice(0, right.Length - 1));
        }
    }
}
