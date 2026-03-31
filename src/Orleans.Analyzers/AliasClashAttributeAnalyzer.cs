using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Globalization;

namespace Orleans.Analyzers;

#nullable disable
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AliasClashAttributeAnalyzer : DiagnosticAnalyzer
{
    private readonly record struct TypeAliasInfo(string TypeName, Location Location);

    public const string RuleId = "ORLEANS0011";

    private static readonly DiagnosticDescriptor Rule = new(
        id: RuleId,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        title: new LocalizableResourceString(nameof(Resources.AliasClashDetectedTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.AliasClashDetectedMessageFormat), Resources.ResourceManager, typeof(Resources)),
        description: new LocalizableResourceString(nameof(Resources.AliasClashDetectedDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: null,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(context =>
        {
            var aliasMap = new ConcurrentDictionary<string, ConcurrentBag<TypeAliasInfo>>();
            var aliasAttributeSymbol = context.Compilation.GetTypeByMetadataName("Orleans.AliasAttribute");
            context.RegisterSymbolAction(
                context => CollectTypeAliases(context, aliasMap, aliasAttributeSymbol),
                SymbolKind.NamedType);

            // We can immediately check duplicate method‐aliases in grain interfaces.
            context.RegisterSymbolAction(
                context => CheckMethodAliases(context, aliasMap, aliasAttributeSymbol),
                SymbolKind.NamedType);

            // Only at the very end, we do one single‐threaded scan for type‐alias clashes only.
            context.RegisterCompilationEndAction(context =>
            {
                foreach (var kvp in aliasMap)
                {
                    var alias = kvp.Key;
                    var infos = kvp.Value;

                    var distinctTypes = infos
                        .Select(i => i.TypeName)
                        .Distinct()
                        .ToList();

                    if (distinctTypes.Count <= 1)
                    {
                        continue; // If more than one different type claimed it.
                    }

                    var firstType = distinctTypes[0];
                    foreach (var info in infos.Where(i => i.TypeName != firstType))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule, info.Location, alias, firstType));
                    }
                }
            });
        });
    }

    private static void CollectTypeAliases(
        SymbolAnalysisContext context,
        ConcurrentDictionary<string, ConcurrentBag<TypeAliasInfo>> aliasMap,
        INamedTypeSymbol aliasAttributeSymbol)
    {
        var typeSymbol = (INamedTypeSymbol)context.Symbol;
        
        if (typeSymbol.TypeKind == TypeKind.Interface && !typeSymbol.ExtendsGrainInterface())
        {
            return; // Skip interfaces that dont extend IAddressable
        }

        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (!aliasAttributeSymbol.Equals(attr.AttributeClass, SymbolEqualityComparer.Default))
                continue;

            var alias = attr.ConstructorArguments.FirstOrDefault().Value as string;
            if (string.IsNullOrEmpty(alias))
                continue;

            var info = new TypeAliasInfo(typeSymbol.ToDisplayString(), attr.ApplicationSyntaxReference.GetSyntax().GetLocation());

            aliasMap.AddOrUpdate(
                key: alias,
                addValueFactory: _ => new ConcurrentBag<TypeAliasInfo>([info]),
                updateValueFactory: (_, bag) =>
                {
                    bag.Add(info);
                    return bag;
                });
        }
    }

    private static void CheckMethodAliases(
        SymbolAnalysisContext context,
        ConcurrentDictionary<string, ConcurrentBag<TypeAliasInfo>> aliasMap,
        INamedTypeSymbol aliasAttributeSymbol)
    {
        if (context.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceSymbol)
        {
            return;
        }

        var methodBags = new List<(string Alias, Location Location)>();

        foreach (var method in interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            foreach (var attr in method.GetAttributes())
            {
                if (!aliasAttributeSymbol.Equals(attr.AttributeClass, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var alias = attr.ConstructorArguments.FirstOrDefault().Value as string;
                if (!string.IsNullOrEmpty(alias))
                {
                    methodBags.Add((alias, attr.ApplicationSyntaxReference.GetSyntax().GetLocation()));
                }
            }
        }

        // Find duplicate aliases within the interface's methods.
        var duplicateMethodAliases = methodBags
            .GroupBy(x => x.Alias)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicateMethodAliases)
        {
            var duplicates = group.Skip(1).ToList();
            var (prefix, suffix) = ParsePrefixAndNumericSuffix(group.Key);

            foreach (var duplicate in duplicates)
            {
                string newAlias;
                do
                {
                    suffix++;
                    newAlias = $"{prefix}{suffix}";
                }
                while (aliasMap.ContainsKey(newAlias) || methodBags.Any(b => b.Alias == newAlias));

                var properties = ImmutableDictionary.CreateBuilder<string, string>();

                properties.Add("AliasName", prefix);
                properties.Add("AliasSuffix", suffix.ToString(CultureInfo.InvariantCulture));

                context.ReportDiagnostic(Diagnostic.Create(Rule, duplicate.Location, properties, group.Key));
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

        return (
            input.Substring(0, input.Length - suffixLength),
            ulong.Parse(input.Substring(input.Length - suffixLength), CultureInfo.InvariantCulture)
        );
    }

    private static int GetNumericSuffixLength(string input)
    {
        var suffixLength = 0;
        for (var i = input.Length - 1; i >= 0; --i)
        {
            if (!char.IsDigit(input[i]))
                break;
            suffixLength++;
        }
        return suffixLength;
    }
}
