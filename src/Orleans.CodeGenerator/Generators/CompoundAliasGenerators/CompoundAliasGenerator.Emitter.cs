namespace Orleans.CodeGenerator.Generators.CompoundAliasGenerators;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

internal partial class CompoundAliasGenerator
{
    private class Emitter : EmitterBase
    {

        private static string _metadataClassName;
        private static string _metadataClassNamespace;
        private static CompoundAliasGeneratorContext _context;

        public Emitter(CompoundAliasGeneratorContext context, SourceProductionContext sourceProductionContext) : base(sourceProductionContext)
        {
            _context = context;

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
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.PartialKeyword))
                //.AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(classMembers);
        }

        private static MethodDeclarationSyntax GetMethodDeclarationSyntax()
        {
            IdentifierNameSyntax configParam = "config".ToIdentifierName();
            List<StatementSyntax> body = GetStatementSyntaxes(configParam);
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "AddAssemblyTypeCompoundAliases")
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
            AddSource("CompoundAlias", content);
        }

        private static List<StatementSyntax> GetStatementSyntaxes(IdentifierNameSyntax configParam)
        {
            var body = new List<StatementSyntax>();
            AddCompoundTypeAliases(configParam, body);
            return body;
        }

        private static void AddCompoundTypeAliases(IdentifierNameSyntax configParam, List<StatementSyntax> body)
        {
            // The goal is to emit a tree describing all of the generated invokers in the form:
            // ("inv", typeof(ProxyBaseType), typeof(ContainingInterface), "<MethodId>")
            // The first step is to collate the invokers into tree to ease the process of generating a tree in code.
            var nodeId = 0;
            AddCompoundTypeAliases(body, configParam.Member("CompoundTypeAliases"), _context.CompoundTypeAliases);
            void AddCompoundTypeAliases(List<StatementSyntax> body, ExpressionSyntax tree, CompoundTypeAliasTree aliases)
            {
                ExpressionSyntax node;

                if (aliases.Key.IsDefault)
                {
                    // At the root node, do not create a new node, just enumerate over the child nodes.
                    node = tree;
                }
                else
                {
                    var nodeName = IdentifierName($"n{++nodeId}");
                    node = nodeName;
                    var valueExpression = aliases.Value switch
                    {
                        { } type => Argument(TypeOfExpression(type)),
                        _ => null
                    };

                    // Get the arguments for the Add call
                    var addArguments = aliases.Key switch
                    {
                        { IsType: true } typeKey => valueExpression switch
                        {
                            // Call the two-argument Add overload to add a key and value.
                            { } argument => new[] { Argument(TypeOfExpression(typeKey.TypeValue.ToOpenTypeSyntax())), argument },

                            // Call the one-argument Add overload to add only a key.
                            _ => new[] { Argument(TypeOfExpression(typeKey.TypeValue.ToOpenTypeSyntax())) },
                        },
                        { IsString: true } stringKey => valueExpression switch
                        {
                            // Call the two-argument Add overload to add a key and value.
                            { } argument => new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringKey.StringValue))), argument },

                            // Call the one-argument Add overload to add only a key.
                            _ => new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringKey.StringValue))) },
                        },
                        _ => throw new InvalidOperationException("Unexpected alias key")
                    };

                    if (aliases.Children is { Count: > 0 })
                    {
                        // C#: var {newTree.Identifier} = {tree}.Add({addArguments});
                        body.Add(LocalDeclarationStatement(VariableDeclaration(
                            ParseTypeName("var"),
                            SingletonSeparatedList(VariableDeclarator(nodeName.Identifier).WithInitializer(EqualsValueClause(InvocationExpression(
                                tree.Member("Add"),
                                ArgumentList(SeparatedList(addArguments)))))))));
                    }
                    else
                    {
                        // Do not emit a variable.
                        // C#: {tree}.Add({addArguments});
                        body.Add(ExpressionStatement(InvocationExpression(tree.Member("Add"), ArgumentList(SeparatedList(addArguments)))));
                    }
                }

                if (aliases.Children is { Count: > 0 })
                {
                    foreach (var child in aliases.Children.Values)
                    {
                        AddCompoundTypeAliases(body, node, child);
                    }
                }
            }
        }

    }
}
