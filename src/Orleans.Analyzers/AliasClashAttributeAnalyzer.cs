using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Microsoft.CodeAnalysis.Simplification;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AliasClashAttributeAnalyzer : DiagnosticAnalyzer
{
    private readonly record struct AliasBag(string Name, Location Location, SyntaxNode Parent);

    private readonly ConcurrentBag<AliasBag> _typeBags = new();
    private readonly ConcurrentBag<AliasBag> _methodBags = new();

    public const string TypesRuleId = "ORLEANS0011";
    public const string MethodsRuleId = "ORLEANS0012";

    private static readonly DiagnosticDescriptor TypesRule = new(
        id: TypesRuleId,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        title: new LocalizableResourceString(nameof(Resources.AliasClashDetectedTitle_Types), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.AliasClashDetectedMessageFormat_Types), Resources.ResourceManager, typeof(Resources)),
        description: new LocalizableResourceString(nameof(Resources.AliasClashDetectedDescription_Types), Resources.ResourceManager, typeof(Resources)));

    private static readonly DiagnosticDescriptor MethodsRule = new(
       id: MethodsRuleId,
       category: "Usage",
       defaultSeverity: DiagnosticSeverity.Error,
       isEnabledByDefault: true,
       title: new LocalizableResourceString(nameof(Resources.AliasClashDetectedTitle_Methods), Resources.ResourceManager, typeof(Resources)),
       messageFormat: new LocalizableResourceString(nameof(Resources.AliasClashDetectedMessageFormat_Methods), Resources.ResourceManager, typeof(Resources)),
       description: new LocalizableResourceString(nameof(Resources.AliasClashDetectedDescription_Methods), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(TypesRule, MethodsRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(ctx =>
        {
            ctx.RegisterSyntaxNodeAction(CheckMethodSyntax, SyntaxKind.MethodDeclaration);

            ctx.RegisterSyntaxNodeAction(CheckTypeSyntax,
                SyntaxKind.InterfaceDeclaration,
                SyntaxKind.ClassDeclaration,
                SyntaxKind.StructDeclaration,
                SyntaxKind.RecordDeclaration,
                SyntaxKind.RecordStructDeclaration);

            ctx.RegisterCompilationEndAction(ReportDuplicates);
        });
    }

    private void CheckMethodSyntax(SyntaxNodeAnalysisContext context)
    {
        var methodDeclaration = (MethodDeclarationSyntax)context.Node;
        var attributes = GetAliasAttributes(methodDeclaration.AttributeLists);

        foreach (var attribute in attributes)
        {
            var bag = GetBag(attribute, context.SemanticModel, methodDeclaration.Parent);
            if (bag != default)
            {
                _methodBags.Add(bag);
            }
        }
    }

    private void CheckTypeSyntax(SyntaxNodeAnalysisContext context)
    {
        var typeDeclaration = (TypeDeclarationSyntax)context.Node;
        var attributes = GetAliasAttributes(typeDeclaration.AttributeLists);

        foreach (var attribute in attributes)
        {
            var bag = GetBag(attribute, context.SemanticModel, null);
            if (bag != default)
            {
                _typeBags.Add(bag);
            }
        }
    }

    private void ReportDuplicates(CompilationAnalysisContext context)
    {
        // types
        var typeDuplicates = _typeBags
            .GroupBy(alias => alias.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var item in typeDuplicates)
        {
            var bags = _typeBags.Where(x => x.Name == item);
            if (bags.Count() > 1)
            {
                foreach (var bag in bags)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: TypesRule,
                       location: bag.Location,
                       messageArgs: new object[] { bag.Name }));
                }
            }
        }

        // methods
        var methodDuplicates = _methodBags
           .GroupBy(alias => new { alias.Parent, alias.Name })
           .Where(group => group.Count() > 1)
           .Select(group => group.Key);

        foreach (var item in methodDuplicates)
        {
            var bags = _methodBags.Where(x => x.Name == item.Name && x.Parent == item.Parent);
            if (bags.Count() > 1)
            {
                var parentName = item.Parent is TypeDeclarationSyntax syntax ? syntax.Identifier.Text : null;
                foreach (var bag in bags)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: MethodsRule,
                       location: bag.Location,
                       messageArgs: new object[] { bag.Name, parentName }));
                }
            }
        }
    }

    private static AliasBag GetBag(AttributeSyntax attribute, SemanticModel semanticModel, SyntaxNode parent)
    {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
        if (argument is null || argument.Expression is not { } expression)
        {
            return default;
        }

        var constantValue = semanticModel.GetConstantValue(expression);
        return constantValue.HasValue && constantValue.Value is string aliasName ?
            new(aliasName, attribute.GetLocation(), parent) : default;
    }

    private static IEnumerable<AttributeSyntax> GetAliasAttributes(SyntaxList<AttributeListSyntax> attributeLists) =>
        attributeLists
           .SelectMany(attributeList => attributeList.Attributes)
           .Where(attribute => attribute.IsAttribute(Constants.AliasAttributeName));
}

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GenerateAliasAttributesCodeFix)), Shared]
public class AliasClashAttributeCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(AliasClashAttributeAnalyzer.TypesRuleId, AliasClashAttributeAnalyzer.MethodsRuleId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = context.Document.GetSyntaxRootAsync(context.CancellationToken).Result;
        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;
        var classDeclaration = root.FindToken(diagnosticSpan.Start).Parent
            .AncestorsAndSelf().OfType<ClassDeclarationSyntax>().First();

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Rename duplicate alias",
                createChangedSolution: c => RenameDuplicateAlias(context.Document.Project.Solution, classDeclaration, c),
                equivalenceKey: AliasClashAttributeAnalyzer.TypesRuleId),
            diagnostic);

        return Task.CompletedTask;
    }

    private static async Task<Solution> RenameDuplicateAlias(Solution solution, ClassDeclarationSyntax classDeclaration, CancellationToken cancellationToken)
    {
        var documentId = solution.GetDocumentIdsWithFilePath(classDeclaration.SyntaxTree.FilePath).FirstOrDefault();
        if (documentId is null) return solution;
        var semanticModel = await solution.GetDocument(documentId).GetSemanticModelAsync(cancellationToken);
        var aliasAttribute = classDeclaration.AttributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attribute => attribute.IsAttribute(Constants.AliasAttributeName));

        var aliasName = aliasAttribute.ArgumentList.Arguments.First().ToString();
        var newAliasName = GenerateUniqueAliasName(solution, aliasName);

        #region -----------------
        //var newAliasAttribute =
        //    Attribute(
        //        ParseName(Constants.AliasAttributeFullyQualifiedName))
        //            .WithArgumentList(
        //                AttributeArgumentList(
        //                    SeparatedList(new[]
        //                    {
        //                        AttributeArgument(
        //                            LiteralExpression(
        //                                SyntaxKind.StringLiteralExpression,
        //                                Literal(newAliasName)))
        //                    })));
        #endregion

        var newAliasAttribute =
           Attribute(
               ParseName(Constants.AliasAttributeFullyQualifiedName))
                   .WithArgumentList(
                       ParseAttributeArgumentList($"(\"{newAliasName}\")"))
                           .WithAdditionalAnnotations(Simplifier.Annotation);

        var newClassDeclaration = classDeclaration.ReplaceNode(aliasAttribute, newAliasAttribute);
        var newRoot = await classDeclaration.SyntaxTree.GetRootAsync(cancellationToken);
        newRoot = newRoot.ReplaceNode(classDeclaration, newClassDeclaration);

        return solution.WithDocumentSyntaxRoot(documentId, newRoot);
    }

    private static string GenerateUniqueAliasName(Solution solution, string aliasName)
    {
        var suffix = 1;
        var newAliasName = aliasName;

        while (AliasNameExistsInSolution(solution, newAliasName))
        {
            newAliasName = $"{aliasName}{suffix}";
            suffix++;
        }

        return newAliasName;
    }

    private static bool AliasNameExistsInSolution(Solution solution, string aliasName)
    {
        foreach (var projectId in solution.ProjectIds)
        {
            var project = solution.GetProject(projectId);

            foreach (var documentId in project.DocumentIds)
            {
                if (!solution.GetDocument(documentId).TryGetSyntaxRoot(out var root))
                {
                    continue;
                }

                var aliasAttributes = root.DescendantNodes().OfType<AttributeSyntax>()
                    .Where(attribute => attribute.IsAttribute(Constants.AliasAttributeName));

                foreach (var attribute in aliasAttributes)
                {
                    var attributeArgument = attribute.ArgumentList.Arguments.First();
                    var aliasValue = attributeArgument.Expression.ToString();

                    if (string.Equals(aliasValue, aliasName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}