using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System;

namespace Orleans.CodeGenerator
{
    internal static class CopierGenerator
    {
        private const string BaseTypeCopierFieldName = "_baseTypeCopier";
        private const string ActivatorFieldName = "_activator";
        private const string DeepCopyMethodName = "DeepCopy";

        public static ClassDeclarationSyntax GenerateCopier(
            LibraryTypes libraryTypes,
            ISerializableTypeDescription type)
        {
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

            if (type.IsImmutable)
            {
                var copyMethod = GenerateImmutableTypeCopyMethod(type, libraryTypes);
                classDeclaration = classDeclaration.AddMembers(copyMethod);
            }
            else
            {
                var fieldDescriptions = GetFieldDescriptions(type, members, libraryTypes);
                var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
                var ctor = GenerateConstructor(libraryTypes, simpleClassName, fieldDescriptions);

                var copyMethod = GenerateMemberwiseDeepCopyMethod(type, fieldDescriptions, members, libraryTypes);
                classDeclaration = classDeclaration
                    .AddMembers(copyMethod)
                    .AddMembers(fieldDeclarations)
                    .AddMembers(ctor);

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
                    case SetterFieldDescription setter:
                        {
                            var fieldSetterVariable = VariableDeclarator(setter.FieldName);

                            return
                                FieldDeclaration(VariableDeclaration(setter.FieldType).AddVariables(fieldSetterVariable))
                                    .AddModifiers(
                                        Token(SyntaxKind.PrivateKeyword),
                                        Token(SyntaxKind.ReadOnlyKeyword));
                        }
                    case GetterFieldDescription getter:
                        {
                            var fieldGetterVariable = VariableDeclarator(getter.FieldName);

                            return
                                FieldDeclaration(VariableDeclaration(getter.FieldType).AddVariables(fieldGetterVariable))
                                    .AddModifiers(
                                        Token(SyntaxKind.PrivateKeyword),
                                        Token(SyntaxKind.ReadOnlyKeyword));
                        }
                    default:
                        return FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                }
            }
        }

        private static ConstructorDeclarationSyntax GenerateConstructor(LibraryTypes libraryTypes, string simpleClassName, List<GeneratedFieldDescription> fieldDescriptions)
        {
            var injected = fieldDescriptions.Where(f => f.IsInjected).ToList();
            var parameters = new List<ParameterSyntax>(injected.Select(f => Parameter(f.FieldName.ToIdentifier()).WithType(f.FieldType)));
            const string CodecProviderParameterName = "codecProvider";
            parameters.Add(Parameter(Identifier(CodecProviderParameterName)).WithType(libraryTypes.ICodecProvider.ToTypeSyntax()));

            IEnumerable<StatementSyntax> GetStatements()
            {
                foreach (var field in fieldDescriptions)
                {
                    switch (field)
                    {
                        case GetterFieldDescription getter:
                            yield return getter.InitializationSyntax;
                            break;

                        case SetterFieldDescription setter:
                            yield return setter.InitializationSyntax;
                            break;

                        case GeneratedFieldDescription _ when field.IsInjected:
                            yield return ExpressionStatement(
                                AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    ThisExpression().Member(field.FieldName.ToIdentifierName()),
                                    Unwrapped(field.FieldName.ToIdentifierName())));
                            break;
                        case CopierFieldDescription codec:
                            {
                                yield return ExpressionStatement(
                                    AssignmentExpression(
                                        SyntaxKind.SimpleAssignmentExpression,
                                        ThisExpression().Member(field.FieldName.ToIdentifierName()),
                                        GetService(field.FieldType)));
                            }
                            break;
                    }
                }
            }

            return ConstructorDeclaration(simpleClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .AddBodyStatements(GetStatements().ToArray());

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
                    ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(IdentifierName(CodecProviderParameterName)) })));
            }
        }

        private static List<GeneratedFieldDescription> GetFieldDescriptions(
            ISerializableTypeDescription serializableTypeDescription,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes)
        {
            var fields = new List<GeneratedFieldDescription>();

            if (serializableTypeDescription.HasComplexBaseType)
            {
                fields.Add(new BaseCopierFieldDescription(libraryTypes.BaseCopier_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax), BaseTypeCopierFieldName));
            }

            if (serializableTypeDescription.UseActivator)
            {
                fields.Add(new ActivatorFieldDescription(libraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
            }

            // Add a codec field for any field in the target which does not have a static codec.
            fields.AddRange(GetCopierFieldDescriptions(serializableTypeDescription.Members, libraryTypes));

            foreach (var member in members)
            {
                if (member.GetGetterFieldDescription() is { } getterFieldDescription)
                {
                    fields.Add(getterFieldDescription);
                }

                if (member.GetSetterFieldDescription() is { } setterFieldDescription)
                {
                    fields.Add(setterFieldDescription);
                }
            }

            for (var hookIndex = 0; hookIndex < serializableTypeDescription.SerializationHooks.Count; ++hookIndex)
            {
                var hookType = serializableTypeDescription.SerializationHooks[hookIndex];
                fields.Add(new SerializationHookFieldDescription(hookType.ToTypeSyntax(), $"_hook{hookIndex}"));
            }

            return fields;
        }

        public static IEnumerable<CopierFieldDescription> GetCopierFieldDescriptions(IEnumerable<IMemberDescription> members, LibraryTypes libraryTypes)
        {
            var filteredMembers = members
                .Distinct(MemberDescriptionTypeComparer.Default)
                .Where(t => !libraryTypes.StaticCopiers.Any(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t.Type)));

            var fieldIndex = 0;
            foreach (var member in filteredMembers)
            {
                yield return GetCopierDescription(member, fieldIndex++, libraryTypes);
            }

            static CopierFieldDescription GetCopierDescription(IMemberDescription member, int fieldIndex, LibraryTypes libraryTypes)
            {
                TypeSyntax copierType = null;
                var t = member.Type;
                if (t.HasAttribute(libraryTypes.GenerateSerializerAttribute)
                    && (!SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, libraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(libraryTypes.TypeManifestProviderAttribute)))
                {
                    // Use the concrete generated type and avoid expensive interface dispatch
                    if (t is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
                    {
                        // Construct the full generic type name
                        var ns = GetGeneratedNamespaceName(t);
                        var name = GenericName(Identifier(GetSimpleClassName(t.Name)), TypeArgumentList(SeparatedList(namedTypeSymbol.TypeArguments.Select(arg => arg.ToTypeSyntax()))));
                        copierType = QualifiedName(ParseName(ns), name);
                    }
                    else
                    {
                        var simpleName = $"{GetGeneratedNamespaceName(t)}.{GetSimpleClassName(t.Name)}";
                        copierType = ParseTypeName(simpleName);
                    }
                }
                else if (libraryTypes.WellKnownCopiers.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t)) is WellKnownCopierDescription codec)
                {
                    // The codec is not a static copier and is also not a generic copiers.
                    copierType = codec.CopierType.ToTypeSyntax();
                }
                else if (t is INamedTypeSymbol named && libraryTypes.WellKnownCopiers.Find(c => t is INamedTypeSymbol named && named.ConstructedFrom is ISymbol unboundFieldType && SymbolEqualityComparer.Default.Equals(c.UnderlyingType, unboundFieldType)) is WellKnownCopierDescription genericCopier)
                {
                    // Construct the generic copier type using the field's type arguments.
                    copierType = genericCopier.CopierType.Construct(named.TypeArguments.ToArray()).ToTypeSyntax();
                }
                else
                {
                    // Use the IDeepCopier<T> interface
                    copierType = libraryTypes.DeepCopier_1.ToTypeSyntax(member.TypeSyntax);
                }

                var fieldName = '_' + ToLowerCamelCase(member.TypeNameIdentifier) + "Copier" + fieldIndex;
                return new CopierFieldDescription(copierType, fieldName, t);
            }

            static string ToLowerCamelCase(string input) => char.IsLower(input, 0) ? input : char.ToLowerInvariant(input[0]) + input.Substring(1);
        }

        private static MemberDeclarationSyntax GenerateMemberwiseDeepCopyMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes)
        {
            var returnType = type.TypeSyntax;

            var originalParam = "original".ToIdentifierName();
            var contextParam = "context".ToIdentifierName();
            var resultVar = "result".ToIdentifierName();

            var body = new List<StatementSyntax>();

            ExpressionSyntax createValueExpression = type.UseActivator switch
            {
                true => InvocationExpression(copierFields.OfType<ActivatorFieldDescription>().Single().FieldName.ToIdentifierName().Member("Create")),
                false => type.GetObjectCreationExpression(libraryTypes)
            };

            if (!type.IsValueType)
            {
                // C#: if (context.TryGetCopy(original, out T result)) { return result; }
                var tryGetCopy = InvocationExpression(
                    contextParam.Member("TryGetCopy"),
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(originalParam),
                        Argument(DeclarationExpression(
                            type.TypeSyntax,
                            SingleVariableDesignation(Identifier("result"))))
                                    .WithRefKindKeyword(Token(SyntaxKind.OutKeyword))
                    })));
                body.Add(IfStatement(tryGetCopy, ReturnStatement(resultVar)));

                if (!type.IsSealedType)
                {
                    // C#: if (original.GetType() != typeof(<codec>)) { return context.DeepCopy(original); }
                    var exactTypeMatch = BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, originalParam, IdentifierName("GetType"))),
                            TypeOfExpression(type.TypeSyntax));
                    var contextCopy = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, contextParam, IdentifierName("DeepCopy")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(originalParam))));
                    body.Add(IfStatement(exactTypeMatch, ReturnStatement(contextCopy)));
                }

                // C#: result = _activator.Create();
                body.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, resultVar, createValueExpression)));

                // C#: context.RecordCopy(original, result);
                body.Add(ExpressionStatement(InvocationExpression(contextParam.Member("RecordCopy"), ArgumentList(SeparatedList(new[]
                {
                    Argument(originalParam),
                    Argument(resultVar)
                })))));

                if (type.HasComplexBaseType)
                {
                    // C#: _baseTypeCopier.DeepCopy(original, result, context);
                    body.Add(
                        ExpressionStatement(
                            InvocationExpression(
                                ThisExpression().Member(BaseTypeCopierFieldName.ToIdentifierName()).Member(DeepCopyMethodName),
                                ArgumentList(SeparatedList(new[]
                                {
                                    Argument(originalParam),
                                    Argument(resultVar),
                                    Argument(contextParam)
                                })))));
                }
            }
            else
            {
                // C#: TField result = _activator.Create();
                // or C#: TField result = new TField();
                body.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        type.TypeSyntax,
                        SingletonSeparatedList(VariableDeclarator(resultVar.Identifier)
                        .WithInitializer(EqualsValueClause(createValueExpression))))));

            }

            body.AddRange(AddSerializationCallbacks(type, originalParam, resultVar, "OnCopying"));
            body.AddRange(GenerateMemberwiseCopy(copierFields, members, libraryTypes, originalParam, contextParam, resultVar));
            body.AddRange(AddSerializationCallbacks(type, originalParam, resultVar, "OnCopied"));

            body.Add(ReturnStatement(resultVar));

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
                            ThisExpression().Member(BaseTypeCopierFieldName.ToIdentifierName()).Member(DeepCopyMethodName),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(inputParam),
                                Argument(resultParam),
                                Argument(contextParam)
                            })))));
            }

            body.AddRange(AddSerializationCallbacks(type, inputParam, resultParam, "OnCopying"));
            body.AddRange(GenerateMemberwiseCopy(copierFields, members, libraryTypes, inputParam, contextParam, resultParam));
            body.AddRange(AddSerializationCallbacks(type, inputParam, resultParam, "OnCopied"));

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

        public static IEnumerable<StatementSyntax> GenerateMemberwiseCopy(
            List<GeneratedFieldDescription> copierFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes,
            IdentifierNameSyntax sourceVar,
            IdentifierNameSyntax contextVar,
            IdentifierNameSyntax destinationVar)
        {
            var codecs = copierFields.OfType<ICopierDescription>()
                    .Concat(libraryTypes.StaticCopiers)
                    .ToList();

            var orderedMembers = members.OrderBy(m => m.Member.FieldId);
            foreach (var member in orderedMembers)
            {
                var getValueExpression = GenerateMemberCopy(
                    copierFields,
                    libraryTypes,
                    inputValue: member.GetGetter(sourceVar),
                    contextVar,
                    codecs,
                    member);
                var memberAssignment = ExpressionStatement(member.GetSetter(destinationVar, getValueExpression));
                yield return memberAssignment;
            }
        }

        public static ExpressionSyntax GenerateMemberCopy(
            List<GeneratedFieldDescription> copierFields,
            LibraryTypes libraryTypes,
            ExpressionSyntax inputValue,
            ExpressionSyntax copyContextVar,
            List<ICopierDescription> codecs,
            ISerializableMember member)
        {
            var description = member.Member;

            // Copiers can either be static classes or injected into the constructor.
            // Either way, the member signatures are the same.
            var memberType = description.Type;
            var codec = codecs.FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
            var staticCopier = libraryTypes.StaticCopiers.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, memberType));
            ExpressionSyntax getValueExpression;

            if (member.IsShallowCopyable)
            {
                getValueExpression = inputValue;
            }
            else if (codec is null)
            {
                getValueExpression = InvocationExpression(
                    copyContextVar.Member(DeepCopyMethodName),
                    ArgumentList(SeparatedList(new[] { Argument(inputValue) })));
            }
            else
            {
                ExpressionSyntax codecExpression;
                if (staticCopier != null)
                {
                    codecExpression = staticCopier.CopierType.ToNameSyntax();
                }
                else
                {
                    var instanceCopier = copierFields.OfType<CopierFieldDescription>().First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
                    codecExpression = ThisExpression().Member(instanceCopier.FieldName);
                }

                getValueExpression = InvocationExpression(
                    codecExpression.Member(DeepCopyMethodName),
                    ArgumentList(SeparatedList(new[] { Argument(inputValue), Argument(copyContextVar) })));
                if (!SymbolEqualityComparer.Default.Equals(codec.UnderlyingType, member.Member.Type))
                {
                    // If the member type type differs from the codec type (eg because the member is an array), cast the result.
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

        private static IEnumerable<StatementSyntax> AddSerializationCallbacks(ISerializableTypeDescription type, IdentifierNameSyntax originalInstance, IdentifierNameSyntax resultInstance, string callbackMethodName)
        {
            for (var hookIndex = 0; hookIndex < type.SerializationHooks.Count; ++hookIndex)
            {
                var hookType = type.SerializationHooks[hookIndex];
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

                yield return ExpressionStatement(InvocationExpression(
                    ThisExpression().Member($"_hook{hookIndex}").Member(callbackMethodName),
                    ArgumentList(SeparatedList(new[] { originalArgument, resultArgument }))));
            }
        }

        internal class BaseCopierFieldDescription : GeneratedFieldDescription
        {
            public BaseCopierFieldDescription(TypeSyntax fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
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