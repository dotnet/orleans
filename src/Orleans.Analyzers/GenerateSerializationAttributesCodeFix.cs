using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers
{
    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class GenerateOrleansSerializationAttributesCodeFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(GenerateSerializationAttributesAnalyzer.RuleId, GenerateGenerateSerializerAttributeAnalyzer.RuleId);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
            var declaration = root.FindNode(context.Span).FirstAncestorOrSelf<TypeDeclarationSyntax>();
            foreach (var diagnostic in context.Diagnostics)
            {
                switch (diagnostic.Id)
                {
                    case GenerateSerializationAttributesAnalyzer.RuleId:
                        context.RegisterCodeFix(
                            CodeAction.Create("Generate serialization attributes", cancellationToken => AddSerializationAttributes(declaration, context, cancellationToken), equivalenceKey: GenerateSerializationAttributesAnalyzer.RuleId),
                            diagnostic);
                        context.RegisterCodeFix(
                            CodeAction.Create("Mark properties and fields [NonSerialized]", cancellationToken => AddNonSerializedAttributes(root, declaration, context, cancellationToken), equivalenceKey: GenerateSerializationAttributesAnalyzer.RuleId + "NonSerialized"),
                            diagnostic);
                        break;
                    case GenerateGenerateSerializerAttributeAnalyzer.RuleId:
                        context.RegisterCodeFix(
                            CodeAction.Create("Add [GenerateSerializer] attribute", cancellationToken => AddGenerateSerializerAttribute(declaration, context, cancellationToken), equivalenceKey: GenerateGenerateSerializerAttributeAnalyzer.RuleId),
                            diagnostic);
                        break;
                }
            }
        }

        private static async Task<Document> AddGenerateSerializerAttribute(TypeDeclarationSyntax declaration, CodeFixContext context, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(context.Document, cancellationToken).ConfigureAwait(false);

            // Add the [GenerateSerializer] attribute
            var attribute = Attribute(ParseName(Constants.GenerateSerializerAttributeFullyQualifiedName))
                .WithAdditionalAnnotations(Simplifier.Annotation);
            editor.AddAttribute(declaration, attribute);
            return editor.GetChangedDocument();
        }

        private static async Task<Document> AddSerializationAttributes(TypeDeclarationSyntax declaration, CodeFixContext context, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(context.Document, cancellationToken).ConfigureAwait(false);
            var analysis = SerializationAttributesHelper.AnalyzeTypeDeclaration(declaration);

            var nextId = analysis.NextAvailableId;
            foreach (var member in analysis.UnannotatedMembers)
            {
                // Add the [Id(x)] attribute
                var attribute = Attribute(ParseName(Constants.IdAttributeFullyQualifiedName))
                    .AddArgumentListArguments(AttributeArgument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal((int)nextId++))))
                    .WithAdditionalAnnotations(Simplifier.Annotation);
                editor.AddAttribute(member, attribute);
            }

            return editor.GetChangedDocument();
        }

        private static async Task<Document> AddNonSerializedAttributes(SyntaxNode root, TypeDeclarationSyntax declaration, CodeFixContext context, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(context.Document, cancellationToken).ConfigureAwait(false);
            var analysis = SerializationAttributesHelper.AnalyzeTypeDeclaration(declaration);

            foreach (var member in analysis.UnannotatedMembers)
            {
                // Add the [NonSerialized] attribute
                var attribute = AttributeList().AddAttributes(Attribute(ParseName(Constants.NonSerializedAttributeFullyQualifiedName)).WithAdditionalAnnotations(Simplifier.Annotation));

                // Since [NonSerialized] is a field-only attribute, add the field target specifier.
                if (member is PropertyDeclarationSyntax)
                {
                    attribute = attribute.WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.FieldKeyword)));
                }

                editor.AddAttribute(member, attribute);
            }

            var document = editor.GetChangedDocument();
            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var insertUsingDirective = true;
            if (root is CompilationUnitSyntax rootCompilationUnit)
            {
                foreach (var directive in rootCompilationUnit.Usings)
                {
                    if (string.Equals(directive.Name.ToString(), Constants.SystemNamespace, StringComparison.Ordinal))
                    {
                        insertUsingDirective = false;
                        break;
                    }
                }

                if (insertUsingDirective)
                {
                    var usingDirective = UsingDirective(IdentifierName(Constants.SystemNamespace)).WithTrailingTrivia(EndOfLine("\r\n"));
                    if (root is CompilationUnitSyntax compilationUnit)
                    {
                        root = compilationUnit.AddUsings(usingDirective);
                    }
                }
            }

            return document.WithSyntaxRoot(root);
        }
    }
}
