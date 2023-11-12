using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Composition;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GenerateAliasAttributesCodeFix)), Shared]
public class GenerateAliasAttributesCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(GenerateAliasAttributesAnalyzer.RuleId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            if (node != null)
            {
                // Check if its an interface method
                var methodDeclaration = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (methodDeclaration != null)
                {
                    await FixFor(context, diagnostic, methodDeclaration);
                    continue;
                }

                // Check if its a type declaration (interface itself, class, struct, record)
                var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeDeclaration != null)
                {
                    await FixFor(context, diagnostic, typeDeclaration);
                    continue;
                }
            }
        }
    }

    private static async Task FixFor(CodeFixContext context, Diagnostic diagnostic, SyntaxNode declaration)
    {
        var documentEditor = await DocumentEditor.CreateAsync(context.Document, context.CancellationToken);

        var arityString = diagnostic.Properties["Arity"] switch
        {
            null or "0" => "",
            string value => $"`{value}"
        };
        var typeName = diagnostic.Properties["TypeName"];
        var ns = diagnostic.Properties["NamespaceAndNesting"] switch
        {
            { Length: > 0 } value => $"{value}.",
            _ => ""
        };

        var aliasAttribute =
            Attribute(
                ParseName(Constants.AliasAttributeFullyQualifiedName))
                    .WithArgumentList(
                        ParseAttributeArgumentList($"(\"{ns}{typeName}{arityString}\")"))
                            .WithAdditionalAnnotations(Simplifier.Annotation);

        documentEditor.AddAttribute(declaration, aliasAttribute);
        var updatedDocument = documentEditor.GetChangedDocument();

        context.RegisterCodeFix(
            action: CodeAction.Create(
                Resources.AddAliasAttributesTitle,
                createChangedDocument: ct => Task.FromResult(updatedDocument),
                equivalenceKey: GenerateAliasAttributesAnalyzer.RuleId),
            diagnostic: diagnostic);
    }
}
