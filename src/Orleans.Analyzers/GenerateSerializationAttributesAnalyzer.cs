using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class GenerateSerializationAttributesAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "ORLEANS0004";
        private const string Category = "Usage";
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AddSerializationAttributesTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AddSerializationAttributesMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AddSerializationAttributesDescription), Resources.ResourceManager, typeof(Resources));

        internal static DiagnosticDescriptor Rule { get; } = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
            context.RegisterSyntaxNodeAction(CheckSyntaxNode, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration);
        }

        private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is TypeDeclarationSyntax declaration && !declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                if (declaration.TryGetAttribute(Constants.GenerateSerializerAttributeName, out var attribute))
                {
                    var (serializableMembers, _, nextId) = SerializationAttributesHelper.AnalyzeTypeDeclaration(declaration);
                    if (serializableMembers.Count > 0)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule, attribute.GetLocation()));
                    }
                }
            }
        }
    }
}
