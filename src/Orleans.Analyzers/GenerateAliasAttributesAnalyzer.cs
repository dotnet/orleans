using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers;

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
            SyntaxKind.InterfaceDeclaration,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        // Interface types and their methods
        if (context.Node is InterfaceDeclarationSyntax { } interfaceDeclaration)
        {
            if (!context.SemanticModel
                .GetDeclaredSymbol(interfaceDeclaration, context.CancellationToken)
                .ExtendsGrainInterface())
            {
                return;
            }

            if (!interfaceDeclaration.HasAttribute(Constants.AliasAttributeName))
            {
                ReportFor(context, interfaceDeclaration.GetLocation(), interfaceDeclaration.Identifier.ToString());
            }

            foreach (var methodDeclaration in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (methodDeclaration.IsStatic())
                {
                    continue;
                }

                if (!methodDeclaration.HasAttribute(Constants.AliasAttributeName))
                {
                    ReportFor(context, methodDeclaration.GetLocation(), methodDeclaration.Identifier.ToString());
                }                
            }

            return;
        }

        // Rest of types: class, struct, record
        if (context.Node is TypeDeclarationSyntax { } typeDeclaration)
        {
            if (!typeDeclaration.HasAttribute(Constants.GenerateSerializerAttributeName))
            {
                return;
            }

            if (typeDeclaration.HasAttribute(Constants.AliasAttributeName))
            {
                return;
            }

            ReportFor(context, typeDeclaration.GetLocation(), typeDeclaration.Identifier.ToString());
        }
    }

    private static void ReportFor(SyntaxNodeAnalysisContext context, Location location, string typeName)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>();

        builder.Add("TypeName", typeName);

        context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: Rule,
                       location: location,
                       properties: builder.ToImmutable()));
    }
}
