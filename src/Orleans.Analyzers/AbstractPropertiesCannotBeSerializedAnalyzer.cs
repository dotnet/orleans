using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AbstractPropertiesCannotBeSerializedAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "ORLEANS0006";
        private const string Category = "Usage";
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AbstractOrStaticMembersCannotBeSerializedTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AbstractOrStaticMembersCannotBeSerializedMessageFormat), Resources.ResourceManager, typeof(Resources));

        internal static DiagnosticDescriptor Rule { get; } = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
            context.RegisterSyntaxNodeAction(CheckSyntaxNode, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration);
        }

        private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is TypeDeclarationSyntax declaration && SerializationAttributesHelper.ShouldGenerateSerializer(declaration))
            {
                var (_, members, _) = SerializationAttributesHelper.AnalyzeTypeDeclaration(declaration);
                foreach (var member in members)
                {
                    string modifier = null;
                    if (member.IsAbstract())
                    {
                        modifier = "abstract";
                    }
                    else if (member.IsStatic())
                    {
                        modifier = "static";
                    }

                    if (modifier is not null)
                    {
                        var location = member.GetLocation();
                        if (member.TryGetAttribute(Constants.IdAttributeName, out var attribute))
                        {
                            location = attribute.GetLocation();
                        }

                        var name = member.GetMemberNameOrDefault();
                        context.ReportDiagnostic(Diagnostic.Create(Rule, location, name, modifier));
                    }
                }
            }
        }
    }
}
