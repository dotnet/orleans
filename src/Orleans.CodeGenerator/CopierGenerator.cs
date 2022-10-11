using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator
{
    internal static class CopierGenerator
    {
        private const string BaseTypeCopierFieldName = "_baseTypeCopier";
        private const string ActivatorFieldName = "_activator";
        private const string DeepCopyMethodName = "DeepCopy";

        public static ClassDeclarationSyntax GenerateCopier(
            LibraryTypes libraryTypes,
            ISerializableTypeDescription type,
            Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers)
        {
            var isShallowCopyable = type.IsShallowCopyable;
            if (isShallowCopyable && !type.IsGenericType)
            {
                defaultCopiers.Add(type, GetDefaultCopier("DefaultShallowCopier", SingletonSeparatedList(type.TypeSyntax)));
                return null;
            }

            var simpleClassName = GetSimpleClassName(type);

            var members = new List<ISerializableMember>();
            foreach (var member in type.Members)
            {
                if (member is ISerializableMember serializable)
                {
                    members.Add(serializable);
                }
                else if (member is IFieldDescription or IPropertyDescription)
                {
                    members.Add(new SerializableMember(libraryTypes, type, member, members.Count));
                }
                else if (member is MethodParameterFieldDescription methodParameter)
                {
                    members.Add(new SerializableMethodMember(methodParameter));
                }
            }

            var accessibility = type.Accessibility switch
            {
                Accessibility.Public => SyntaxKind.PublicKeyword,
                _ => SyntaxKind.InternalKeyword,
            };
            var classDeclaration = ClassDeclaration(simpleClassName)
                .AddBaseListTypes(SimpleBaseType(libraryTypes.DeepCopier_1.ToTypeSyntax(type.TypeSyntax)))
                .AddModifiers(Token(accessibility), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())));

            if (isShallowCopyable)
            {
                var copyMethod = GenerateImmutableTypeCopyMethod(type, libraryTypes);
                classDeclaration = classDeclaration.AddMembers(copyMethod);
                classDeclaration = classDeclaration.AddBaseListTypes(SimpleBaseType(libraryTypes.IOptionalDeepCopier.ToTypeSyntax()));
            }
            else
            {
                var fieldDescriptions = GetFieldDescriptions(type, members, libraryTypes, out var onlyDeepFields);
                var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
                var ctor = GenerateConstructor(libraryTypes, simpleClassName, fieldDescriptions);

                var copyMethod = GenerateMemberwiseDeepCopyMethod(type, fieldDescriptions, members, libraryTypes, onlyDeepFields);
                classDeclaration = classDeclaration
                    .AddMembers(copyMethod)
                    .AddMembers(fieldDeclarations);

                if (ctor != null)
                    classDeclaration = classDeclaration.AddMembers(ctor);

                if (!type.IsSealedType)
                {
                    classDeclaration = classDeclaration
                        .AddMembers(GenerateBaseCopierDeepCopyMethod(type, fieldDescriptions, members, libraryTypes))
                        .AddBaseListTypes(SimpleBaseType(libraryTypes.BaseCopier_1.ToTypeSyntax(type.TypeSyntax)));
                }
            }

            if (type.IsGenericType)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, type.TypeParameters);
            }

            return classDeclaration;
        }

        private static TypeSyntax GetDefaultCopier(string name, SeparatedSyntaxList<TypeSyntax> args)
            => QualifiedName(IdentifierName("OrleansGeneratedCodeHelper"), GenericName(name).WithTypeArgumentList(TypeArgumentList(args)));

        public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

        public static string GetSimpleClassName(string name) => $"Copier_{name}";

        public static string GetGeneratedNamespaceName(ITypeSymbol type) => type.GetNamespaceAndNesting() switch
        {
            { Length: > 0 } ns => $"{CodeGenerator.CodeGeneratorName}.{ns}",
            _ => CodeGenerator.CodeGeneratorName
        };

        private static MemberDeclarationSyntax[] GetFieldDeclarations(List<GeneratedFieldDescription> fieldDescriptions)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            static MemberDeclarationSyntax GetFieldDeclaration(GeneratedFieldDescription description)
            {
                switch (description)
                {
                    case FieldAccessorDescription accessor:
                        return
                            FieldDeclaration(VariableDeclaration(accessor.FieldType,
                                SingletonSeparatedList(VariableDeclarator(accessor.FieldName).WithInitializer(EqualsValueClause(accessor.InitializationSyntax)))))
                                .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                    default:
                        return FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                }
            }
        }

        private static ConstructorDeclarationSyntax GenerateConstructor(LibraryTypes libraryTypes, string simpleClassName, List<GeneratedFieldDescription> fieldDescriptions)
        {
            var codecProviderAdded = false;
            var parameters = new List<ParameterSyntax>();
            var statements = new List<StatementSyntax>();
            foreach (var field in fieldDescriptions)
            {
                switch (field)
                {
                    case GeneratedFieldDescription _ when field.IsInjected:
                        parameters.Add(Parameter(field.FieldName.ToIdentifier()).WithType(field.FieldType));
                        statements.Add(ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                ThisExpression().Member(field.FieldName.ToIdentifierName()),
                                Unwrapped(field.FieldName.ToIdentifierName()))));
                        break;
                    case CopierFieldDescription or BaseCopierFieldDescription when !field.IsInjected:
                        if (!codecProviderAdded)
                        {
                            parameters.Add(Parameter(Identifier("codecProvider")).WithType(libraryTypes.ICodecProvider.ToTypeSyntax()));
                            codecProviderAdded = true;
                        }

                        var copier = InvocationExpression(
                            IdentifierName("OrleansGeneratedCodeHelper").Member(GenericName(Identifier("GetService"), TypeArgumentList(SingletonSeparatedList(field.FieldType)))),
                            ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(IdentifierName("codecProvider")) })));

                        statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, field.FieldName.ToIdentifierName(), copier)));
                        break;
                }
            }

            return statements.Count == 0 ? null : ConstructorDeclaration(simpleClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .AddBodyStatements(statements.ToArray());

            static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
            {
                return InvocationExpression(
                    IdentifierName("OrleansGeneratedCodeHelper").Member("UnwrapService"),
                    ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(expr) })));
            }
        }

        private static List<GeneratedFieldDescription> GetFieldDescriptions(
            ISerializableTypeDescription serializableTypeDescription,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes,
            out bool onlyDeepFields)
        {
            var serializationHooks = serializableTypeDescription.SerializationHooks;
            onlyDeepFields = serializableTypeDescription.IsValueType && serializationHooks.Count == 0;

            var fields = new List<GeneratedFieldDescription>();

            if (serializableTypeDescription.HasComplexBaseType)
            {
                fields.Add(GetBaseTypeField(serializableTypeDescription, libraryTypes));
            }

            if (serializableTypeDescription.UseActivator && !serializableTypeDescription.IsAbstractType)
            {
                onlyDeepFields = false;
                fields.Add(new ActivatorFieldDescription(libraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
            }

            // Add a copier field for any field in the target which does not have a static copier.
            GetCopierFieldDescriptions(serializableTypeDescription.Members, libraryTypes, fields);

            foreach (var member in members)
            {
                if (onlyDeepFields && member.IsShallowCopyable) continue;

                if (member.GetGetterFieldDescription() is { } getterFieldDescription)
                {
                    fields.Add(getterFieldDescription);
                }

                if (member.GetSetterFieldDescription() is { } setterFieldDescription)
                {
                    fields.Add(setterFieldDescription);
                }
            }

            for (var hookIndex = 0; hookIndex < serializationHooks.Count; ++hookIndex)
            {
                var hookType = serializationHooks[hookIndex];
                fields.Add(new SerializationHookFieldDescription(hookType.ToTypeSyntax(), $"_hook{hookIndex}"));
            }

            return fields;
        }

        private static BaseCopierFieldDescription GetBaseTypeField(ISerializableTypeDescription serializableTypeDescription, LibraryTypes libraryTypes)
        {
            var baseType = serializableTypeDescription.BaseType;
            if (baseType.HasAttribute(libraryTypes.GenerateSerializerAttribute)
                && (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, libraryTypes.Compilation.Assembly) || baseType.ContainingAssembly.HasAttribute(libraryTypes.TypeManifestProviderAttribute))
                && baseType is not INamedTypeSymbol { IsGenericType: true })
            {
                // Use the concrete generated type and avoid expensive interface dispatch (except for generic types that will fall back to IBaseCopier<T>)
                return new(QualifiedName(ParseName(GetGeneratedNamespaceName(baseType)), IdentifierName(GetSimpleClassName(baseType.Name))), true);
            }

            return new(libraryTypes.BaseCopier_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax));
        }

        public static void GetCopierFieldDescriptions(IEnumerable<IMemberDescription> members, LibraryTypes libraryTypes, List<GeneratedFieldDescription> fields)
        {
            var fieldIndex = 0;
            var uniqueTypes = new HashSet<IMemberDescription>(MemberDescriptionTypeComparer.Default);
            foreach (var member in members)
            {
                var t = member.Type;

                if (libraryTypes.IsShallowCopyable(t))
                    continue;

                foreach (var c in libraryTypes.StaticCopiers)
                    if (SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t))
                        goto skip;

                if (member.Symbol.HasAnyAttribute(libraryTypes.ImmutableAttributes))
                    continue;

                if (!uniqueTypes.Add(member))
                    continue;

                TypeSyntax copierType;
                if (t.HasAttribute(libraryTypes.GenerateSerializerAttribute)
                    && (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, libraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(libraryTypes.TypeManifestProviderAttribute))
                    && t is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 0 })
                {
                    // Use the concrete generated type and avoid expensive interface dispatch (except for complex nested cases that will fall back to IDeepCopier<T>)
                    SimpleNameSyntax name;
                    if (t is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
                    {
                        // Construct the full generic type name
                        name = GenericName(Identifier(GetSimpleClassName(t.Name)), TypeArgumentList(SeparatedList(namedTypeSymbol.TypeArguments.Select(arg => arg.ToTypeSyntax()))));
                    }
                    else
                    {
                        name = IdentifierName(GetSimpleClassName(t.Name));
                    }
                    copierType = QualifiedName(ParseName(GetGeneratedNamespaceName(t)), name);
                }
                else if (libraryTypes.WellKnownCopiers.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t)) is WellKnownCopierDescription copier)
                {
                    // The copier is not a static copier and is also not a generic copiers.
                    copierType = copier.CopierType.ToTypeSyntax();
                }
                else if (t is INamedTypeSymbol { ConstructedFrom: ISymbol unboundFieldType } named && libraryTypes.WellKnownCopiers.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, unboundFieldType)) is { } genericCopier)
                {
                    // Construct the generic copier type using the field's type arguments.
                    copierType = genericCopier.CopierType.Construct(named.TypeArguments.ToArray()).ToTypeSyntax();
                }
                else
                {
                    // Use the IDeepCopier<T> interface
                    copierType = libraryTypes.DeepCopier_1.ToTypeSyntax(member.TypeSyntax);
                }

                fields.Add(new CopierFieldDescription(copierType, $"_copier{fieldIndex++}", t));
skip:;
            }
        }

        private static MemberDeclarationSyntax GenerateMemberwiseDeepCopyMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes,
            bool onlyDeepFields)
        {
            var returnType = type.TypeSyntax;

            var originalParam = "original".ToIdentifierName();
            var contextParam = "context".ToIdentifierName();
            var resultVar = "result".ToIdentifierName();

            var body = new List<StatementSyntax>();

            var membersCopied = false;
            if (type.IsAbstractType)
            {
                // C#: return context.DeepCopy(original)
                body.Add(ReturnStatement(InvocationExpression(contextParam.Member("DeepCopy")).WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(originalParam))))));
                membersCopied = true;
            }
            else if (!type.IsValueType)
            {
                // C#: if (context.TryGetCopy(original, out T existing)) { return existing; }
                var tryGetCopy = InvocationExpression(
                    contextParam.Member("TryGetCopy"),
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(originalParam),
                        Argument(DeclarationExpression(
                            type.TypeSyntax,
                            SingleVariableDesignation(Identifier("existing"))))
                                    .WithRefKindKeyword(Token(SyntaxKind.OutKeyword))
                    })));
                body.Add(IfStatement(tryGetCopy, ReturnStatement("existing".ToIdentifierName())));

                if (!type.IsSealedType)
                {
                    // C#: if (original.GetType() != typeof(T)) { return context.DeepCopy(original); }
                    var exactTypeMatch = BinaryExpression(SyntaxKind.NotEqualsExpression, InvocationExpression(originalParam.Member("GetType")), TypeOfExpression(type.TypeSyntax));
                    var contextCopy = InvocationExpression(contextParam.Member("DeepCopy")).WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(originalParam))));
                    body.Add(IfStatement(exactTypeMatch, ReturnStatement(contextCopy)));
                }

                // C#: var result = _activator.Create();
                body.Add(LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"), SingletonSeparatedList(VariableDeclarator("result")
                    .WithInitializer(EqualsValueClause(GetCreateValueExpression(type, copierFields, libraryTypes)))))));

                // C#: context.RecordCopy(original, result);
                body.Add(ExpressionStatement(InvocationExpression(contextParam.Member("RecordCopy"), ArgumentList(SeparatedList(new[]
                {
                    Argument(originalParam),
                    Argument(resultVar)
                })))));

                if (!type.IsSealedType)
                {
                    // C#: DeepCopy(original, result, context);
                    body.Add(ExpressionStatement(InvocationExpression(IdentifierName("DeepCopy"),
                        ArgumentList(SeparatedList(new[] { Argument(originalParam), Argument(resultVar), Argument(contextParam) })))));
                    body.Add(ReturnStatement(resultVar));
                    membersCopied = true;
                }
                else if (type.HasComplexBaseType)
                {
                    // C#: _baseTypeCopier.DeepCopy(original, result, context);
                    body.Add(
                        ExpressionStatement(
                            InvocationExpression(
                                BaseTypeCopierFieldName.ToIdentifierName().Member(DeepCopyMethodName),
                                ArgumentList(SeparatedList(new[]
                                {
                                    Argument(originalParam),
                                    Argument(resultVar),
                                    Argument(contextParam)
                                })))));
                }
            }
            else if (!onlyDeepFields)
            {
                // C#: var result = _activator.Create();
                // or C#: var result = new TField();
                // or C#: var result = default(TField);
                body.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        IdentifierName("var"),
                        SingletonSeparatedList(VariableDeclarator(resultVar.Identifier)
                        .WithInitializer(EqualsValueClause(GetCreateValueExpression(type, copierFields, libraryTypes)))))));
            }
            else
            {
                originalParam = resultVar;
            }

            if (!membersCopied)
            {
                GenerateMemberwiseCopy(type, copierFields, members, libraryTypes, originalParam, contextParam, resultVar, body, onlyDeepFields);
                body.Add(ReturnStatement(resultVar));
            }

            var parameters = new[]
            {
                Parameter(originalParam.Identifier).WithType(type.TypeSyntax),
                Parameter(contextParam.Identifier).WithType(libraryTypes.CopyContext.ToTypeSyntax())
            };

            return MethodDeclaration(returnType, DeepCopyMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static ExpressionSyntax GetCreateValueExpression(ISerializableTypeDescription type, List<GeneratedFieldDescription> copierFields, LibraryTypes libraryTypes)
        {
            return type.UseActivator switch
            {
                true => InvocationExpression(copierFields.Find(f => f is ActivatorFieldDescription).FieldName.ToIdentifierName().Member("Create")),
                false => type.GetObjectCreationExpression(libraryTypes)
            };
        }

        private static MemberDeclarationSyntax GenerateBaseCopierDeepCopyMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes)
        {
            var inputParam = "input".ToIdentifierName();
            var resultParam = "output".ToIdentifierName();
            var contextParam = "context".ToIdentifierName();

            var body = new List<StatementSyntax>();

            if (type.HasComplexBaseType)
            {
                // C#: _baseTypeCopier.DeepCopy(original, result, context);
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            BaseTypeCopierFieldName.ToIdentifierName().Member(DeepCopyMethodName),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(inputParam),
                                Argument(resultParam),
                                Argument(contextParam)
                            })))));
            }

            GenerateMemberwiseCopy(type, copierFields, members, libraryTypes, inputParam, contextParam, resultParam, body);

            var parameters = new[]
            {
                Parameter(inputParam.Identifier).WithType(type.TypeSyntax),
                Parameter(resultParam.Identifier).WithType(type.TypeSyntax),
                Parameter(contextParam.Identifier).WithType(libraryTypes.CopyContext.ToTypeSyntax())
            };

            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), DeepCopyMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static void GenerateMemberwiseCopy(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes,
            IdentifierNameSyntax sourceVar,
            IdentifierNameSyntax contextVar,
            IdentifierNameSyntax destinationVar,
            List<StatementSyntax> body,
            bool onlyDeepFields = false)
        {
            AddSerializationCallbacks(type, sourceVar, destinationVar, "OnCopying", body);

            var copiers = copierFields.OfType<ICopierDescription>()
                    .Concat(libraryTypes.StaticCopiers)
                    .ToList();

            var orderedMembers = members.OrderBy(m => m.Member.FieldId);
            foreach (var member in orderedMembers)
            {
                if (onlyDeepFields && member.IsShallowCopyable) continue;

                var getValueExpression = GenerateMemberCopy(
                    copierFields,
                    libraryTypes,
                    inputValue: member.GetGetter(sourceVar),
                    contextVar,
                    copiers,
                    member);
                var memberAssignment = ExpressionStatement(member.GetSetter(destinationVar, getValueExpression));
                body.Add(memberAssignment);
            }

            AddSerializationCallbacks(type, sourceVar, destinationVar, "OnCopied", body);
        }

        public static ExpressionSyntax GenerateMemberCopy(
            List<GeneratedFieldDescription> copierFields,
            LibraryTypes libraryTypes,
            ExpressionSyntax inputValue,
            ExpressionSyntax copyContextVar,
            List<ICopierDescription> copiers,
            ISerializableMember member)
        {
            if (member.IsShallowCopyable)
                return inputValue;

            var description = member.Member;

            // Copiers can either be static classes or injected into the constructor.
            // Either way, the member signatures are the same.
            var memberType = description.Type;
            var copier = copiers.Find(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
            ExpressionSyntax getValueExpression;

            if (copier is null)
            {
                getValueExpression = InvocationExpression(
                    copyContextVar.Member(DeepCopyMethodName),
                    ArgumentList(SeparatedList(new[] { Argument(inputValue) })));
            }
            else
            {
                ExpressionSyntax copierExpression;
                var staticCopier = libraryTypes.StaticCopiers.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, memberType));
                if (staticCopier != null)
                {
                    copierExpression = staticCopier.CopierType.ToNameSyntax();
                }
                else
                {
                    var instanceCopier = copierFields.First(f => f is CopierFieldDescription cf && SymbolEqualityComparer.Default.Equals(cf.UnderlyingType, memberType));
                    copierExpression = IdentifierName(instanceCopier.FieldName);
                }

                getValueExpression = InvocationExpression(
                    copierExpression.Member(DeepCopyMethodName),
                    ArgumentList(SeparatedList(new[] { Argument(inputValue), Argument(copyContextVar) })));
                if (!SymbolEqualityComparer.Default.Equals(copier.UnderlyingType, member.Member.Type))
                {
                    // If the member type type differs from the copier type (eg because the member is an array), cast the result.
                    getValueExpression = CastExpression(description.TypeSyntax, getValueExpression);
                }
            }

            return getValueExpression;
        }

        private static MemberDeclarationSyntax GenerateImmutableTypeCopyMethod(
            ISerializableTypeDescription type,
            LibraryTypes libraryTypes)
        {
            var returnType = type.TypeSyntax;

            var inputParam = "input".ToIdentifierName();

            var body = new StatementSyntax[]
            {
                ReturnStatement(inputParam)
            };

            var parameters = new[]
            {
                Parameter("input".ToIdentifier()).WithType(returnType),
                Parameter("_".ToIdentifier()).WithType(libraryTypes.CopyContext.ToTypeSyntax()),
            };

            return MethodDeclaration(returnType, DeepCopyMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static void AddSerializationCallbacks(ISerializableTypeDescription type, IdentifierNameSyntax originalInstance, IdentifierNameSyntax resultInstance, string callbackMethodName, List<StatementSyntax> body)
        {
            var serializationHooks = type.SerializationHooks;
            for (var hookIndex = 0; hookIndex < serializationHooks.Count; ++hookIndex)
            {
                var hookType = serializationHooks[hookIndex];
                var member = hookType.GetAllMembers<IMethodSymbol>(callbackMethodName, Accessibility.Public).FirstOrDefault();
                if (member is null || member.Parameters.Length != 2)
                {
                    continue;
                }

                var originalArgument = Argument(originalInstance);
                if (member.Parameters[0].RefKind == RefKind.Ref)
                {
                    originalArgument = originalArgument.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                }

                var resultArgument = Argument(resultInstance);
                if (member.Parameters[1].RefKind == RefKind.Ref)
                {
                    resultArgument = resultArgument.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                }

                body.Add(ExpressionStatement(InvocationExpression(
                    IdentifierName($"_hook{hookIndex}").Member(callbackMethodName),
                    ArgumentList(SeparatedList(new[] { originalArgument, resultArgument })))));
            }
        }

        internal sealed class BaseCopierFieldDescription : GeneratedFieldDescription
        {
            public BaseCopierFieldDescription(TypeSyntax fieldType, bool concreteType = false) : base(fieldType, BaseTypeCopierFieldName)
                => IsInjected = !concreteType;

            public override bool IsInjected { get; }
        }

        internal sealed class CopierFieldDescription : GeneratedFieldDescription, ICopierDescription
        {
            public CopierFieldDescription(TypeSyntax fieldType, string fieldName, ITypeSymbol underlyingType) : base(fieldType, fieldName)
            {
                UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => false;
        }

    }
}