using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal class ActivatorGenerator
    {
        private readonly CodeGenerator _codeGenerator;

        private struct ConstructorArgument
        {
            public TypeSyntax Type { get; set; }
            public string FieldName { get; set; }
            public string ParameterName { get; set; }
        }

        public ActivatorGenerator(CodeGenerator codeGenerator)
        {
            _codeGenerator = codeGenerator;
        }

        public ClassDeclarationSyntax GenerateActivator(ISerializableTypeDescription type)
        {
            var simpleClassName = GetSimpleClassName(type);

            var baseInterface = _codeGenerator.LibraryTypes.IActivator_1.ToTypeSyntax(type.TypeSyntax);

            var orderedFields = new List<ConstructorArgument>();
            var index = 0;
            if (type.ActivatorConstructorParameters is { Count: > 0 } parameters)
            {
                foreach (var arg in parameters)
                {
                    orderedFields.Add(new ConstructorArgument { Type = arg, FieldName = $"_arg{index}", ParameterName = $"arg{index}" });
                    index++;
                }
            }

            var members = new List<MemberDeclarationSyntax>();
            foreach (var field in orderedFields)
            {
                members.Add(
                    FieldDeclaration(VariableDeclaration(field.Type, SingletonSeparatedList(VariableDeclarator(field.FieldName))))
                    .AddModifiers(
                        Token(SyntaxKind.PrivateKeyword),
                        Token(SyntaxKind.ReadOnlyKeyword)));
            }

            if (orderedFields.Count > 0)
                members.Add(GenerateConstructor(simpleClassName, orderedFields));

            members.Add(GenerateCreateMethod(type, orderedFields));

            var classDeclaration = ClassDeclaration(simpleClassName)
                .AddBaseListTypes(SimpleBaseType(baseInterface))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(members.ToArray());

            if (type.IsGenericType)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, type.TypeParameters);
            }

            return classDeclaration;
        }

        public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => $"Activator_{serializableType.Name}";

        private ConstructorDeclarationSyntax GenerateConstructor(
            string simpleClassName,
            List<ConstructorArgument> orderedFields)
        {
            var parameters = new List<ParameterSyntax>();
            var body = new List<StatementSyntax>();
            foreach (var field in orderedFields)
            {
                parameters.Add(Parameter(field.ParameterName.ToIdentifier()).WithType(field.Type));

                body.Add(ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                field.FieldName.ToIdentifierName(),
                                Unwrapped(field.ParameterName.ToIdentifierName()))));
            }

            var constructorDeclaration = ConstructorDeclaration(simpleClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .AddBodyStatements(body.ToArray());

            return constructorDeclaration;

            static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
            {
                return InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), IdentifierName("UnwrapService")),
                    ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(expr) })));
            }
        }

        private MemberDeclarationSyntax GenerateCreateMethod(ISerializableTypeDescription type, List<ConstructorArgument> orderedFields)
        {
            ExpressionSyntax createObject;
            if (type.ActivatorConstructorParameters is { Count: > 0 })
            {
                var argList = new List<ArgumentSyntax>();
                foreach (var field in orderedFields)
                {
                    argList.Add(Argument(field.FieldName.ToIdentifierName()));
                }

                createObject = ObjectCreationExpression(type.TypeSyntax).WithArgumentList(ArgumentList(SeparatedList(argList)));
            }
            else
            {
                createObject = type.GetObjectCreationExpression();
            }

            return MethodDeclaration(type.TypeSyntax, "Create")
                .WithExpressionBody(ArrowExpressionClause(createObject))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .AddModifiers(Token(SyntaxKind.PublicKeyword));
        }
    }
}