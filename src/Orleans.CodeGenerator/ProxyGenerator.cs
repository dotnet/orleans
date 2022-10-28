using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.CopierGenerator;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal static class ProxyGenerator
    {
        private const string CopyContextPoolMemberName = "CopyContextPool";
        private const string CodecProviderMemberName = "CodecProvider";

        public static (ClassDeclarationSyntax, GeneratedProxyDescription) Generate(
            LibraryTypes libraryTypes,
            InvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel)
        {
            var generatedClassName = GetSimpleClassName(interfaceDescription);

            var fieldDescriptions = GetFieldDescriptions(interfaceDescription, metadataModel, libraryTypes);
            var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
            var proxyMethods = CreateProxyMethods(libraryTypes, fieldDescriptions, interfaceDescription, metadataModel);

            var ctors = GenerateConstructors(generatedClassName, fieldDescriptions, interfaceDescription.ProxyBaseType);

            var classDeclaration = ClassDeclaration(generatedClassName)
                .AddBaseListTypes(
                    SimpleBaseType(interfaceDescription.ProxyBaseType.ToTypeSyntax()),
                    SimpleBaseType(interfaceDescription.InterfaceType.ToTypeSyntax()))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(
                    AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(fieldDeclarations)
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

        private static List<GeneratedFieldDescription> GetFieldDescriptions(
            InvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel,
            LibraryTypes libraryTypes)
        {
            var fields = new List<GeneratedFieldDescription>();

            // Add a codec field for any method parameter which does not have a static codec.
            var allTypes = interfaceDescription.Methods
                .Where(method => method.MethodTypeParameters.Count == 0)
                .SelectMany(method => metadataModel.GeneratedInvokables[method].Members);
            GetCopierFieldDescriptions(allTypes, libraryTypes, fields);
            return fields;
        }

        private static MemberDeclarationSyntax[] GetFieldDeclarations(List<GeneratedFieldDescription> fieldDescriptions)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            static MemberDeclarationSyntax GetFieldDeclaration(GeneratedFieldDescription description)
            {
                return FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
            }
        }

        private static MemberDeclarationSyntax[] CreateProxyMethods(
            LibraryTypes libraryTypes,
            List<GeneratedFieldDescription> fieldDescriptions,
            InvokableInterfaceDescription interfaceDescription,
            MetadataModel metadataModel)
        {
            var res = new List<MemberDeclarationSyntax>();
            foreach (var methodDescription in interfaceDescription.Methods)
            {
                res.Add(CreateProxyMethod(methodDescription));
            }
            return res.ToArray();

            MethodDeclarationSyntax CreateProxyMethod(MethodDescription methodDescription)
            {
                var method = methodDescription.Method;
                var declaration = MethodDeclaration(method.ReturnType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions), method.Name.EscapeIdentifier())
                    .AddParameterListParameters(method.Parameters.Select((p, i) => GetParameterSyntax(i, p, methodDescription.TypeParameterSubstitutions)).ToArray())
                    .WithBody(
                        CreateAsyncProxyMethodBody(libraryTypes, fieldDescriptions, metadataModel, methodDescription));
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
            List<GeneratedFieldDescription> fieldDescriptions,
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

            var codecs = fieldDescriptions.OfType<ICopierDescription>()
                    .Concat(libraryTypes.StaticCopiers)
                    .ToList();

            // Set request object fields from method parameters.
            var parameterIndex = 0;
            var parameters = requestDescription.Members.OfType<MethodParameterFieldDescription>().Select(member => new SerializableMethodMember(member));
            ExpressionSyntax copyContextPool = BaseExpression().Member(CopyContextPoolMemberName);
            ExpressionSyntax copyContextVariable = IdentifierName("copyContext");
            var hasCopyContext = false;
            foreach (var parameter in parameters)
            {
                // Only create a copy context as needed.
                if (!hasCopyContext && !parameter.IsShallowCopyable)
                {
                    // C#: using var copyContext = base.CopyContext.GetContext();
                    statements.Add(
                            LocalDeclarationStatement(
                                VariableDeclaration(
                                    ParseTypeName("var"),
                                    SingletonSeparatedList(
                                        VariableDeclarator(Identifier("copyContext")).WithInitializer(
                                            EqualsValueClause(InvocationExpression(
                                                    copyContextPool.Member("GetContext"),
                                                    ArgumentList())))))).WithUsingKeyword(Token(SyntaxKind.UsingKeyword)));
                    hasCopyContext = true;
                }

                var valueExpression = GenerateMemberCopy(
                    fieldDescriptions,
                    libraryTypes,
                    IdentifierName($"arg{parameterIndex}"),
                    copyContextVariable,
                    codecs,
                    parameter);

                statements.Add(
                    ExpressionStatement(
                        AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            requestVar.Member($"arg{parameterIndex}"),
                            valueExpression)));

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
            var methodReturnType = methodDescription.Method.ReturnType;
            if (methodReturnType is not INamedTypeSymbol namedMethodReturnType)
            {
                var diagnostic = InvalidRpcMethodReturnTypeDiagnostic.CreateDiagnostic(methodDescription);
                throw new OrleansGeneratorDiagnosticAnalysisException(diagnostic);
            }

            if (namedMethodReturnType.TypeArguments.Length == 1)
            {
                // Task<T> / ValueTask<T>
                resultType = namedMethodReturnType.TypeArguments[0];
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

            var rt = namedMethodReturnType.ConstructedFrom;
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

        private static MemberDeclarationSyntax[] GenerateConstructors(
            string simpleClassName,
            List<GeneratedFieldDescription> fieldDescriptions,
            INamedTypeSymbol baseType)
        {
            if (baseType is null)
            {
                return Array.Empty<MemberDeclarationSyntax>();
            }

            var bodyStatements = GetBodyStatements();
            var res = new List<MemberDeclarationSyntax>();
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

                res.Add(CreateConstructor(method));
            }
            return res.ToArray();

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
                    .WithBody(Block(bodyStatements));
            }

            static SyntaxToken[] GetModifiers(IMethodSymbol method)
            {
                switch (method.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                    case Accessibility.Protected:
                        return new[] { Token(SyntaxKind.PublicKeyword) };
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                    case Accessibility.ProtectedAndInternal:
                        return new[] { Token(SyntaxKind.InternalKeyword) };
                    default:
                        return Array.Empty<SyntaxToken>();
                }
            }

            static ArgumentSyntax GetBaseInitializerArgument(IParameterSymbol parameter, int index)
            {
                var name = $"arg{index}";
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

            List<StatementSyntax> GetBodyStatements()
            {
                var res = new List<StatementSyntax>();
                foreach (var field in fieldDescriptions)
                {
                    switch (field)
                    {
                        case GeneratedFieldDescription _ when field.IsInjected:
                            res.Add(ExpressionStatement(
                                AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    ThisExpression().Member(field.FieldName.ToIdentifierName()),
                                    Unwrapped(field.FieldName.ToIdentifierName()))));
                            break;
                        case CopierFieldDescription codec:
                            {
                                res.Add(ExpressionStatement(
                                    AssignmentExpression(
                                        SyntaxKind.SimpleAssignmentExpression,
                                        field.FieldName.ToIdentifierName(),
                                        GetService(field.FieldType))));
                            }
                            break;
                    }
                }
                return res;

                static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
                {
                    return InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), IdentifierName("UnwrapService")),
                        ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(expr) })));
                }

                static ExpressionSyntax GetService(TypeSyntax type)
                {
                    return InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), GenericName(Identifier("GetService"), TypeArgumentList(SingletonSeparatedList(type)))),
                        ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(IdentifierName(CodecProviderMemberName)) })));
                }
            }
        }

        private static ParameterSyntax GetParameterSyntax(int index, IParameterSymbol parameter, Dictionary<ITypeParameterSymbol, string> typeParameterSubstitutions)
        {
            var result = Parameter(Identifier($"arg{index}")).WithType(parameter.Type.ToTypeSyntax(typeParameterSubstitutions));
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