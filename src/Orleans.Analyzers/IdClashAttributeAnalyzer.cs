using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IdClashAttributeAnalyzer : DiagnosticAnalyzer
{
    private readonly record struct AliasBag(string Name, Location Location);

    public const string RuleId = "ORLEANS0012";

    private static readonly DiagnosticDescriptor Rule = new(
       id: RuleId,
       category: "Usage",
       defaultSeverity: DiagnosticSeverity.Error,
       isEnabledByDefault: true,
       title: new LocalizableResourceString(nameof(Resources.IdClashDetectedTitle), Resources.ResourceManager, typeof(Resources)),
       messageFormat: new LocalizableResourceString(nameof(Resources.IdClashDetectedMessageFormat), Resources.ResourceManager, typeof(Resources)),
       description: new LocalizableResourceString(nameof(Resources.IdClashDetectedDescription), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSyntaxNodeAction(CheckSyntaxNode,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        var typeDeclaration = context.Node as TypeDeclarationSyntax;
        if (!typeDeclaration.HasAttribute(Constants.GenerateSerializerAttributeName))
        {
            return;
        }

        List<AttributeArgumentBag<int>> bags = [];
        foreach (var memberDeclaration in typeDeclaration.Members.OfType<MemberDeclarationSyntax>())
        {
            var attributes = memberDeclaration.AttributeLists.GetAttributeSyntaxes(Constants.IdAttributeName);
            foreach (var attribute in attributes)
            {
                var bag = attribute.GetArgumentBag<int>(context.SemanticModel);
                if (bag != default)
                {
                    bags.Add(bag);
                }
            }
        }

        var duplicateIds = bags
           .GroupBy(id => id.Value)
           .Where(group => group.Count() > 1)
           .Select(group => group.Key);

        if (!duplicateIds.Any())
        {
            return;
        }

        foreach (var duplicateId in duplicateIds)
        {
            var filteredBags = bags.Where(x => x.Value == duplicateId);
            var duplicateCount = filteredBags.Count();

            if (duplicateCount > 1)
            {
                foreach (var bag in filteredBags)
                {
                    var builder = ImmutableDictionary.CreateBuilder<string, string>();

                    builder.Add("IdValue", bag.Value.ToString());

                    context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: Rule,
                       location: bag.Location,
                       properties: builder.ToImmutable()));
                }
            }
        }
    }
}
