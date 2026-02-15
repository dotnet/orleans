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

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
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
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(context =>
        {
            var generateSerializerAttribute = context.Compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
            var idAttribute = context.Compilation.GetTypeByMetadataName("Orleans.IdAttribute");
            if (generateSerializerAttribute is not null && idAttribute is not null)
            {
                context.RegisterSymbolAction(context => AnalyzeNamedType(context, generateSerializerAttribute, idAttribute), SymbolKind.NamedType);
            }
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol generateSerializerAttribute, INamedTypeSymbol idAttribute)
    {
        var typeSymbol = (INamedTypeSymbol)context.Symbol;
        if (!typeSymbol.HasAttribute(generateSerializerAttribute))
        {
            return;
        }

        List<AttributeArgumentBag<uint>> bags = [];
        foreach (var member in typeSymbol.GetMembers())
        {
            foreach (var attribute in member.GetAttributes())
            {
                if (!idAttribute.Equals(attribute.AttributeClass, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length == 1 &&
                    attribute.ConstructorArguments[0].Value is uint idValue)
                {
                    var attributeSyntax = (AttributeSyntax)attribute.ApplicationSyntaxReference.GetSyntax();
                    bags.Add(new AttributeArgumentBag<uint>(idValue, attributeSyntax.GetLocation()));
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
