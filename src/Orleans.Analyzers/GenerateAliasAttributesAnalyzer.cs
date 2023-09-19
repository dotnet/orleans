using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers;

//TODO:
// * Grain Interfaces - x
// * Interface Methods - x
// * Grain Classes (ones that inherited from Grain)
// * Classes, Structs, Enums that have [GenerateSerializer]

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GenerateAliasAttributesAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0010";
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AddAliasAttributesTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AddAliasMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AddAliasAttributesDescription), Resources.ResourceManager, typeof(Resources));

    private static readonly DiagnosticDescriptor Rule = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.RegisterSyntaxNodeAction(CheckSyntaxNode,
            SyntaxKind.InterfaceDeclaration);
            //SyntaxKind.ClassDeclaration,
            //SyntaxKind.StructDeclaration,
            //SyntaxKind.RecordDeclaration,
            //SyntaxKind.RecordStructDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is InterfaceDeclarationSyntax { } interfaceDeclaration)
        {
            if (!IsGrainInterface(ref context, interfaceDeclaration))
            {
                return;
            }

            var interfaceSymbol = context.SemanticModel.GetDeclaredSymbol(interfaceDeclaration, context.CancellationToken);
            if (!HasAliasAttribute(ref context, interfaceSymbol))
            {
                Report(ref context, interfaceDeclaration.GetLocation());
            }

            foreach (var methodDeclaration in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDeclaration, context.CancellationToken);
                if (methodSymbol.IsStatic)
                {
                    continue;
                }

                if (!HasAliasAttribute(ref context, methodSymbol))
                {
                    Report(ref context, methodDeclaration.GetLocation());
                }                
            }

            return;
        }
    }

    private static bool IsGrainInterface(ref SyntaxNodeAnalysisContext context, InterfaceDeclarationSyntax syntax)
        => context.SemanticModel
                .GetDeclaredSymbol(syntax, context.CancellationToken)
                .ExtendsGrainInterface();
    private static bool HasAliasAttribute(ref SyntaxNodeAnalysisContext context, ISymbol symbol)
    {
        var aliasSymbol = context.Compilation.GetTypeByMetadataName(Constants.AliasAttributeFullyQualifiedName);
        return symbol.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, aliasSymbol));
    }

    private static void Report(ref SyntaxNodeAnalysisContext context, Location location)
       => context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: Rule,
                       location: location,
                       messageArgs: new object[] { }));
}
