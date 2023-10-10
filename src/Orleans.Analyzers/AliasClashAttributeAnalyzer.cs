using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AliasClashAttributeAnalyzer : DiagnosticAnalyzer
{
    private readonly record struct AliasBag(string Name, Location Location);

    public const string RuleId = "ORLEANS0011";
    private const string Category = "Usage";

    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AliasClashDetectedTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AliasClashDetectedMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AliasClashDetectedDescription), Resources.ResourceManager, typeof(Resources));

    private static readonly DiagnosticDescriptor Rule = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    private static readonly ConcurrentBag<AliasBag> _aliasBags = new();

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

        context.RegisterCompilationStartAction(ctx =>
        {
            ctx.RegisterSyntaxNodeAction(CheckSyntaxNode,
                SyntaxKind.InterfaceDeclaration,
                SyntaxKind.ClassDeclaration,
                SyntaxKind.StructDeclaration,
                SyntaxKind.RecordDeclaration,
                SyntaxKind.RecordStructDeclaration);

            ctx.RegisterCompilationEndAction(CheckDuplicates);
        });
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        var typeDeclaration = (TypeDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        var aliasAttributes = typeDeclaration.AttributeLists
            .SelectMany(attributeList => attributeList.Attributes)
            .Where(attribute => attribute.IsAttribute(Constants.AliasAttributeName))
            .ToList();

        foreach (var attribute in aliasAttributes)
        {
            var bag = GetBag(attribute, semanticModel);
            if (bag != default)
            {
                _aliasBags.Add(bag);
            }
        }
    }

    private static AliasBag GetBag(AttributeSyntax attribute, SemanticModel semanticModel)
    {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
        if (argument?.Expression is not { } expression)
        {
            return default;
        }

        var constantValue = semanticModel.GetConstantValue(expression);
        return constantValue.HasValue && constantValue.Value is string aliasName ?
            new(aliasName, argument.GetLocation()) : default;
    }

    private void CheckDuplicates(CompilationAnalysisContext context)
    {
        var duplicateAliases = _aliasBags
            .GroupBy(alias => alias.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        foreach (var alias in duplicateAliases)
        {
            var bags = _aliasBags
                .Where(item => item.Name == alias)
                .ToList();

            if (bags.Count > 1)
            {
                foreach (var bag in bags)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        descriptor: Rule,
                        location: bag.Location,
                        messageArgs: new object[] { bag.Name, bags.Count },
                        properties: null));
                }
            }
        }
    }
}