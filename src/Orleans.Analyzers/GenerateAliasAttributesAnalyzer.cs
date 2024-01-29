using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GenerateAliasAttributesAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0010";
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AddAliasAttributesTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AddAliasMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AddAliasAttributesDescription), Resources.ResourceManager, typeof(Resources));

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
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
            if (!interfaceDeclaration.ExtendsGrainInterface(context.SemanticModel))
            {
                return;
            }

            if (!interfaceDeclaration.HasAttribute(Constants.AliasAttributeName))
            {
                ReportFor(
                    context,
                    interfaceDeclaration.GetLocation(),
                    interfaceDeclaration.Identifier.ToString(),
                    GetArity(interfaceDeclaration),
                    GetNamespaceAndNesting(interfaceDeclaration));
            }

            foreach (var methodDeclaration in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (methodDeclaration.IsStatic())
                {
                    continue;
                }

                if (!methodDeclaration.HasAttribute(Constants.AliasAttributeName))
                {
                    ReportFor(context, methodDeclaration.GetLocation(), methodDeclaration.Identifier.ToString(), arity: 0, namespaceAndNesting: null);
                }
            }

            return;
        }

        // Rest of types: class, struct, record
        if (context.Node is TypeDeclarationSyntax { } typeDeclaration)
        {
            if (typeDeclaration is ClassDeclarationSyntax classDeclaration &&
                classDeclaration.InheritsGrainClass(context.SemanticModel))
            {
                return;
            }

            if (!typeDeclaration.HasAttribute(Constants.GenerateSerializerAttributeName))
            {
                return;
            }

            if (typeDeclaration.HasAttribute(Constants.AliasAttributeName))
            {
                return;
            }

            ReportFor(
                context,
                typeDeclaration.GetLocation(),
                typeDeclaration.Identifier.ToString(),
                GetArity(typeDeclaration),
                GetNamespaceAndNesting(typeDeclaration));
        }
    }

    private static int GetArity(TypeDeclarationSyntax typeDeclarationSyntax)
    {
        var node = typeDeclarationSyntax;
        int arity = 0;
        while (node is TypeDeclarationSyntax type)
        {
            arity += type.Arity;
            node = type.Parent as TypeDeclarationSyntax;
        }

        return arity;
    }

    private static string GetNamespaceAndNesting(TypeDeclarationSyntax typeDeclarationSyntax)
    {
        SyntaxNode node = typeDeclarationSyntax.Parent;
        StringBuilder sb = new();
        Stack<string> segments = new();

        while (node is not null)
        {
            if (node is TypeDeclarationSyntax type)
            {
                segments.Push(type.Identifier.ToString());
            }
            else if (node is BaseNamespaceDeclarationSyntax ns)
            {
                segments.Push(ns.Name.ToString());
            }

            node = node.Parent;
        }

        foreach (var segment in segments)
        {
            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(segment);
        }

        return sb.ToString();
    }

    private static void ReportFor(SyntaxNodeAnalysisContext context, Location location, string typeName, int arity, string namespaceAndNesting)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>();

        builder.Add("TypeName", typeName);
        builder.Add("NamespaceAndNesting", namespaceAndNesting);
        builder.Add("Arity", arity.ToString(System.Globalization.CultureInfo.InvariantCulture));

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            properties: builder.ToImmutable()));
    }
}
