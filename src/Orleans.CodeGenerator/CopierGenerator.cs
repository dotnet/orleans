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
    internal class CopierGenerator
    {
        private const string BaseTypeCopierFieldName = "_baseTypeCopier";
        private const string ActivatorFieldName = "_activator";
        private const string DeepCopyMethodName = "DeepCopy";
        private readonly CodeGenerator _codeGenerator;

        public CopierGenerator(CodeGenerator codeGenerator)
        {
            _codeGenerator = codeGenerator;
        }

        private LibraryTypes LibraryTypes => _codeGenerator.LibraryTypes;

        public ClassDeclarationSyntax GenerateCopier(
            ISerializableTypeDescription type,
            Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers)
        {
            var isShallowCopyable = type.IsShallowCopyable;
            if (isShallowCopyable && !type.IsGenericType)
            {
                defaultCopiers.Add(type, LibraryTypes.ShallowCopier.ToTypeSyntax(type.TypeSyntax));
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
                    members.Add(new SerializableMember(_codeGenerator, member, members.Count));
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

            var isExceptionType = type.IsExceptionType && type.SerializationHooks.Count == 0;

            var baseType = isExceptionType ? QualifiedName(AliasQualifiedName("global", IdentifierName("Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper")), GenericName(Identifier("ExceptionCopier"), TypeArgumentList(SeparatedList(new[] { type.TypeSyntax, type.BaseType.ToTypeSyntax() }))))
                : (isShallowCopyable ? LibraryTypes.ShallowCopier : LibraryTypes.DeepCopier_1).ToTypeSyntax(type.TypeSyntax);

            var classDeclaration = ClassDeclaration(simpleClassName)
                .AddBaseListTypes(SimpleBaseType(baseType))
                .AddModifiers(Token(accessibility), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(CodeGenerator.GetGeneratedCodeAttributes());

            if (!isShallowCopyable)
            {
                var fieldDescriptions = GetFieldDescriptions(type, members, isExceptionType, out var onlyDeepFields);
                var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
                var ctor = GenerateConstructor(simpleClassName, fieldDescriptions, isExceptionType);

                classDeclaration = classDeclaration.AddMembers(fieldDeclarations);

                if (!isExceptionType)
                {
                    var copyMethod = GenerateMemberwiseDeepCopyMethod(type, fieldDescriptions, members, onlyDeepFields);
                    classDeclaration = classDeclaration.AddMembers(copyMethod);
                }

                if (ctor != null)
                    classDeclaration = classDeclaration.AddMembers(ctor);

                if (isExceptionType || !type.IsSealedType)
                {
                    if (GenerateBaseCopierDeepCopyMethod(type, fieldDescriptions, members, isExceptionType) is { } baseCopier)
                        classDeclaration = classDeclaration.AddMembers(baseCopier);

                    if (!isExceptionType)
                        classDeclaration = classDeclaration.AddBaseListTypes(SimpleBaseType(LibraryTypes.BaseCopier_1.ToTypeSyntax(type.TypeSyntax)));
                }
            }

            if (type.IsGenericType)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, type.TypeParameters);
            }

            return classDeclaration;
        }

        public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

        public static string GetSimpleClassName(string name) => $"Copier_{name}";

        private MemberDeclarationSyntax[] GetFieldDeclarations(List<GeneratedFieldDescription> fieldDescriptions)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            static MemberDeclarationSyntax GetFieldDeclaration(GeneratedFieldDescription description)
            {
                switch (description)
                {
                    case FieldAccessorDescription accessor when accessor.InitializationSyntax != null:
                        return
                            FieldDeclaration(VariableDeclaration(accessor.FieldType,
                                SingletonSeparatedList(VariableDeclarator(accessor.FieldName).WithInitializer(EqualsValueClause(accessor.InitializationSyntax)))))
                                .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                    case FieldAccessorDescription accessor when accessor.InitializationSyntax == null:
                        //[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Amount")]
                        //extern static void SetAmount(External instance, int value);
                        return
                            MethodDeclaration(
                                PredefinedType(Token(SyntaxKind.VoidKeyword)),
                                accessor.AccessorName)
                                .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ExternKeyword), Token(SyntaxKind.StaticKeyword))
                                .AddAttributeLists(AttributeList(SingletonSeparatedList(
                                    Attribute(IdentifierName("System.Runtime.CompilerServices.UnsafeAccessor"))
                                        .AddArgumentListArguments(
                                            AttributeArgument(
                                                MemberAccessExpression(
                                                        SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName("System.Runtime.CompilerServices.UnsafeAccessorKind"),
                                                        IdentifierName("Method"))),
                                            AttributeArgument(
                                                    LiteralExpression(
                                                        SyntaxKind.StringLiteralExpression,
                                                        Literal($"set_{accessor.FieldName}")))
                                            .WithNameEquals(NameEquals("Name"))))))
                                .WithParameterList(
                                    ParameterList(SeparatedList(new[]
                                        {
                                            Parameter(Identifier("instance")).WithType(accessor.ContainingType),
                                            Parameter(Identifier("value")).WithType(description.FieldType)
                                        })))
                                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
                    default:
                        return FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                }
            }
        }

        private ConstructorDeclarationSyntax GenerateConstructor(string simpleClassName, List<GeneratedFieldDescription> fieldDescriptions, bool isExceptionType)
        {
            var codecProviderAdded = false;
            var parameters = new List<ParameterSyntax>();
            var statements = new List<StatementSyntax>();

            if (isExceptionType)
            {
                parameters.Add(Parameter(Identifier("codecProvider")).WithType(LibraryTypes.ICodecProvider.ToTypeSyntax()));
                codecProviderAdded = true;
            }

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
                            parameters.Add(Parameter(Identifier("codecProvider")).WithType(LibraryTypes.ICodecProvider.ToTypeSyntax()));
                            codecProviderAdded = true;
                        }

                        var copier = InvocationExpression(
                            IdentifierName("OrleansGeneratedCodeHelper").Member(GenericName(Identifier("GetService"), TypeArgumentList(SingletonSeparatedList(field.FieldType)))),
                            ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(IdentifierName("codecProvider")) })));

                        statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, field.FieldName.ToIdentifierName(), copier)));
                        break;
                }
            }

            return statements.Count == 0 && !isExceptionType ? null : ConstructorDeclaration(simpleClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .AddBodyStatements(statements.ToArray())
                .WithInitializer(isExceptionType ? ConstructorInitializer(SyntaxKind.BaseConstructorInitializer, ArgumentList(SingletonSeparatedList(Argument(IdentifierName("codecProvider"))))) : null);

            static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
            {
                return InvocationExpression(
                    IdentifierName("OrleansGeneratedCodeHelper").Member("UnwrapService"),
                    ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(expr) })));
            }
        }

        private List<GeneratedFieldDescription> GetFieldDescriptions(
            ISerializableTypeDescription serializableTypeDescription,
            List<ISerializableMember> members,
            bool isExceptionType,
            out bool onlyDeepFields)
        {
            var serializationHooks = serializableTypeDescription.SerializationHooks;
            onlyDeepFields = serializableTypeDescription.IsValueType && serializationHooks.Count == 0;

            var fields = new List<GeneratedFieldDescription>();

            if (!isExceptionType && serializableTypeDescription.HasComplexBaseType)
            {
                fields.Add(GetBaseTypeField(serializableTypeDescription));
            }

            if (!serializableTypeDescription.IsImmutable)
            {
                if (!isExceptionType && serializableTypeDescription.UseActivator && !serializableTypeDescription.IsAbstractType)
                {
                    onlyDeepFields = false;
                    fields.Add(new ActivatorFieldDescription(LibraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
                }

                // Add a copier field for any field in the target which does not have a static copier.
                GetCopierFieldDescriptions(serializableTypeDescription.Members, fields);
            }

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

        private BaseCopierFieldDescription GetBaseTypeField(ISerializableTypeDescription serializableTypeDescription)
        {
            var baseType = serializableTypeDescription.BaseType;
            if (baseType.HasAnyAttribute(LibraryTypes.GenerateSerializerAttributes)
                && (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, LibraryTypes.Compilation.Assembly) || baseType.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
                && baseType is not INamedTypeSymbol { IsGenericType: true })
            {
                // Use the concrete generated type and avoid expensive interface dispatch (except for generic types that will fall back to IBaseCopier<T>)
                return new(QualifiedName(ParseName(GetGeneratedNamespaceName(baseType)), IdentifierName(GetSimpleClassName(baseType.Name))), true);
            }

            return new(LibraryTypes.BaseCopier_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax));
        }

        public void GetCopierFieldDescriptions(IEnumerable<IMemberDescription> members, List<GeneratedFieldDescription> fields)
        {
            var fieldIndex = 0;
            var uniqueTypes = new HashSet<IMemberDescription>(MemberDescriptionTypeComparer.Default);
            foreach (var member in members)
            {
                var t = member.Type;

                if (LibraryTypes.IsShallowCopyable(t))
                    continue;

                foreach (var c in LibraryTypes.StaticCopiers)
                    if (SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t))
                        goto skip;

                if (member.Symbol.HasAnyAttribute(LibraryTypes.ImmutableAttributes))
                    continue;

                if (!uniqueTypes.Add(member))
                    continue;

                TypeSyntax copierType;
                if (t.HasAnyAttribute(LibraryTypes.GenerateSerializerAttributes)
                    && (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, LibraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
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
                else if (t is IArrayTypeSymbol { IsSZArray: true } array)
                {
                    copierType = LibraryTypes.ArrayCopier.Construct(array.ElementType).ToTypeSyntax();
                }
                else if (LibraryTypes.WellKnownCopiers.FindByUnderlyingType(t) is { } copier)
                {
                    // The copier is not a static copier and is also not a generic copiers.
                    copierType = copier.CopierType.ToTypeSyntax();
                }
                else if (t is INamedTypeSymbol { ConstructedFrom: ISymbol unboundFieldType } named && LibraryTypes.WellKnownCopiers.FindByUnderlyingType(unboundFieldType) is { } genericCopier)
                {
                    // Construct the generic copier type using the field's type arguments.
                    copierType = genericCopier.CopierType.Construct(named.TypeArguments.ToArray()).ToTypeSyntax();
                }
                else
                {
                    // Use the IDeepCopier<T> interface
                    copierType = LibraryTypes.DeepCopier_1.ToTypeSyntax(member.TypeSyntax);
                }

                fields.Add(new CopierFieldDescription(copierType, $"_copier{fieldIndex++}", t));
skip:;
            }
        }

        private MemberDeclarationSyntax GenerateMemberwiseDeepCopyMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
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
                body.Add(ReturnStatement(InvocationExpression(contextParam.Member("DeepCopy"), ArgumentList(SingletonSeparatedList(Argument(originalParam))))));
                membersCopied = true;
            }
            else if (type.IsUnsealedImmutable)
            {
                // C#: return original is null || original.GetType() == typeof(T) ? original : context.DeepCopy(original);
                var exactTypeMatch = BinaryExpression(SyntaxKind.EqualsExpression, InvocationExpression(originalParam.Member("GetType")), TypeOfExpression(type.TypeSyntax));
                var nullOrTypeMatch = BinaryExpression(SyntaxKind.LogicalOrExpression, BinaryExpression(SyntaxKind.IsExpression, originalParam, LiteralExpression(SyntaxKind.NullLiteralExpression)), exactTypeMatch);
                var contextCopy = InvocationExpression(contextParam.Member("DeepCopy"), ArgumentList(SingletonSeparatedList(Argument(originalParam))));
                body.Add(ReturnStatement(ConditionalExpression(nullOrTypeMatch, originalParam, contextCopy)));
                membersCopied = true;
            }
            else if (!type.IsValueType)
            {
                if (type.TrackReferences)
                {
                    // C#: if (context.TryGetCopy(original, out T existing)) return existing;
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
                }
                else
                {
                    // C#: if (original is null) return null;
                    body.Add(IfStatement(BinaryExpression(SyntaxKind.IsExpression, originalParam, LiteralExpression(SyntaxKind.NullLiteralExpression)), ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression))));
                }

                if (!type.IsSealedType)
                {
                    // C#: if (original.GetType() != typeof(T)) { return context.DeepCopy(original); }
                    var exactTypeMatch = BinaryExpression(SyntaxKind.NotEqualsExpression, InvocationExpression(originalParam.Member("GetType")), TypeOfExpression(type.TypeSyntax));
                    var contextCopy = InvocationExpression(contextParam.Member("DeepCopy"), ArgumentList(SingletonSeparatedList(Argument(originalParam))));
                    body.Add(IfStatement(exactTypeMatch, ReturnStatement(contextCopy)));
                }

                // C#: var result = _activator.Create();
                body.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        IdentifierName("var"),
                        SingletonSeparatedList(VariableDeclarator(
                            resultVar.Identifier,
                            argumentList: null,
                            initializer: EqualsValueClause(GetCreateValueExpression(type, copierFields)))))));

                if (type.TrackReferences)
                {
                    // C#: context.RecordCopy(original, result);
                    body.Add(ExpressionStatement(InvocationExpression(contextParam.Member("RecordCopy"), ArgumentList(SeparatedList(new[]
                    {
                        Argument(originalParam),
                        Argument(resultVar)
                    })))));
                }

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
                        .WithInitializer(EqualsValueClause(GetCreateValueExpression(type, copierFields)))))));
            }
            else
            {
                originalParam = resultVar;
            }

            if (!membersCopied)
            {
                GenerateMemberwiseCopy(type, copierFields, members, originalParam, contextParam, resultVar, body, onlyDeepFields);
                body.Add(ReturnStatement(resultVar));
            }

            var parameters = new[]
            {
                Parameter(originalParam.Identifier).WithType(type.TypeSyntax),
                Parameter(contextParam.Identifier).WithType(LibraryTypes.CopyContext.ToTypeSyntax())
            };

            return MethodDeclaration(returnType, DeepCopyMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private ExpressionSyntax GetCreateValueExpression(ISerializableTypeDescription type, List<GeneratedFieldDescription> copierFields)
        {
            return type.UseActivator switch
            {
                true => InvocationExpression(copierFields.Find(f => f is ActivatorFieldDescription).FieldName.ToIdentifierName().Member("Create")),
                false => type.GetObjectCreationExpression()
            };
        }

        private MemberDeclarationSyntax GenerateBaseCopierDeepCopyMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            bool isExceptionType)
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
                            (isExceptionType ? (ExpressionSyntax)BaseExpression() : IdentifierName(BaseTypeCopierFieldName)).Member(DeepCopyMethodName),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(inputParam),
                                Argument(resultParam),
                                Argument(contextParam)
                            })))));
            }

            var emptyBodyCount = body.Count;

            GenerateMemberwiseCopy(
                type,
                copierFields,
                members,
                inputParam,
                contextParam,
                resultParam,
                body);

            if (isExceptionType && body.Count == emptyBodyCount)
                return null;

            var parameters = new[]
            {
                Parameter(inputParam.Identifier).WithType(type.TypeSyntax),
                Parameter(resultParam.Identifier).WithType(type.TypeSyntax),
                Parameter(contextParam.Identifier).WithType(LibraryTypes.CopyContext.ToTypeSyntax())
            };

            var method = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), DeepCopyMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());

            if (isExceptionType)
                method = method.AddModifiers(Token(SyntaxKind.OverrideKeyword));

            return method;
        }

        private void GenerateMemberwiseCopy(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            IdentifierNameSyntax sourceVar,
            IdentifierNameSyntax contextVar,
            IdentifierNameSyntax destinationVar,
            List<StatementSyntax> body,
            bool onlyDeepFields = false)
        {
            AddSerializationCallbacks(type, sourceVar, destinationVar, "OnCopying", body);

            var copiers = type.IsUnsealedImmutable ? null : copierFields.OfType<ICopierDescription>()
                    .Concat(LibraryTypes.StaticCopiers)
                    .ToList();

            var orderedMembers = members.OrderBy(m => m.Member.FieldId);
            foreach (var member in orderedMembers)
            {
                if (onlyDeepFields && member.IsShallowCopyable) continue;

                var getValueExpression = GenerateMemberCopy(
                    copierFields,
                    inputValue: member.GetGetter(sourceVar),
                    contextVar,
                    copiers,
                    member);
                var memberAssignment = ExpressionStatement(member.GetSetter(destinationVar, getValueExpression));
                body.Add(memberAssignment);
            }

            AddSerializationCallbacks(type, sourceVar, destinationVar, "OnCopied", body);
        }

        public ExpressionSyntax GenerateMemberCopy(
            List<GeneratedFieldDescription> copierFields,
            ExpressionSyntax inputValue,
            ExpressionSyntax copyContextVar,
            List<ICopierDescription> copiers,
            ISerializableMember member)
        {
            if (copiers is null || member.IsShallowCopyable)
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
                var staticCopier = LibraryTypes.StaticCopiers.FindByUnderlyingType(memberType);
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

        private void AddSerializationCallbacks(ISerializableTypeDescription type, IdentifierNameSyntax originalInstance, IdentifierNameSyntax resultInstance, string callbackMethodName, List<StatementSyntax> body)
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