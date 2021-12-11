using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal static class InvokableGenerator
    {
        public static (ClassDeclarationSyntax, GeneratedInvokerDescription) Generate(
            LibraryTypes libraryTypes,
            InvokableInterfaceDescription interfaceDescription,
            MethodDescription method)
        {
            var generatedClassName = GetSimpleClassName(interfaceDescription, method);
            INamedTypeSymbol baseClassType = GetBaseClassType(method);
            var fieldDescriptions = GetFieldDescriptions(method, interfaceDescription);
            var fields = GetFieldDeclarations(method, fieldDescriptions, libraryTypes);
            var (ctor, ctorArgs) = GenerateConstructor(libraryTypes, generatedClassName, method, fieldDescriptions, baseClassType);

            Accessibility accessibility = GetAccessibility(interfaceDescription);

            var targetField = fieldDescriptions.OfType<TargetFieldDescription>().Single();

            var accessibilityKind = accessibility switch
            {
                Accessibility.Public => SyntaxKind.PublicKeyword,
                _ => SyntaxKind.InternalKeyword,
            };
            var classDeclaration = ClassDeclaration(generatedClassName)
                .AddBaseListTypes(SimpleBaseType(baseClassType.ToTypeSyntax(method.TypeParameterSubstitutions)))
                .AddModifiers(Token(accessibilityKind), Token(SyntaxKind.SealedKeyword), Token(SyntaxKind.PartialKeyword))
                .AddAttributeLists(
                    AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(fields)
                .AddMembers(ctor)
                .AddMembers(
                    GenerateGetArgumentCount(libraryTypes, method),
                    GenerateGetMethodName(libraryTypes, method),
                    GenerateGetInterfaceName(libraryTypes, method),
                    GenerateGetInterfaceType(libraryTypes, method),
                    GenerateGetInterfaceTypeArguments(libraryTypes, method),
                    GenerateGetMethodTypeArguments(libraryTypes, method),
                    GenerateGetParameterTypes(libraryTypes, method),
                    GenerateGetMethod(libraryTypes),
                    GenerateSetTargetMethod(libraryTypes, interfaceDescription, targetField),
                    GenerateGetTargetMethod(targetField),
                    GenerateDisposeMethod(libraryTypes, method, fieldDescriptions, baseClassType),
                    GenerateGetArgumentMethod(libraryTypes, method, fieldDescriptions),
                    GenerateSetArgumentMethod(libraryTypes, method, fieldDescriptions),
                    GenerateInvokeInnerMethod(libraryTypes, method, fieldDescriptions, targetField));

            var typeParametersWithNames = method.AllTypeParameters;
            if (typeParametersWithNames.Count > 0)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, typeParametersWithNames);
            }

            List<INamedTypeSymbol> serializationHooks = new();
            if (baseClassType.GetAttributes(libraryTypes.SerializationCallbacksAttribute, out var hookAttributes))
            {
                foreach (var hookAttribute in hookAttributes)
                {
                    var hookType = (INamedTypeSymbol)hookAttribute.ConstructorArguments[0].Value;
                    serializationHooks.Add(hookType);
                }
            }

            var invokerDescription = new GeneratedInvokerDescription(
                interfaceDescription,
                method,
                accessibility,
                generatedClassName,
                fieldDescriptions.OfType<IMemberDescription>().ToList(),
                serializationHooks,
                baseClassType,
                ctorArgs);
            return (classDeclaration, invokerDescription);

            static Accessibility GetAccessibility(InvokableInterfaceDescription interfaceDescription)
            {
                var t = interfaceDescription.InterfaceType;
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

        private static INamedTypeSymbol GetBaseClassType(MethodDescription method)
        {
            var methodReturnType = (INamedTypeSymbol)method.Method.ReturnType;
            if (method.InvokableBaseTypes.TryGetValue(methodReturnType, out var baseClassType))
            {
                return baseClassType;
            }

            if (methodReturnType.ConstructedFrom is { } constructedFrom)
            {
                var unbound = constructedFrom.ConstructUnboundGenericType();
                if (method.InvokableBaseTypes.TryGetValue(unbound, out baseClassType))
                {
                    return baseClassType.ConstructedFrom.Construct(methodReturnType.TypeArguments.ToArray());
                }
            }
            
            throw new InvalidOperationException($"Unsupported return type {methodReturnType} for method {method.Method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
        }

        private static MemberDeclarationSyntax GenerateSetTargetMethod(
            LibraryTypes libraryTypes,
            InvokableInterfaceDescription interfaceDescription,
            TargetFieldDescription targetField)
        {
            var type = IdentifierName("TTargetHolder");
            var typeToken = type.Identifier;
            var holder = IdentifierName("holder");
            var holderParameter = holder.Identifier;

            var getTarget = InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        holder,
                        GenericName(interfaceDescription.IsExtension ? "GetComponent" : "GetTarget")
                            .WithTypeArgumentList(
                                TypeArgumentList(
                                    SingletonSeparatedList(interfaceDescription.InterfaceType.ToTypeSyntax())))))
                .WithArgumentList(ArgumentList());

            var body =
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    ThisExpression().Member(targetField.FieldName),
                    getTarget);
            return MethodDeclaration(libraryTypes.Void.ToTypeSyntax(), "SetTarget")
                .WithTypeParameterList(TypeParameterList(SingletonSeparatedList(TypeParameter(typeToken))))
                .WithParameterList(ParameterList(SingletonSeparatedList(Parameter(holderParameter).WithType(type))))
                .WithExpressionBody(ArrowExpressionClause(body))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateGetTargetMethod(
            TargetFieldDescription targetField)
        {
            var type = IdentifierName("TTarget");
            var typeToken = type.Identifier;

            var body = CastExpression(type, ThisExpression().Member(targetField.FieldName));
            return MethodDeclaration(type, "GetTarget")
                .WithTypeParameterList(TypeParameterList(SingletonSeparatedList(TypeParameter(typeToken))))
                .WithParameterList(ParameterList())
                .WithExpressionBody(ArrowExpressionClause(body))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateGetArgumentMethod(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription,
            List<InvokerFieldDescripton> fields)
        {
            var index = IdentifierName("index");
            var type = IdentifierName("TArgument");
            var typeToken = type.Identifier;

            var cases = new List<SwitchSectionSyntax>();
            foreach (var field in fields)
            {
                if (field is not MethodParameterFieldDescription parameter)
                {
                    continue;
                }

                // C#: case {index}: return (TArgument)(object){field}
                var label = CaseSwitchLabel(
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(parameter.ParameterOrdinal)));
                cases.Add(
                    SwitchSection(
                        SingletonList<SwitchLabelSyntax>(label),
                        new SyntaxList<StatementSyntax>(
                            ReturnStatement(
                                CastExpression(
                                    type,
                                    CastExpression(
                                        libraryTypes.Object.ToTypeSyntax(),
                                        ThisExpression().Member(parameter.FieldName)))))));
            }

            // C#: default: return OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange<TArgument>(index, {maxArgs})
            var throwHelperMethod = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("OrleansGeneratedCodeHelper"),
                GenericName("InvokableThrowArgumentOutOfRange")
                    .WithTypeArgumentList(
                        TypeArgumentList(
                            SingletonSeparatedList<TypeSyntax>(type))));
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
            var body = SwitchStatement(ParenthesizedExpression(index), new SyntaxList<SwitchSectionSyntax>(cases));
            return MethodDeclaration(type, "GetArgument")
                .WithTypeParameterList(TypeParameterList(SingletonSeparatedList(TypeParameter(typeToken))))
                .WithParameterList(
                    ParameterList(
                        SingletonSeparatedList(
                            Parameter(Identifier("index")).WithType(libraryTypes.Int32.ToTypeSyntax()))))
                .WithBody(Block(body))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateSetArgumentMethod(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription,
            List<InvokerFieldDescripton> fields)
        {
            var index = IdentifierName("index");
            var value = IdentifierName("value");
            var type = IdentifierName("TArgument");
            var typeToken = type.Identifier;

            var cases = new List<SwitchSectionSyntax>();
            foreach (var field in fields)
            {
                if (field is not MethodParameterFieldDescription parameter)
                {
                    continue;
                }

                // C#: case {index}: {field} = (TField)(object)value; return;
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
                                        ThisExpression().Member(parameter.FieldName),
                                        CastExpression(
                                            parameter.FieldType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions),
                                            CastExpression(
                                                libraryTypes.Object.ToTypeSyntax(),
                                                value
                                            )))),
                                ReturnStatement()
                            })));
            }

            // C#: default: return OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange<TArgument>(index, {maxArgs})
            var maxArgs = Math.Max(0, methodDescription.Method.Parameters.Length - 1);
            var throwHelperMethod = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("OrleansGeneratedCodeHelper"),
                GenericName("InvokableThrowArgumentOutOfRange")
                    .WithTypeArgumentList(
                        TypeArgumentList(
                            SingletonSeparatedList<TypeSyntax>(type))));
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
            var body = SwitchStatement(ParenthesizedExpression(index), new SyntaxList<SwitchSectionSyntax>(cases));
            return MethodDeclaration(libraryTypes.Void.ToTypeSyntax(), "SetArgument")
                .WithTypeParameterList(TypeParameterList(SingletonSeparatedList(TypeParameter(typeToken))))
                .WithParameterList(
                    ParameterList(
                        SeparatedList(
                            new[]
                            {
                                Parameter(Identifier("index")).WithType(libraryTypes.Int32.ToTypeSyntax()),
                                Parameter(Identifier("value"))
                                    .WithType(type)
                                    .WithModifiers(TokenList(Token(SyntaxKind.InKeyword)))
                            }
                        )))
                .WithBody(Block(body))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateInvokeInnerMethod(
            LibraryTypes libraryTypes,
            MethodDescription method,
            List<InvokerFieldDescripton> fields,
            TargetFieldDescription target)
        {
            var resultTask = IdentifierName("resultTask");

            // C# var resultTask = this.target.{Method}({params});
            var args = SeparatedList(
                fields.OfType<MethodParameterFieldDescription>()
                    .OrderBy(p => p.ParameterOrdinal)
                    .Select(p => Argument(ThisExpression().Member(p.FieldName))));
            ExpressionSyntax methodCall;
            if (method.MethodTypeParameters.Count > 0)
            {
                methodCall = MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    ThisExpression().Member(target.FieldName),
                    GenericName(
                        Identifier(method.Method.Name),
                        TypeArgumentList(
                            SeparatedList<TypeSyntax>(
                                method.MethodTypeParameters.Select(p => IdentifierName(p.Name))))));
            }
            else
            {
                methodCall = ThisExpression().Member(target.FieldName).Member(method.Method.Name);
            }

            return MethodDeclaration(method.Method.ReturnType.ToTypeSyntax(method.TypeParameterSubstitutions), "InvokeInner")
                .WithParameterList(ParameterList())
                .WithExpressionBody(ArrowExpressionClause(InvocationExpression(methodCall, ArgumentList(args))))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateDisposeMethod(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription,
            List<InvokerFieldDescripton> fields,
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
                                ThisExpression().Member(field.FieldName),
                                DefaultExpression(field.FieldType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions)))));
                }
            }

            // C# base.Dispose();
            if (baseClassType is { }
                && baseClassType.AllInterfaces.Contains(libraryTypes.IDisposable)
                && baseClassType.GetAllMembers<IMethodSymbol>("Dispose").FirstOrDefault(m => !m.IsAbstract && m.DeclaredAccessibility != Accessibility.Private) is { })
            {
                body.Add(ExpressionStatement(InvocationExpression(BaseExpression().Member("Dispose")).WithArgumentList(ArgumentList())));
            }

            return MethodDeclaration(libraryTypes.Void.ToTypeSyntax(), "Dispose")
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithBody(Block(body));
        }

        private static MemberDeclarationSyntax GenerateGetArgumentCount(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
            PropertyDeclaration(libraryTypes.Int32.ToTypeSyntax(), "ArgumentCount")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(methodDescription.Method.Parameters.Length))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private static MemberDeclarationSyntax GenerateGetMethodName(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
            PropertyDeclaration(PredefinedType(Token(SyntaxKind.StringKeyword)), "MethodName")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(methodDescription.Method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private static MemberDeclarationSyntax GenerateGetInterfaceName(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
            PropertyDeclaration(PredefinedType(Token(SyntaxKind.StringKeyword)), "InterfaceName")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(methodDescription.Method.ContainingType.ToDisplayName(methodDescription.TypeParameterSubstitutions, includeGlobalSpecifier: false)))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private static MemberDeclarationSyntax GenerateGetInterfaceType(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
            PropertyDeclaration(libraryTypes.Type.ToTypeSyntax(), "InterfaceType")
                .WithExpressionBody(
                    ArrowExpressionClause(
                        TypeOfExpression(methodDescription.Method.ContainingType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions))))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private static MemberDeclarationSyntax GenerateGetInterfaceTypeArguments(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
                GenerateGetTypeArrayHelper(libraryTypes, "InterfaceTypeArguments", "InterfaceTypeArgumentsBackingField");

        private static MemberDeclarationSyntax GenerateGetMethodTypeArguments(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
                GenerateGetTypeArrayHelper(libraryTypes, "MethodTypeArguments", "MethodTypeArgumentsBackingField");

        private static MemberDeclarationSyntax GenerateGetParameterTypes(
            LibraryTypes libraryTypes,
            MethodDescription methodDescription) =>
                GenerateGetTypeArrayHelper(libraryTypes, "ParameterTypes", "ParameterTypesBackingField");

        private static MemberDeclarationSyntax GenerateGetTypeArrayHelper(
            LibraryTypes libraryTypes,
            string propertyName,
            string backingPropertyName)
            => PropertyDeclaration(ArrayType(libraryTypes.Type.ToTypeSyntax(), SingletonList(ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression())))), propertyName)
                .WithExpressionBody(ArrowExpressionClause(IdentifierName(backingPropertyName)))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        private static MemberDeclarationSyntax GenerateGetMethod(
            LibraryTypes libraryTypes)
            => PropertyDeclaration(libraryTypes.MethodInfo.ToTypeSyntax(), "Method")
                .WithExpressionBody(ArrowExpressionClause(IdentifierName("MethodBackingField")))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        public static string GetSimpleClassName(InvokableInterfaceDescription interfaceDescription, MethodDescription method)
        {
            var genericArity = method.AllTypeParameters.Count;
            var typeArgs = genericArity > 0 ? "_" + genericArity : string.Empty;
            return $"Invokable_{interfaceDescription.Name}_{interfaceDescription.ProxyBaseType.Name}_{method.Name}{typeArgs}";
        }

        private static MemberDeclarationSyntax[] GetFieldDeclarations(
            MethodDescription method,
            List<InvokerFieldDescripton> fieldDescriptions,
            LibraryTypes libraryTypes)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            MemberDeclarationSyntax GetFieldDeclaration(InvokerFieldDescripton description)
            {
                FieldDeclarationSyntax field;
                if (description is TypeArrayFieldDescription types)
                {
                    field = FieldDeclaration(
                        VariableDeclaration(
                            ArrayType(libraryTypes.Type.ToTypeSyntax(), SingletonList(ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression())))),
                            SingletonSeparatedList(VariableDeclarator(description.FieldName)
                            .WithInitializer(EqualsValueClause(ArrayCreationExpression(
                                ArrayType(libraryTypes.Type.ToTypeSyntax(), SingletonList(ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression())))),
                                InitializerExpression(SyntaxKind.ArrayInitializerExpression, SeparatedList<ExpressionSyntax>(types.Values.Select(t => TypeOfExpression(t))))))))))
                        .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                }
                else if (description is MethodInfoFieldDescription methodInfo)
                {
                    field = FieldDeclaration(
                        VariableDeclaration(
                            libraryTypes.MethodInfo.ToTypeSyntax(),
                            SingletonSeparatedList(VariableDeclarator(description.FieldName)
                            .WithInitializer(EqualsValueClause(
                                InvocationExpression(
                                    IdentifierName("OrleansGeneratedCodeHelper").Member("GetMethodInfoOrDefault"),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(TypeOfExpression(method.Method.ContainingType.ToTypeSyntax(method.TypeParameterSubstitutions))),
                                        Argument(method.Method.Name.GetLiteralExpression()),
                                        Argument(IdentifierName("MethodTypeArgumentsBackingField")),
                                        Argument(IdentifierName("ParameterTypesBackingField")),
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

                if (!description.IsSerializable)
                {
                    field = field.AddAttributeLists(
                            AttributeList()
                                .AddAttributes(Attribute(libraryTypes.NonSerializedAttribute.ToNameSyntax())));
                }
                else if (description is MethodParameterFieldDescription parameter)
                {
                    field = field.AddAttributeLists(
                        AttributeList()
                            .AddAttributes(
                                Attribute(
                                    libraryTypes.IdAttributeTypes[0].ToNameSyntax(),
                                    AttributeArgumentList()
                                        .AddArguments(
                                            AttributeArgument(
                                                LiteralExpression(
                                                    SyntaxKind.NumericLiteralExpression,
                                                    Literal(parameter.FieldId)))))));
                }

                return field;
            }
        }

        private static (ConstructorDeclarationSyntax Constructor, List<TypeSyntax> ConstructorArguments) GenerateConstructor(
            LibraryTypes libraryTypes,
            string simpleClassName,
            MethodDescription method,
            List<InvokerFieldDescripton> fieldDescriptions,
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

                if (constructor.HasAttribute(libraryTypes.GeneratedActivatorConstructorAttribute))
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
                body.Add(ExpressionStatement(InvocationExpression(ThisExpression().Member(methodName), ArgumentList(SeparatedList(new[] { Argument(argumentExpression) })))));
            }

            var constructorDeclaration = ConstructorDeclaration(simpleClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .WithInitializer(
                    ConstructorInitializer(
                        SyntaxKind.BaseConstructorInitializer,
                        ArgumentList(SeparatedList(baseConstructorArguments))))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(Attribute(libraryTypes.GeneratedActivatorConstructorAttribute.ToNameSyntax()))))
                .AddBodyStatements(body.ToArray());

            return (constructorDeclaration, constructorArgumentTypes);
        }

        private static List<InvokerFieldDescripton> GetFieldDescriptions(
            MethodDescription method,
            InvokableInterfaceDescription interfaceDescription)
        {
            var fields = new List<InvokerFieldDescripton>();

            ushort fieldId = 0;
            foreach (var parameter in method.Method.Parameters)
            {
                fields.Add(new MethodParameterFieldDescription(method, parameter, $"arg{fieldId}", fieldId));
                fieldId++;
            }

            fields.Add(new TargetFieldDescription(method, interfaceDescription.InterfaceType));

            var methodTypeArguments = method.MethodTypeParameters.Select(p => p.Parameter.ToTypeSyntax(method.TypeParameterSubstitutions)).ToArray();
            fields.Add(new TypeArrayFieldDescription(method, interfaceDescription.CodeGenerator.LibraryTypes.Type, "MethodTypeArgumentsBackingField", methodTypeArguments));

            var interfaceTypeArguments = method.Method.ContainingType.TypeArguments.Select(p => p.ToTypeSyntax(method.TypeParameterSubstitutions)).ToArray();
            fields.Add(new TypeArrayFieldDescription(method, interfaceDescription.CodeGenerator.LibraryTypes.Type, "InterfaceTypeArgumentsBackingField", interfaceTypeArguments));

            var methodParameterTypes = method.Method.Parameters.Select(p => p.Type.ToTypeSyntax(method.TypeParameterSubstitutions)).ToArray();
            fields.Add(new TypeArrayFieldDescription(method, interfaceDescription.CodeGenerator.LibraryTypes.Type, "ParameterTypesBackingField", methodParameterTypes));

            fields.Add(new MethodInfoFieldDescription(method, interfaceDescription.CodeGenerator.LibraryTypes.MethodInfo, "MethodBackingField"));

            return fields;
        }

        internal abstract class InvokerFieldDescripton
        {
            protected InvokerFieldDescripton(ITypeSymbol fieldType, string fieldName)
            {
                FieldType = fieldType;
                FieldName = fieldName;
            }

            public ITypeSymbol FieldType { get; }
            public abstract TypeSyntax FieldTypeSyntax { get; }
            public string FieldName { get; }
            public abstract bool IsSerializable { get; }
            public abstract bool IsInstanceField { get; }
        }

        internal class TargetFieldDescription : InvokerFieldDescripton
        {
            private readonly MethodDescription _method;

            public TargetFieldDescription(MethodDescription method, ITypeSymbol fieldType) : base(fieldType, "target")
            {
                _method = method;
            }

            public override bool IsSerializable => false;
            public override TypeSyntax FieldTypeSyntax => FieldType.ToTypeSyntax(_method.TypeParameterSubstitutions);
            public override bool IsInstanceField => true;
        }

        internal class MethodParameterFieldDescription : InvokerFieldDescripton, IMemberDescription
        {
            public MethodParameterFieldDescription(MethodDescription method, IParameterSymbol parameter, string fieldName, ushort fieldId)
                : base(parameter.Type, fieldName)
            {
                Method = method;
                FieldId = fieldId;
                Parameter = parameter;
                if (parameter.Type.TypeKind == TypeKind.Dynamic)
                {
                    TypeSyntax = PredefinedType(Token(SyntaxKind.ObjectKeyword));
                    TypeName = "dynamic";
                }
                else
                {
                    TypeName = Type.ToDisplayName(method.TypeParameterSubstitutions);
                    TypeSyntax = Type.ToTypeSyntax(method.TypeParameterSubstitutions);
                }

                FieldTypeSyntax = TypeSyntax;
                Symbol = parameter;
            }

            public ISymbol Symbol { get; }
            public MethodDescription Method { get; }
            public int ParameterOrdinal => Parameter.Ordinal;
            public ushort FieldId { get; }
            public ISymbol Member => Parameter;
            public ITypeSymbol Type => FieldType;
            public INamedTypeSymbol ContainingType => Parameter.ContainingType;
            public TypeSyntax TypeSyntax { get; }
            public IParameterSymbol Parameter { get; }
            public override bool IsSerializable => true;
            public override bool IsInstanceField => true;
            public override TypeSyntax FieldTypeSyntax { get; }

            public string AssemblyName => Parameter.Type.ContainingAssembly.ToDisplayName();
            public string TypeName { get; }

            public string TypeNameIdentifier
            {
                get
                {
                    if (Type is ITypeParameterSymbol tp && Method.TypeParameterSubstitutions.TryGetValue(tp, out var name))
                    {
                        return name;
                    }

                    return Type.GetValidIdentifier();
                }
            }

            public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax(Method.TypeParameterSubstitutions);
        }

        internal class TypeArrayFieldDescription : InvokerFieldDescripton
        {
            private readonly MethodDescription _method;

            public TypeArrayFieldDescription(MethodDescription method, ITypeSymbol fieldType, string fieldName, TypeSyntax[] values) : base(fieldType, fieldName)
            {
                _method = method;
                Values = values;
            }

            public TypeSyntax[] Values { get; }
            public override bool IsSerializable => false;
            public override bool IsInstanceField => false;
            public override TypeSyntax FieldTypeSyntax => FieldType.ToTypeSyntax(_method.TypeParameterSubstitutions);
        }

        internal class MethodInfoFieldDescription : InvokerFieldDescripton
        {
            private readonly MethodDescription _method;

            public MethodInfoFieldDescription(MethodDescription method, ITypeSymbol fieldType, string fieldName) : base(fieldType, fieldName)
            {
                _method = method;
            }

            public override bool IsSerializable => false;
            public override bool IsInstanceField => false;
            public override TypeSyntax FieldTypeSyntax => FieldType.ToTypeSyntax(_method.TypeParameterSubstitutions);
        }
    }
}