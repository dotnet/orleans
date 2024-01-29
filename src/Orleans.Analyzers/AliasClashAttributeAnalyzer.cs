using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public partial class AliasClashAttributeAnalyzer : DiagnosticAnalyzer
{
    private readonly record struct AliasBag(string Name, Location Location);

    public const string RuleId = "ORLEANS0011";

    private static readonly DiagnosticDescriptor Rule = new(
       id: RuleId,
       category: "Usage",
       defaultSeverity: DiagnosticSeverity.Error,
       isEnabledByDefault: true,
       title: new LocalizableResourceString(nameof(Resources.AliasClashDetectedTitle), Resources.ResourceManager, typeof(Resources)),
       messageFormat: new LocalizableResourceString(nameof(Resources.AliasClashDetectedMessageFormat), Resources.ResourceManager, typeof(Resources)),
       description: new LocalizableResourceString(nameof(Resources.AliasClashDetectedDescription), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSyntaxNodeAction(CheckSyntaxNode, SyntaxKind.InterfaceDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        var interfaceDeclaration = (InterfaceDeclarationSyntax)context.Node;
        if (!interfaceDeclaration.ExtendsGrainInterface(context.SemanticModel))
        {
            return;
        }

        List<AttributeArgumentBag<string>> bags = new();
        foreach (var methodDeclaration in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            var attributes = methodDeclaration.AttributeLists.GetAttributeSyntaxes(Constants.AliasAttributeName);
            foreach (var attribute in attributes)
            {
                var bag = attribute.GetArgumentBag<string>(context.SemanticModel);
                if (bag != default)
                {
                    bags.Add(bag);
                }
            }
        }

        var duplicateAliases = bags
           .GroupBy(alias => alias.Value)
           .Where(group => group.Count() > 1)
           .Select(group => group.Key);

        if (!duplicateAliases.Any())
        {
            return;
        }

        foreach (var duplicateAlias in duplicateAliases)
        {
            var filteredBags = bags.Where(x => x.Value == duplicateAlias);
            var duplicateCount = filteredBags.Count();

            if (duplicateCount > 1)
            {
                var (prefix, suffix) = ParsePrefixAndNumericSuffix(duplicateAlias);

                filteredBags = filteredBags.Skip(1);

                foreach (var bag in filteredBags)
                {
                    string newAlias;
                    do
                    {
                        ++suffix;
                        newAlias = $"{prefix}{suffix}";
                    } while (bags.Any(x => x.Value.Equals(newAlias, StringComparison.Ordinal)));

                    var builder = ImmutableDictionary.CreateBuilder<string, string>();

                    builder.Add("AliasName", prefix);
                    builder.Add("AliasSuffix", suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: Rule,
                       location: bag.Location,
                       properties: builder.ToImmutable()));

                    suffix++;
                }
            }
        }
    }

    private static (string Prefix, ulong Suffix) ParsePrefixAndNumericSuffix(string input)
    {
        var suffixLength = GetNumericSuffixLength(input);
        if (suffixLength == 0)
        {
            return (input, 0);
        }

        return (input.Substring(0, input.Length - suffixLength), ulong.Parse(input.Substring(input.Length - suffixLength)));
    }

    private static int GetNumericSuffixLength(string input)
    {
        var suffixLength = 0;
        for (var c = input.Length - 1; c > 0; --c)
        {
            if (!char.IsDigit(input[c]))
            {
                break;
            }

            ++suffixLength;
        }

        return suffixLength;
    }
}