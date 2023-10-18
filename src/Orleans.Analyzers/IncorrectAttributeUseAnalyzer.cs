using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IncorrectAttributeUseAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0013";

    private static readonly DiagnosticDescriptor Rule = new(
       id: RuleId,
       category: "Usage",
       defaultSeverity: DiagnosticSeverity.Error,
       isEnabledByDefault: true,
       title: new LocalizableResourceString(nameof(Resources.IncorrectAttributeUseTitle), Resources.ResourceManager, typeof(Resources)),
       messageFormat: new LocalizableResourceString(nameof(Resources.IncorrectAttributeUseMessageFormat), Resources.ResourceManager, typeof(Resources)),
       description: new LocalizableResourceString(nameof(Resources.IncorrectAttributeUseTitleDescription), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSyntaxNodeAction(CheckSyntaxNode, SyntaxKind.ClassDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ClassDeclarationSyntax) return;

        var classDeclaration = (ClassDeclarationSyntax)context.Node;

        if (!classDeclaration.InheritsGrainClass(context.SemanticModel))
        {
            return;
        }

        TryReportFor(Constants.AliasAttributeName, context, classDeclaration);
        TryReportFor(Constants.GenerateSerializerAttributeName, context, classDeclaration);
    }

    private static void TryReportFor(string attributeTypeName, SyntaxNodeAnalysisContext context, ClassDeclarationSyntax classDeclaration)
    {
        if (classDeclaration.TryGetAttribute(attributeTypeName, out var attribute))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: Rule,
                location: attribute.GetLocation(),
                messageArgs: new object[] { attributeTypeName }));
        }
    }
}