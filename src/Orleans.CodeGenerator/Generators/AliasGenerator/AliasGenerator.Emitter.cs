namespace Orleans.CodeGenerator.Generators.AliasGenerator;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

internal partial class AliasGenerator
{
    private class Emitter : EmitterBase
    {

        private static string _metadataClassName;
        private static string _metadataClassNamespace;
        private static AliasGeneratorContext _context;

        public Emitter(IncrementalGeneratorContext context, SourceProductionContext sourceProductionContext) : base(sourceProductionContext)
        {
            _context = (AliasGeneratorContext)context;

        }

        private static CompilationUnitSyntax GetCompilationUnit(SyntaxList<UsingDirectiveSyntax>? usings = default, params MemberDeclarationSyntax[] namespaces)
        {
            var cus = CompilationUnit().WithMembers(List(namespaces));
            if (usings != null)
                cus.WithUsings(usings.Value);
            return cus;
        }

        private static NamespaceDeclarationSyntax GetNamespaceDeclarationSyntax(string namespaceName, SyntaxList<UsingDirectiveSyntax>? usings = default, params MemberDeclarationSyntax[] memberDeclarationSyntaxes)
        {

            var nds = NamespaceDeclaration(ParseName(namespaceName));
            if (usings is not null)
                nds = nds.WithUsings(usings.Value);
            if (memberDeclarationSyntaxes.Any())
                nds = nds.WithMembers(List(memberDeclarationSyntaxes));

            return nds;
        }

        private static ClassDeclarationSyntax GetClassDeclarationSyntax(params MemberDeclarationSyntax[] classMembers)
        {

            return ClassDeclaration(_metadataClassName + "_TypeManifestOptionsExtensionMethods")
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(classMembers);
        }

        private static MethodDeclarationSyntax GetMethodDeclarationSyntax()
        {
            IdentifierNameSyntax configParam = "config".ToIdentifierName();
            List<StatementSyntax> body = GetStatementSyntaxes(configParam);
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "AddAssemblyTypeAliases")
                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
                .AddParameterListParameters(
                    Parameter(configParam.Identifier).WithModifiers(SyntaxTokenList.Create(Token(SyntaxKind.ThisKeyword))).WithType(IdentifierName("global::Orleans.Serialization.Configuration.TypeManifestOptions")))
                .AddBodyStatements(body.ToArray());
        }


        public override void Emit()
        {
            _metadataClassName = SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);
            _metadataClassNamespace = Constants.CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);

            AddAliasExtensioMethodClass();

        }

        private void AddAliasExtensioMethodClass()
        {
            var mds = GetMethodDeclarationSyntax();
            var cds = GetClassDeclarationSyntax(mds);
            //var usings = List(new[] { UsingDirective(ParseName("global::Orleans.Serialization.Codecs")), UsingDirective(ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")) });
            var nds = GetNamespaceDeclarationSyntax(_metadataClassNamespace, default, cds);
            var compilationUnit = GetCompilationUnit(default, nds);
            var content = ConvertCompilationUnitSyntaxIntoString(compilationUnit);
            AddSource("Alias", content);
        }

        private static List<StatementSyntax> GetStatementSyntaxes(IdentifierNameSyntax configParam)
        {
            var body = new List<StatementSyntax>();

            var addTypeAliasMethod = configParam.Member("WellKnownTypeAliases").Member("Add");
            foreach (var type in _context.TypeAliases)
            {
                body.Add(ExpressionStatement(InvocationExpression(addTypeAliasMethod,
                    ArgumentList(SeparatedList(new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(type.Alias))), Argument(TypeOfExpression(type.Type)) })))));
            }

            return body;
        }
    }
}
