using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IncorrectAliasUseAttributeAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0013";

    private static readonly DiagnosticDescriptor Rule = new(
       id: RuleId,
       category: "Usage",
       defaultSeverity: DiagnosticSeverity.Warning,
       isEnabledByDefault: true,
       title: new LocalizableResourceString(nameof(Resources.IncorrectAliasUseTitle), Resources.ResourceManager, typeof(Resources)),
       messageFormat: new LocalizableResourceString(nameof(Resources.IncorrectAliasUseMessageFormat), Resources.ResourceManager, typeof(Resources)),
       description: new LocalizableResourceString(nameof(Resources.IncorrectAliasUseTitleDescription), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSyntaxNodeAction(CheckSyntaxNode, SyntaxKind.ClassDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;

        if (!classDeclaration.HasAttribute(Constants.AliasAttributeName))
        {
            return;
        }

        if (!classDeclaration.InheritsGrainClass(context.SemanticModel))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor:Rule,
            location: classDeclaration.GetLocation(),
            messageArgs: new object[] { classDeclaration.Identifier.Text }));
    }
}
