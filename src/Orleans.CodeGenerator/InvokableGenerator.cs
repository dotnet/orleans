using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.CodeGenerator.Diagnostics;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal class InvokableGenerator
    {
        private readonly CodeGenerator _codeGenerator;

        public InvokableGenerator(CodeGenerator codeGenerator)
        {
            _codeGenerator = codeGenerator;
        }

        private LibraryTypes LibraryTypes => _codeGenerator.LibraryTypes;

        public GeneratedInvokableDescription Generate(InvokableMethodDescription invokableMethodInfo)
        {
            var method = invokableMethodInfo.Method;
            var generatedClassName = GetSimpleClassName(invokableMethodInfo);

            var baseClassType = GetBaseClassType(invokableMethodInfo);
            var fieldDescriptions = GetFieldDescriptions(invokableMethodInfo);
            var fields = GetFieldDeclarations(invokableMethodInfo, fieldDescriptions);
            var (ctor, ctorArgs) = GenerateConstructor(generatedClassName, invokableMethodInfo, baseClassType);
            var accessibility = GetAccessibility(method);
            var compoundTypeAliases = GetCompoundTypeAliasAttributeArguments(invokableMethodInfo, invokableMethodInfo.Key);

            List<INamedTypeSymbol> serializationHooks = new();
            if (baseClassType.GetAttributes(LibraryTypes.SerializationCallbacksAttribute, out var hookAttributes))
            {
                foreach (var hookAttribute in hookAttributes)
                {
                    var hookType = (INamedTypeSymbol)hookAttribute.ConstructorArguments[0].Value;
                    serializationHooks.Add(hookType);
                }
            }

            var targetField = fieldDescriptions.OfType<TargetFieldDescription>().Single();

            var accessibilityKind = accessibility switch
            {
                Accessibility.Public => SyntaxKind.PublicKeyword,
                _ => SyntaxKind.InternalKeyword,
            };

            var classDeclaration = GetClassDeclarationSyntax(
                invokableMethodInfo,
                generatedClassName,
                baseClassType,
                fieldDescriptions,
                fields,
                ctor,
                compoundTypeAliases,
                targetField,
                accessibilityKind);

            string returnValueInitializerMethod = null;
            if (baseClassType.GetAttribute(LibraryTypes.ReturnValueProxyAttribute) is { ConstructorArguments: { Length: > 0 } attrArgs })
            {
                returnValueInitializerMethod = (string)attrArgs[0].Value;
            }

            while (baseClassType.HasAttribute(LibraryTypes.SerializerTransparentAttribute))
            {
                baseClassType = baseClassType.BaseType;
            }

            var invokerDescription = new GeneratedInvokableDescription(
                invokableMethodInfo,
                accessibility,
                generatedClassName,
                CodeGenerator.GetGeneratedNamespaceName(invokableMethodInfo.ContainingInterface),
                fieldDescriptions.OfType<IMemberDescription>().ToList(),
                serializationHooks,
                baseClassType,
                ctorArgs,
                compoundTypeAliases,
                returnValueInitializerMethod,
                classDeclaration);
            return invokerDescription;

            static Accessibility GetAccessibility(IMethodSymbol methodSymbol)
            {
                var t = methodSymbol.ContainingType;
                Accessibility accessibility = t.DeclaredAccessibility;
                while (t is not null)
                {
                    if ((int)t.DeclaredAccessibility < (int)accessibility)
                    {
                        accessibility = t.DeclaredAccessibility;
                    }

                    t = t.ContainingType;
                }

                return accessibility;
            }
        }

        private ClassDeclarationSyntax GetClassDeclarationSyntax(
            InvokableMethodDescription method,
            string generatedClassName,
            INamedTypeSymbol baseClassType,
            List<InvokerFieldDescription> fieldDescriptions,
            MemberDeclarationSyntax[] fields,
            ConstructorDeclarationSyntax ctor,
            List<CompoundTypeAliasComponent[]> compoundTypeAliases,
            TargetFieldDescription targetField,
            SyntaxKind accessibilityKind)
        {
            var classDeclaration = ClassDeclaration(generatedClassName)
                .AddBaseListTypes(SimpleBaseType(baseClassType.ToTypeSyntax(method.TypeParameterSubstitutions)))
                .AddModifiers(Token(accessibilityKind), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(fields);

            foreach (var alias in compoundTypeAliases)
            {
                classDeclaration = classDeclaration.AddAttributeLists(
                    AttributeList(SingletonSeparatedList(GetCompoundTypeAliasAttribute(alias))));
            }

            if (ctor != null)
            {
                classDeclaration = classDeclaration.AddMembers(ctor);
            }

            if (method.ResponseTimeoutTicks.HasValue)
            {
                classDeclaration = classDeclaration.AddMembers(GenerateResponseTimeoutPropertyMembers(method.ResponseTimeoutTicks.Value));
            }

            classDeclaration = AddOptionalMembers(classDeclaration,
                    GenerateGetArgumentCount(method),
                    GenerateGetMethodName(method),
                    GenerateGetInterfaceName(method),
                    GenerateGetActivityName(method),
                    GenerateGetInterfaceType(method),
                    GenerateGetMethod(),
                    GenerateSetTargetMethod(method, targetField),
                    GenerateGetTargetMethod(targetField),
                    GenerateDisposeMethod(fieldDescriptions, baseClassType),
                    GenerateGetArgumentMethod(method, fieldDescriptions),
                    GenerateSetArgumentMethod(method, fieldDescriptions),
                    GenerateInvokeInnerMethod(method, fieldDescriptions, targetField));

            if (method.AllTypeParameters.Count > 0)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, method.AllTypeParameters);
            }

            return classDeclaration;
        }

        private MemberDeclarationSyntax[] GenerateResponseTimeoutPropertyMembers(long value)
        {
            var timespanField = FieldDeclaration(
                        VariableDeclaration(
                            LibraryTypes.TimeSpan.ToTypeSyntax(),
                            SingletonSeparatedList(VariableDeclarator("_responseTimeoutValue")
                            .WithInitializer(EqualsValueClause(
                                InvocationExpression(
                                    IdentifierName("global::System.TimeSpan").Member("FromTicks"),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value)))
                                    }))))))))
                        .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));

            var responseTimeoutProperty = MethodDeclaration(NullableType(LibraryTypes.TimeSpan.ToTypeSyntax()), "GetDefaultResponseTimeout")
                .WithExpressionBody(ArrowExpressionClause(IdentifierName("_responseTimeoutValue")))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword));
;
            return new MemberDeclarationSyntax[] { timespanField, responseTimeoutProperty };
        }

        private ClassDeclarationSyntax AddOptionalMembers(ClassDeclarationSyntax decl, params MemberDeclarationSyntax[] items)
            => decl.WithMembers(decl.Members.AddRange(items.Where(i => i != null)));

        internal AttributeSyntax GetCompoundTypeAliasAttribute(CompoundTypeAliasComponent[] argValues)
        {
            var args = new AttributeArgumentSyntax[argValues.Length];
            for (var i = 0; i < argValues.Length; i++)
            {
                ExpressionSyntax value;
                value = argValues[i].Value switch
                {
                    string stringValue => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringValue)),
                    ITypeSymbol typeValue => TypeOfExpression(typeValue.ToOpenTypeSyntax()),
                    _ => throw new InvalidOperationException($"Unsupported value")
                };

                args[i] = AttributeArgument(value);
            }

            return Attribute(LibraryTypes.CompoundTypeAliasAttribute.ToNameSyntax()).AddArgumentListArguments(args);
        }

        internal static List<CompoundTypeAliasComponent[]> GetCompoundTypeAliasAttributeArguments(InvokableMethodDescription methodDescription, InvokableMethodId invokableId)
        {
            var result = new List<CompoundTypeAliasComponent[]>(2);
            var containingInterface = methodDescription.ContainingInterface;
            if (methodDescription.HasAlias)
            {
                result.Add(GetCompoundTypeAliasComponents(invokableId, containingInterface, methodDescription.MethodId));
            }

            result.Add(GetCompoundTypeAliasComponents(invokableId, containingInterface, methodDescription.GeneratedMethodId));
            return result;
        }

        public static CompoundTypeAliasComponent[] GetCompoundTypeAliasComponents(
            InvokableMethodId invokableId,
            INamedTypeSymbol containingInterface,
            string methodId)
        {
            var proxyBaseComponents = invokableId.ProxyBase.CompositeAliasComponents;
            var alias = new CompoundTypeAliasComponent[1 + proxyBaseComponents.Length + 2];
            alias[0] = new("inv");
            for (var i = 0; i < proxyBaseComponents.Length; i++)
            {
                alias[i + 1] = proxyBaseComponents[i];
            }

            alias[1 + proxyBaseComponents.Length] = new(containingInterface);
            alias[1 + proxyBaseComponents.Length + 1] = new(methodId);
            return alias;
        }

        private INamedTypeSymbol GetBaseClassType(InvokableMethodDescription method)
        {
            var methodReturnType = method.Method.ReturnType;
            if (methodReturnType is not INamedTypeSymbol namedMethodReturnType)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InvalidRpcMethodReturnTypeDiagnostic.CreateDiagnostic(method));
            }

            if (method.InvokableBaseTypes.TryGetValue(namedMethodReturnType, out var baseClassType))
            {
                return baseClassType;
            }

            if (namedMethodReturnType.ConstructedFrom is { IsGenericType: true, IsUnboundGenericType: false } constructedFrom)
            {
                var unbound = constructedFrom.ConstructUnboundGenericType();
                if (method.InvokableBaseTypes.TryGetValue(unbound, out baseClassType))
                {
                    return baseClassType.ConstructedFrom.Construct(namedMethodReturnType.TypeArguments.ToArray());
                }
            }

            throw new OrleansGeneratorDiagnosticAnalysisException(InvalidRpcMethodReturnTypeDiagnostic.CreateDiagnostic(method));
        }

        private MemberDeclarationSyntax GenerateSetTargetMethod(
            InvokableMethodDescription methodDescription,
            TargetFieldDescription targetField)
        {
            var holder = IdentifierName("holder");
            var holderParameter = holder.Identifier;

            var containingInterface = methodDescription.ContainingInterface;
            var isExtension = methodDescription.Key.ProxyBase.IsExtension;
            var getTarget = InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        holder,
                        GenericName(isExtension ? "GetComponent" : "GetTarget")
                            .WithTypeArgumentList(
                                TypeArgumentList(
                                    SingletonSeparatedList(containingInterface.ToTypeSyntax())))))
                .WithArgumentList(ArgumentList());

            var body =
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(targetField.FieldName),
                    getTarget);
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "SetTarget")
                .WithParameterList(ParameterList(SingletonSeparatedList(Parameter(holderParameter).WithType(LibraryTypes.ITargetHolder.ToTypeSyntax()))))
                .WithExpressionBody(ArrowExpressionClause(body))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private MemberDeclarationSyntax GenerateGetTargetMethod(TargetFieldDescription targetField)
        {
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.ObjectKeyword)), "GetTarget")
                .WithParameterList(ParameterList())
                .WithExpressionBody(ArrowExpressionClause(IdentifierName(targetField.FieldName)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private MemberDeclarationSyntax GenerateGetArgumentMethod(
            InvokableMethodDescription methodDescription,
            List<InvokerFieldDescription> fields)
        {
            if (methodDescription.Method.Parameters.Length == 0)
                return null;

            var index = IdentifierName("index");

            var cases = new List<SwitchSectionSyntax>();
            foreach (var field in fields)
            {
                if (field is not MethodParameterFieldDescription parameter)
                {
                    continue;
                }

                // C#: case {index}: return {field}
                var label = CaseSwitchLabel(
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(parameter.ParameterOrdinal)));
                cases.Add(
                    SwitchSection(
                        SingletonList<SwitchLabelSyntax>(label),
                        new SyntaxList<StatementSyntax>(
                            ReturnStatement(
                                IdentifierName(parameter.FieldName)))));
            }

            // C#: default: return OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, {maxArgs})
            var throwHelperMethod = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("OrleansGeneratedCodeHelper"),
                IdentifierName("InvokableThrowArgumentOutOfRange"));
            cases.Add(
                SwitchSection(
                    SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel()),
                    new SyntaxList<StatementSyntax>(
                        ReturnStatement(
                            InvocationExpression(
                                throwHelperMethod,
                                ArgumentList(
                                    SeparatedList(
                                        new[]
                                        {
                                            Argument(index),
                                            Argument(
                                                LiteralExpression(
                                                    SyntaxKind.NumericLiteralExpression,
                                                    Literal(
                                                        Math.Max(0, methodDescription.Method.Parameters.Length - 1))))
                                        })))))));
            var body = SwitchStatement(index, new SyntaxList<SwitchSectionSyntax>(cases));
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.ObjectKeyword)), "GetArgument")
                .WithParameterList(
                    ParameterList(
                        SingletonSeparatedList(
                            Parameter(Identifier("index")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))))))
                .WithBody(Block(body))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private MemberDeclarationSyntax GenerateSetArgumentMethod(
            InvokableMethodDescription methodDescription,
            List<InvokerFieldDescription> fields)
        {
            if (methodDescription.Method.Parameters.Length == 0)
                return null;

            var index = IdentifierName("index");
            var value = IdentifierName("value");

            var cases = new List<SwitchSectionSyntax>();
            foreach (var field in fields)
            {
                if (field is not MethodParameterFieldDescription parameter)
                {
                    continue;
                }

                // C#: case {index}: {field} = (TField)value; return;
                var label = CaseSwitchLabel(
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(parameter.ParameterOrdinal)));
                cases.Add(
                    SwitchSection(
                        SingletonList<SwitchLabelSyntax>(label),
                        new SyntaxList<StatementSyntax>(
                            new StatementSyntax[]
                            {
                                ExpressionStatement(
                                    AssignmentExpression(
                                        SyntaxKind.SimpleAssignmentExpression,
                                        IdentifierName(parameter.FieldName),
                                        CastExpression(parameter.FieldType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions), value))),
                                ReturnStatement()
                            })));
            }

            // C#: default: OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, {maxArgs})
            var maxArgs = Math.Max(0, methodDescription.Method.Parameters.Length - 1);
            var throwHelperMethod = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("OrleansGeneratedCodeHelper"),
                IdentifierName("InvokableThrowArgumentOutOfRange"));
            cases.Add(
                SwitchSection(
                    SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel()),
                    new SyntaxList<StatementSyntax>(
                        new StatementSyntax[]
                        {
                            ExpressionStatement(
                                InvocationExpression(
                                    throwHelperMethod,
                                    ArgumentList(
                                        SeparatedList(
                                            new[]
                                            {
                                                Argument(index),
                                                Argument(
                                                    LiteralExpression(
                                                        SyntaxKind.NumericLiteralExpression,
                                                        Literal(maxArgs)))
                                            })))),
                            ReturnStatement()
                        })));
            var body = SwitchStatement(index, new SyntaxList<SwitchSectionSyntax>(cases));
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "SetArgument")
                .WithParameterList(
                    ParameterList(
                        SeparatedList(
                            new[]
                            {
                                Parameter(Identifier("index")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))),
                                Parameter(Identifier("value")).WithType(PredefinedType(Token(SyntaxKind.ObjectKeyword)))
                            }
                        )))
                .WithBody(Block(body))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private MemberDeclarationSyntax GenerateInvokeInnerMethod(
            InvokableMethodDescription method,
            List<InvokerFieldDescription> fields,
            TargetFieldDescription target)
        {
            var resultTask = IdentifierName("resultTask");

            // C# var resultTask = this.target.{Method}({params});
            var args = SeparatedList(
                fields.OfType<MethodParameterFieldDescription>()
                    .OrderBy(p => p.ParameterOrdinal)
                    .Select(p => Argument(IdentifierName(p.FieldName))));
            ExpressionSyntax methodCall;
            if (method.MethodTypeParameters.Count > 0)
            {
                methodCall = MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(target.FieldName),
                    GenericName(
                        Identifier(method.Method.Name),
                        TypeArgumentList(
                            SeparatedList<TypeSyntax>(
                                method.MethodTypeParameters.Select(p => IdentifierName(p.Name))))));
            }
            else
            {
                methodCall = IdentifierName(target.FieldName).Member(method.Method.Name);
            }

            return MethodDeclaration(method.Method.ReturnType.ToTypeSyntax(method.TypeParameterSubstitutions), "InvokeInner")
                .WithParameterList(ParameterList())
                .WithExpressionBody(ArrowExpressionClause(InvocationExpression(methodCall, ArgumentList(args))))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private MemberDeclarationSyntax GenerateDisposeMethod(
            List<InvokerFieldDescription> fields,
            INamedTypeSymbol baseClassType)
        {
            var body = new List<StatementSyntax>();
            foreach (var field in fields)
            {
                if (field.IsInstanceField)
                {
                    body.Add(
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName(field.FieldName),
                                LiteralExpression(SyntaxKind.DefaultLiteralExpression))));
                }
            }

            // C# base.Dispose();
            if (baseClassType is { }
                && baseClassType.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_IDisposable)
                && baseClassType.GetAllMembers<IMethodSymbol>("Dispose").FirstOrDefault(m => !m.IsAbstract && m.DeclaredAccessibility != Accessibility.Private) is { })
            {
                body.Add(ExpressionStatement(InvocationExpression(BaseExpression().Member("Dispose")).WithArgumentList(ArgumentList())));
            }

            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Dispose")
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithBody(Block(body));
        }

        private MemberDeclarationSyntax GenerateGetArgumentCount(InvokableMethodDescription methodDescription)
            => methodDescription.Method.Parameters.Length is var count and not 0 ?
            MethodDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)), "GetArgumentCount")
                .WithExpressionBody(ArrowExpressionClause(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(count))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)) : null;

        private MemberDeclarationSyntax GenerateGetActivityName(InvokableMethodDescription methodDescription)
        {
            // This property is intended to contain a value suitable for use as an OpenTelemetry Span Name for RPC calls.
            // Therefore, the interface name and method name components must not include periods or slashes.
            // In order to avoid that, we omit the namespace from the interface name.
            // See: https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/rpc.md

            var interfaceName = methodDescription.Method.ContainingType.ToDisplayName(methodDescription.TypeParameterSubstitutions, includeGlobalSpecifier: false, includeNamespace: false);
            var methodName = methodDescription.Method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var activityName = $"{interfaceName}/{methodName}";
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.StringKeyword)), "GetActivityName")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(activityName))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        private MemberDeclarationSyntax GenerateGetMethodName(
            InvokableMethodDescription methodDescription) =>
            MethodDeclaration(PredefinedType(Token(SyntaxKind.StringKeyword)), "GetMethodName")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(methodDescription.Method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private MemberDeclarationSyntax GenerateGetInterfaceName(
            InvokableMethodDescription methodDescription) =>
            MethodDeclaration(PredefinedType(Token(SyntaxKind.StringKeyword)), "GetInterfaceName")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(methodDescription.Method.ContainingType.ToDisplayName(methodDescription.TypeParameterSubstitutions, includeGlobalSpecifier: false)))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private MemberDeclarationSyntax GenerateGetInterfaceType(
            InvokableMethodDescription methodDescription) =>
            MethodDeclaration(LibraryTypes.Type.ToTypeSyntax(), "GetInterfaceType")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        TypeOfExpression(methodDescription.Method.ContainingType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private MemberDeclarationSyntax GenerateGetMethod()
            => MethodDeclaration(LibraryTypes.MethodInfo.ToTypeSyntax(), "GetMethod")
                .WithExpressionBody(ArrowExpressionClause(IdentifierName("MethodBackingField")))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        public static string GetSimpleClassName(InvokableMethodDescription method)
        {
            var genericArity = method.AllTypeParameters.Count;
            var typeArgs = genericArity > 0 ? "_" + genericArity : string.Empty;
            var proxyKey = method.ProxyBase.Key.GeneratedClassNameComponent;
            return $"Invokable_{method.ContainingInterface.Name}_{proxyKey}_{method.GeneratedMethodId}{typeArgs}";
        }

        private MemberDeclarationSyntax[] GetFieldDeclarations(
            InvokableMethodDescription method,
            List<InvokerFieldDescription> fieldDescriptions)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            MemberDeclarationSyntax GetFieldDeclaration(InvokerFieldDescription description)
            {
                FieldDeclarationSyntax field;
                if (description is MethodInfoFieldDescription methodInfo)
                {
                    var methodTypeArguments = GetTypesArray(method, method.MethodTypeParameters.Select(p => p.Parameter));
                    var parameterTypes = GetTypesArray(method, method.Method.Parameters.Select(p => p.Type));

                    field = FieldDeclaration(
                        VariableDeclaration(
                            LibraryTypes.MethodInfo.ToTypeSyntax(),
                            SingletonSeparatedList(VariableDeclarator(description.FieldName)
                            .WithInitializer(EqualsValueClause(
                                InvocationExpression(
                                    IdentifierName("OrleansGeneratedCodeHelper").Member("GetMethodInfoOrDefault"),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(TypeOfExpression(method.Method.ContainingType.ToTypeSyntax(method.TypeParameterSubstitutions))),
                                        Argument(method.Method.Name.GetLiteralExpression()),
                                        Argument(methodTypeArguments),
                                        Argument(parameterTypes),
                                    }))))))))
                        .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                }
                else
                {
                    field = FieldDeclaration(
                        VariableDeclaration(
                            description.FieldType.ToTypeSyntax(method.TypeParameterSubstitutions),
                            SingletonSeparatedList(VariableDeclarator(description.FieldName))));
                }

                switch (description)
                {
                    case MethodParameterFieldDescription _:
                        field = field.AddModifiers(Token(SyntaxKind.PublicKeyword));
                        break;
                }

                return field;
            }
        }

        private ExpressionSyntax GetTypesArray(InvokableMethodDescription method, IEnumerable<ITypeSymbol> typeSymbols)
        {
            var types = typeSymbols.ToArray();
            return types.Length == 0 ? LiteralExpression(SyntaxKind.NullLiteralExpression)
                : ImplicitArrayCreationExpression(InitializerExpression(SyntaxKind.ArrayInitializerExpression, SeparatedList<ExpressionSyntax>(
                    types.Select(t => TypeOfExpression(t.ToTypeSyntax(method.TypeParameterSubstitutions))))));
        }

        private (ConstructorDeclarationSyntax Constructor, List<TypeSyntax> ConstructorArguments) GenerateConstructor(
            string simpleClassName,
            InvokableMethodDescription method,
            INamedTypeSymbol baseClassType)
        {
            var parameters = new List<ParameterSyntax>();

            var body = new List<StatementSyntax>();

            List<TypeSyntax> constructorArgumentTypes = new();
            List<ArgumentSyntax> baseConstructorArguments = new();
            foreach (var constructor in baseClassType.GetAllMembers<IMethodSymbol>())
            {
                if (constructor.MethodKind != MethodKind.Constructor || constructor.DeclaredAccessibility == Accessibility.Private || constructor.IsImplicitlyDeclared)
                {
                    continue;
                }

                if (constructor.HasAttribute(LibraryTypes.GeneratedActivatorConstructorAttribute))
                {
                    var index = 0;
                    foreach (var parameter in constructor.Parameters)
                    {
                        var identifier = $"base{index}";

                        var argumentType = parameter.Type.ToTypeSyntax(method.TypeParameterSubstitutions);
                        constructorArgumentTypes.Add(argumentType);
                        parameters.Add(Parameter(identifier.ToIdentifier()).WithType(argumentType));
                        baseConstructorArguments.Add(Argument(identifier.ToIdentifierName()));
                        index++;
                    }
                    break;
                }
            }

            foreach (var (methodName, methodArgument) in method.CustomInitializerMethods)
            {
                var argumentExpression = methodArgument.ToExpression();
                body.Add(ExpressionStatement(InvocationExpression(IdentifierName(methodName), ArgumentList(SeparatedList(new[] { Argument(argumentExpression) })))));
            }

            if (body.Count == 0 && parameters.Count == 0)
                return default;

            var constructorDeclaration = ConstructorDeclaration(simpleClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .WithInitializer(
                    ConstructorInitializer(
                        SyntaxKind.BaseConstructorInitializer,
                        ArgumentList(SeparatedList(baseConstructorArguments))))
                .AddBodyStatements(body.ToArray());

            return (constructorDeclaration, constructorArgumentTypes);
        }

        private List<InvokerFieldDescription> GetFieldDescriptions(InvokableMethodDescription method)
        {
            var fields = new List<InvokerFieldDescription>();

            uint fieldId = 0;
            foreach (var parameter in method.Method.Parameters)
            {
                fields.Add(new MethodParameterFieldDescription(method.CodeGenerator, parameter, $"arg{fieldId}", fieldId, method.TypeParameterSubstitutions));
                fieldId++;
            }

            fields.Add(new TargetFieldDescription(method.Method.ContainingType));
            fields.Add(new MethodInfoFieldDescription(LibraryTypes.MethodInfo, "MethodBackingField"));

            return fields;
        }

        internal abstract class InvokerFieldDescription
        {
            protected InvokerFieldDescription(ITypeSymbol fieldType, string fieldName)
            {
                FieldType = fieldType;
                FieldName = fieldName;
            }

            public ITypeSymbol FieldType { get; }
            public string FieldName { get; }
            public abstract bool IsSerializable { get; }
            public abstract bool IsInstanceField { get; }
        }

        internal sealed class TargetFieldDescription : InvokerFieldDescription
        {
            public TargetFieldDescription(ITypeSymbol fieldType) : base(fieldType, "target") { }

            public override bool IsSerializable => false;
            public override bool IsInstanceField => true;
        }

        internal class MethodParameterFieldDescription : InvokerFieldDescription, IMemberDescription
        {
            public MethodParameterFieldDescription(
                CodeGenerator codeGenerator,
                IParameterSymbol parameter,
                string fieldName,
                uint fieldId,
                Dictionary<ITypeParameterSymbol, string> typeParameterSubstitutions)
                : base(parameter.Type, fieldName)
            {
                TypeParameterSubstitutions = typeParameterSubstitutions;
                FieldId = fieldId;
                CodeGenerator = codeGenerator;
                Parameter = parameter;
                if (parameter.Type.TypeKind == TypeKind.Dynamic)
                {
                    TypeSyntax = PredefinedType(Token(SyntaxKind.ObjectKeyword));
                    TypeName = "dynamic";
                }
                else
                {
                    TypeName = Type.ToDisplayName(TypeParameterSubstitutions);
                    TypeSyntax = Type.ToTypeSyntax(TypeParameterSubstitutions);
                }

                Symbol = parameter;
            }

            public CodeGenerator CodeGenerator { get; }
            public ISymbol Symbol { get; }
            public Dictionary<ITypeParameterSymbol, string> TypeParameterSubstitutions { get; }
            public int ParameterOrdinal => Parameter.Ordinal;
            public uint FieldId { get; }
            public ISymbol Member => Parameter;
            public ITypeSymbol Type => FieldType;
            public INamedTypeSymbol ContainingType => Parameter.ContainingType;
            public TypeSyntax TypeSyntax { get; }
            public IParameterSymbol Parameter { get; }
            public override bool IsSerializable => true;
            public override bool IsInstanceField => true;

            public string AssemblyName => Parameter.Type.ContainingAssembly.ToDisplayName();
            public string TypeName { get; }

            public string TypeNameIdentifier
            {
                get
                {
                    if (Type is ITypeParameterSymbol tp && TypeParameterSubstitutions.TryGetValue(tp, out var name))
                    {
                        return name;
                    }

                    return Type.GetValidIdentifier();
                }
            }

            public bool IsPrimaryConstructorParameter => false;

            public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax(TypeParameterSubstitutions);
        }

        internal sealed class MethodInfoFieldDescription : InvokerFieldDescription
        {
            public MethodInfoFieldDescription(ITypeSymbol fieldType, string fieldName) : base(fieldType, fieldName) { }

            public override bool IsSerializable => false;
            public override bool IsInstanceField => false;
        }
    }
}
