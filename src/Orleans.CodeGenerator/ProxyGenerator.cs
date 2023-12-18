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
    internal class ProxyGenerator
    {
        private const string CopyContextPoolMemberName = "CopyContextPool";
        private const string CodecProviderMemberName = "CodecProvider";
        private readonly CodeGenerator _codeGenerator;

        public ProxyGenerator(CodeGenerator codeGenerator)
        {
            _codeGenerator = codeGenerator;
        }

        private LibraryTypes LibraryTypes => _codeGenerator.LibraryTypes;

        public (ClassDeclarationSyntax, GeneratedProxyDescription) Generate(ProxyInterfaceDescription interfaceDescription)
        {
            var generatedClassName = GetSimpleClassName(interfaceDescription);

            var fieldDescriptions = GetFieldDescriptions(interfaceDescription);
            var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
            var proxyMethods = CreateProxyMethods(fieldDescriptions, interfaceDescription);

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

            return (classDeclaration, new GeneratedProxyDescription(interfaceDescription, generatedClassName));
        }

        public static string GetSimpleClassName(ProxyInterfaceDescription interfaceDescription)
            => $"Proxy_{SyntaxGeneration.Identifier.SanitizeIdentifierName(interfaceDescription.Name)}";
        
        private List<GeneratedFieldDescription> GetFieldDescriptions(
            ProxyInterfaceDescription interfaceDescription)
        {
            var fields = new List<GeneratedFieldDescription>();

            // Add a copier field for any method parameter which does not have a static codec.
            var paramCopiers = interfaceDescription.Methods
                .Where(method => method.MethodTypeParameters.Count == 0)
                .SelectMany(method => method.GeneratedInvokable.Members);
            _codeGenerator.CopierGenerator.GetCopierFieldDescriptions(paramCopiers, fields);
            return fields;
        }

        private MemberDeclarationSyntax[] GetFieldDeclarations(List<GeneratedFieldDescription> fieldDescriptions)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            static MemberDeclarationSyntax GetFieldDeclaration(GeneratedFieldDescription description)
            {
                return FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
            }
        }

        private MemberDeclarationSyntax[] CreateProxyMethods(
            List<GeneratedFieldDescription> fieldDescriptions,
            ProxyInterfaceDescription interfaceDescription)
        {
            var res = new List<MemberDeclarationSyntax>();
            foreach (var methodDescription in interfaceDescription.Methods)
            {
                res.Add(CreateProxyMethod(methodDescription));
            }
            return res.ToArray();

            MethodDeclarationSyntax CreateProxyMethod(ProxyMethodDescription methodDescription)
            {
                var (isAsync, body) = CreateAsyncProxyMethodBody(fieldDescriptions, methodDescription);
                var method = methodDescription.Method;
                var declaration = MethodDeclaration(method.ReturnType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions), method.Name.EscapeIdentifier())
                    .AddParameterListParameters(method.Parameters.Select((p, i) => GetParameterSyntax(i, p, methodDescription.TypeParameterSubstitutions)).ToArray())
                    .WithBody(body);

                if (isAsync)
                {
                    declaration = declaration.WithModifiers(TokenList(Token(SyntaxKind.AsyncKeyword)));
                }

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

        private (bool IsAsync, BlockSyntax body) CreateAsyncProxyMethodBody(
            List<GeneratedFieldDescription> fieldDescriptions,
            ProxyMethodDescription methodDescription)
        {
            var statements = new List<StatementSyntax>();
            var requestVar = IdentifierName("request");
            var methodSymbol = methodDescription.Method;
            var invokable = methodDescription.GeneratedInvokable;
            ExpressionSyntax createRequestExpr = (!invokable.IsEmptyConstructable || invokable.UseActivator) switch
            {
                true => InvocationExpression(ThisExpression().Member("GetInvokable", invokable.TypeSyntax))
                .WithArgumentList(ArgumentList(SeparatedList<ArgumentSyntax>())),
                _ => ObjectCreationExpression(invokable.TypeSyntax).WithArgumentList(ArgumentList())
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
                    .Concat(_codeGenerator.LibraryTypes.StaticCopiers)
                    .ToList();

            // Set request object fields from method parameters.
            var parameterIndex = 0;
            var parameters = invokable.Members.OfType<MethodParameterFieldDescription>().Select(member => new SerializableMethodMember(member));
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

                var valueExpression = _codeGenerator.CopierGenerator.GenerateMemberCopy(
                    fieldDescriptions,
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

            string invokeMethodName = default;
            foreach (var attr in methodDescription.Method.GetAttributes())
            {
                if (attr.AttributeClass.GetAttributes(LibraryTypes.InvokeMethodNameAttribute, out var attrs))
                {
                    foreach (var methodAttr in attrs)
                    {
                        invokeMethodName = (string)methodAttr.ConstructorArguments.First().Value;
                    }
                }
            }

            var methodReturnType = methodDescription.Method.ReturnType;
            if (methodReturnType is not INamedTypeSymbol namedMethodReturnType)
            {
                var diagnostic = InvalidRpcMethodReturnTypeDiagnostic.CreateDiagnostic(methodDescription.InvokableMethod);
                throw new OrleansGeneratorDiagnosticAnalysisException(diagnostic);
            }

            ExpressionSyntax baseInvokeExpression;
            var isVoid = methodReturnType.SpecialType is SpecialType.System_Void;
            if (namedMethodReturnType.TypeArguments.Length == 1)
            {
                // Task<T> / ValueTask<T>
                var resultType = namedMethodReturnType.TypeArguments[0];
                baseInvokeExpression = BaseExpression().Member(
                    invokeMethodName ?? "InvokeAsync",
                    resultType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions));
            }
            else if (isVoid)
            {
                // void
                baseInvokeExpression = BaseExpression().Member(invokeMethodName ?? "Invoke");
            }
            else
            {
                // Task / ValueTask
                baseInvokeExpression = BaseExpression().Member(invokeMethodName ?? "InvokeAsync");
            }

            // C#: base.InvokeAsync<TReturn>(request);
            var invocationExpression =
                         InvocationExpression(
                             baseInvokeExpression,
                             ArgumentList(SeparatedList(new[] { Argument(requestVar) })));

            var rt = namedMethodReturnType.ConstructedFrom;
            bool isAsync;
            if (SymbolEqualityComparer.Default.Equals(rt, LibraryTypes.Task_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, LibraryTypes.Task))
            {
                // C#: return <invocation>.AsTask()
                statements.Add(ReturnStatement(InvocationExpression(invocationExpression.Member("AsTask"), ArgumentList())));
                isAsync = false;
            }
            else if (SymbolEqualityComparer.Default.Equals(rt, LibraryTypes.ValueTask_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, LibraryTypes.ValueTask))
            {
                // ValueTask<T> / ValueTask
                // C#: return <invocation>
                statements.Add(ReturnStatement(invocationExpression));
                isAsync = false;
            }
            else if (invokable.ReturnValueInitializerMethod is { } returnValueInitializerMethod)
            {
                // C#: return request.<returnValueInitializerMethod>(this);
                statements.Add(ReturnStatement(InvocationExpression(requestVar.Member(returnValueInitializerMethod), ArgumentList(SingletonSeparatedList(Argument(ThisExpression()))))));
                isAsync = false;
            }
            else if (isVoid)
            {
                // C#: <invocation>
                statements.Add(ExpressionStatement(invocationExpression));
                isAsync = false;
            }
            else if (rt.Arity == 0)
            {
                // C#: await <invocation>
                statements.Add(ExpressionStatement(AwaitExpression(invocationExpression)));
                isAsync = true;
            }
            else
            {
                // C#: return await <invocation>
                statements.Add(ReturnStatement(AwaitExpression(invocationExpression)));
                isAsync = true;
            }

            return (isAsync, Block(statements));
        }

        private MemberDeclarationSyntax[] GenerateConstructors(
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

        private ParameterSyntax GetParameterSyntax(int index, IParameterSymbol parameter, Dictionary<ITypeParameterSymbol, string> typeParameterSubstitutions)
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