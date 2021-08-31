using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal static class ProxyGenerator
    {
        public static (ClassDeclarationSyntax, GeneratedProxyDescription) Generate(
            LibraryTypes libraryTypes,
            InvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel)
        {
            var generatedClassName = GetSimpleClassName(interfaceDescription);

            var ctors = GenerateConstructors(generatedClassName, interfaceDescription.ProxyBaseType).ToArray();
            var proxyMethods = CreateProxyMethods(libraryTypes, interfaceDescription, metadataModel).ToArray();

            var classDeclaration = ClassDeclaration(generatedClassName)
                .AddBaseListTypes(
                    SimpleBaseType(interfaceDescription.ProxyBaseType.ToTypeSyntax()),
                    SimpleBaseType(interfaceDescription.InterfaceType.ToTypeSyntax()))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(
                    AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(ctors)
                .AddMembers(proxyMethods);

            var typeParameters = interfaceDescription.TypeParameters;
            if (typeParameters.Count > 0)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, typeParameters);
            }

            return (classDeclaration, new GeneratedProxyDescription(interfaceDescription));
        }

        public static string GetSimpleClassName(InvokableInterfaceDescription interfaceDescription) => $"Proxy_{interfaceDescription.Name}";

        private static IEnumerable<MemberDeclarationSyntax> CreateProxyMethods(
            LibraryTypes libraryTypes,
            InvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel)
        {
            foreach (var methodDescription in interfaceDescription.Methods)
            {
                yield return CreateProxyMethod(methodDescription);
            }

            MethodDeclarationSyntax CreateProxyMethod(MethodDescription methodDescription)
            {
                var method = methodDescription.Method;
                var declaration = MethodDeclaration(method.ReturnType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions), method.Name.EscapeIdentifier())
                    .AddParameterListParameters(method.Parameters.Select((p, i) => GetParameterSyntax(i, p, methodDescription.TypeParameterSubstitutions)).ToArray())
                    .WithBody(
                        CreateAsyncProxyMethodBody(libraryTypes, metadataModel, methodDescription));
                if (methodDescription.HasCollision)
                {
                    declaration = declaration.WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));

                    // Type parameter constrains are not valid on explicit interface definitions
                    var typeParameters = SyntaxFactoryUtility.GetTypeParameterConstraints(methodDescription.MethodTypeParameters);
                    foreach (var (name, constraints) in typeParameters)
                    {
                        if (constraints.Count > 0)
                        {
                            declaration = declaration.AddConstraintClauses(
                                TypeParameterConstraintClause(name).AddConstraints(constraints.ToArray()));
                        }
                    }
                }
                else
                {
                    var explicitInterfaceSpecifier = ExplicitInterfaceSpecifier(methodDescription.Method.ContainingType.ToNameSyntax());
                    declaration = declaration.WithExplicitInterfaceSpecifier(explicitInterfaceSpecifier);
                }

                if (methodDescription.MethodTypeParameters.Count > 0)
                {
                    declaration = declaration.WithTypeParameterList(
                        TypeParameterList(SeparatedList(methodDescription.MethodTypeParameters.Select(tp => TypeParameter(tp.Name)))));
                }

                return declaration;
            }
        }

        private static BlockSyntax CreateAsyncProxyMethodBody(
            LibraryTypes libraryTypes,
            MetadataModel metadataModel,
            MethodDescription methodDescription)
        {
            var statements = new List<StatementSyntax>();
            var requestVar = IdentifierName("request");
            var requestDescription = metadataModel.GeneratedInvokables[methodDescription];
            ExpressionSyntax createRequestExpr = (!requestDescription.IsEmptyConstructable || requestDescription.UseActivator) switch
            {
                true => InvocationExpression(ThisExpression().Member("GetInvokable", requestDescription.TypeSyntax))
                .WithArgumentList(ArgumentList(SeparatedList<ArgumentSyntax>())),
                _ => ObjectCreationExpression(requestDescription.TypeSyntax).WithArgumentList(ArgumentList())
            };

            statements.Add(
                LocalDeclarationStatement(
                    VariableDeclaration(
                        ParseTypeName("var"),
                        SingletonSeparatedList(
                            VariableDeclarator(
                                    Identifier("request"))
                                .WithInitializer(
                                    EqualsValueClause(createRequestExpr))))));

            // Set request object fields from method parameters.
            var parameterIndex = 0;
            foreach (var parameter in methodDescription.Method.Parameters)
            {
                statements.Add(
                    ExpressionStatement(
                        AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            requestVar.Member($"arg{parameterIndex}"),
                            IdentifierName(SyntaxFactoryUtility.GetSanitizedName(parameter, parameterIndex)))));

                parameterIndex++;
            }

            var invokeMethodName = "InvokeAsync";
            foreach (var attr in methodDescription.Method.GetAttributes())
            {
                if (attr.AttributeClass.GetAttributes(libraryTypes.InvokeMethodNameAttribute, out var attrs))
                {
                    foreach (var methodAttr in attrs)
                    {
                        invokeMethodName = (string)methodAttr.ConstructorArguments.First().Value;
                    }
                }
            }

            ITypeSymbol resultType;
            var methodReturnType = (INamedTypeSymbol)methodDescription.Method.ReturnType;
            if (methodReturnType.TypeArguments.Length == 1)
            {
                // Task<T> / ValueTask<T>
                resultType = methodReturnType.TypeArguments[0];
            }
            else
            {
                // void, Task / ValueTask
                resultType = null;
            }

            // C#: base.InvokeAsync<TReturn>(request);
            var baseInvokeExpression = resultType switch
            {
                not null => BaseExpression().Member(invokeMethodName, resultType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions)),
                _ => BaseExpression().Member(invokeMethodName),
            };
            var invocationExpression =
                         InvocationExpression(
                             baseInvokeExpression,
                             ArgumentList(SeparatedList(new[] { Argument(requestVar) })));

            var rt = methodReturnType.ConstructedFrom;
            if (SymbolEqualityComparer.Default.Equals(rt, libraryTypes.Task_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, libraryTypes.Task))
            {
                // C#: return <invocation>.AsTask()
                statements.Add(ReturnStatement(InvocationExpression(invocationExpression.Member("AsTask"), ArgumentList())));
            }
            else if (SymbolEqualityComparer.Default.Equals(rt, libraryTypes.ValueTask_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, libraryTypes.ValueTask))
            {
                // ValueTask<T> / ValueTask
                // C#: return <invocation>
                statements.Add(ReturnStatement(invocationExpression));
            }
            else
            {
                // C#: _ = <invocation>
                statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName("_"), invocationExpression)));
            }

            return Block(statements);
        }

        private static IEnumerable<MemberDeclarationSyntax> GenerateConstructors(
            string simpleClassName,
            INamedTypeSymbol baseType)
        {
            if (baseType is null)
            {
                yield break;
            }

            foreach (var member in baseType.GetMembers())
            {
                if (member is not IMethodSymbol method)
                {
                    continue;
                }

                if (method.MethodKind != MethodKind.Constructor)
                {
                    continue;
                }

                if (method.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                yield return CreateConstructor(method);
            }

            ConstructorDeclarationSyntax CreateConstructor(IMethodSymbol baseConstructor)
            {
                return ConstructorDeclaration(simpleClassName)
                    .AddParameterListParameters(baseConstructor.Parameters.Select((p, i) => GetParameterSyntax(i, p, typeParameterSubstitutions: null)).ToArray())
                    .WithModifiers(TokenList(GetModifiers(baseConstructor)))
                    .WithInitializer(
                        ConstructorInitializer(
                            SyntaxKind.BaseConstructorInitializer,
                            ArgumentList(
                                SeparatedList(baseConstructor.Parameters.Select(GetBaseInitializerArgument)))))
                    .WithBody(Block());
            }

            static IEnumerable<SyntaxToken> GetModifiers(IMethodSymbol method)
            {
                switch (method.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                    case Accessibility.Protected:
                        yield return Token(SyntaxKind.PublicKeyword);
                        break;
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                    case Accessibility.ProtectedAndInternal:
                        yield return Token(SyntaxKind.InternalKeyword);
                        break;
                    default:
                        break;
                }
            }

            static ArgumentSyntax GetBaseInitializerArgument(IParameterSymbol parameter, int index)
            {
                var name = SyntaxFactoryUtility.GetSanitizedName(parameter, index);
                var result = Argument(IdentifierName(name));
                switch (parameter.RefKind)
                {
                    case RefKind.None:
                        break;
                    case RefKind.Ref:
                        result = result.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                        break;
                    case RefKind.Out:
                        result = result.WithRefOrOutKeyword(Token(SyntaxKind.OutKeyword));
                        break;
                    default:
                        break;
                }

                return result;
            }
        }

        private static ParameterSyntax GetParameterSyntax(int index, IParameterSymbol parameter, Dictionary<ITypeParameterSymbol, string> typeParameterSubstitutions)
        {
            var result = Parameter(Identifier(SyntaxFactoryUtility.GetSanitizedName(parameter, index))).WithType(parameter.Type.ToTypeSyntax(typeParameterSubstitutions));
            switch (parameter.RefKind)
            {
                case RefKind.None:
                    break;
                case RefKind.Ref:
                    result = result.WithModifiers(TokenList(Token(SyntaxKind.RefKeyword)));
                    break;
                case RefKind.Out:
                    result = result.WithModifiers(TokenList(Token(SyntaxKind.OutKeyword)));
                    break;
                case RefKind.In:
                    result = result.WithModifiers(TokenList(Token(SyntaxKind.InKeyword)));
                    break;
                default:
                    break;
            }

            return result;
        }
    }
}