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

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
            context.RegisterSyntaxNodeAction(CheckSyntaxNode, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
        }

        private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is TypeDeclarationSyntax declaration && !declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                if (declaration.TryGetAttribute(Constants.GenerateSerializerAttributeName, out var attribute))
                {
                    var analysis = SerializationAttributesHelper.AnalyzeTypeDeclaration(declaration);
                    if (analysis.UnannotatedMembers.Count > 0)
                    {
                        // Check if GenerateFieldIds is set to PublicProperties
                        var generateFieldIds = GetGenerateFieldIdsValue(attribute);
                        if (generateFieldIds != GenerateFieldIds.PublicProperties)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule, attribute.GetLocation()));
                        }
                    }
                }
            }
        }

        private static GenerateFieldIds GetGenerateFieldIdsValue(AttributeSyntax attribute)
        {
            if (attribute.ArgumentList == null)
            {
                return GenerateFieldIds.None;
            }

            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                if (argument.NameEquals?.Name.Identifier.Text == "GenerateFieldIds")
                {
                    if (argument.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        var memberName = memberAccess.Name.Identifier.Text;
                        if (memberName == "PublicProperties")
                        {
                            return GenerateFieldIds.PublicProperties;
                        }
                    }
                }
            }

            return GenerateFieldIds.None;
        }

        private enum GenerateFieldIds
        {
            None,
            PublicProperties
        }
    }
}
