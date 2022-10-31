using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.InvokableGenerator;

namespace Orleans.CodeGenerator
{
    internal static class SerializerGenerator
    {
        private const string BaseTypeSerializerFieldName = "_baseTypeSerializer";
        private const string ActivatorFieldName = "_activator";
        private const string SerializeMethodName = "Serialize";
        private const string DeserializeMethodName = "Deserialize";
        private const string WriteFieldMethodName = "WriteField";
        private const string ReadValueMethodName = "ReadValue";
        private const string CodecFieldTypeFieldName = "_codecFieldType";

        public static ClassDeclarationSyntax GenerateSerializer(LibraryTypes libraryTypes, ISerializableTypeDescription type)
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

            var fieldDescriptions = GetFieldDescriptions(type, members, libraryTypes);
            var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
            var ctor = GenerateConstructor(libraryTypes, simpleClassName, fieldDescriptions);

            var accessibility = type.Accessibility switch
            {
                Accessibility.Public => SyntaxKind.PublicKeyword,
                _ => SyntaxKind.InternalKeyword,
            };

            var baseType = (type.IsAbstractType ? libraryTypes.AbstractTypeSerializer : libraryTypes.FieldCodec_1).ToTypeSyntax(type.TypeSyntax);

            var classDeclaration = ClassDeclaration(simpleClassName)
                .AddBaseListTypes(SimpleBaseType(baseType))
                .AddModifiers(Token(accessibility), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(fieldDeclarations);

            if (ctor != null)
                classDeclaration = classDeclaration.AddMembers(ctor);

            if (type.IsEnumType)
            {
                var writeMethod = GenerateEnumWriteMethod(type, libraryTypes);
                var readMethod = GenerateEnumReadMethod(type, libraryTypes);
                classDeclaration = classDeclaration.AddMembers(writeMethod, readMethod);
            }
            else
            {
                var serializeMethod = GenerateSerializeMethod(type, fieldDescriptions, members, libraryTypes);
                var deserializeMethod = GenerateDeserializeMethod(type, fieldDescriptions, members, libraryTypes);
                if (type.IsAbstractType)
                {
                    if (serializeMethod != null) classDeclaration = classDeclaration.AddMembers(serializeMethod);
                    if (deserializeMethod != null) classDeclaration = classDeclaration.AddMembers(deserializeMethod);
                }
                else
                {
                    var writeFieldMethod = GenerateCompoundTypeWriteFieldMethod(type, libraryTypes);
                    var readValueMethod = GenerateCompoundTypeReadValueMethod(type, fieldDescriptions, libraryTypes);
                    classDeclaration = classDeclaration.AddMembers(serializeMethod, deserializeMethod, writeFieldMethod, readValueMethod);

                    var serializerInterface = type.IsValueType ? libraryTypes.ValueSerializer : type.IsSealedType ? null : libraryTypes.BaseCodec_1;
                    if (serializerInterface != null)
                        classDeclaration = classDeclaration.AddBaseListTypes(SimpleBaseType(serializerInterface.ToTypeSyntax(type.TypeSyntax)));
                }
            }

            if (type.IsGenericType)
            {
                classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, type.TypeParameters);
            }

            return classDeclaration;
        }

        public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

        public static string GetSimpleClassName(string name) => $"Codec_{name}";

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
                    case TypeFieldDescription type:
                        return FieldDeclaration(
                                VariableDeclaration(
                                    type.FieldType,
                                    SingletonSeparatedList(VariableDeclarator(type.FieldName)
                                        .WithInitializer(EqualsValueClause(TypeOfExpression(type.UnderlyingTypeSyntax))))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
                    case CodecFieldTypeFieldDescription type:
                        return FieldDeclaration(
                                VariableDeclaration(
                                    type.FieldType,
                                    SingletonSeparatedList(VariableDeclarator(type.FieldName)
                                        .WithInitializer(EqualsValueClause(TypeOfExpression(type.CodecFieldType))))))
                            .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ReadOnlyKeyword));
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
                    case CodecFieldDescription or BaseCodecFieldDescription when !field.IsInjected:
                        if (!codecProviderAdded)
                        {
                            parameters.Add(Parameter(Identifier("codecProvider")).WithType(libraryTypes.ICodecProvider.ToTypeSyntax()));
                            codecProviderAdded = true;
                        }

                        var codec = InvocationExpression(
                            IdentifierName("OrleansGeneratedCodeHelper").Member(GenericName(Identifier("GetService"), TypeArgumentList(SingletonSeparatedList(field.FieldType)))),
                            ArgumentList(SeparatedList(new[] { Argument(ThisExpression()), Argument(IdentifierName("codecProvider")) })));

                        statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, field.FieldName.ToIdentifierName(), codec)));
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
            LibraryTypes libraryTypes)
        {
            var fields = new List<GeneratedFieldDescription>();

            if (!serializableTypeDescription.IsAbstractType)
            {
                fields.Add(new CodecFieldTypeFieldDescription(libraryTypes.Type.ToTypeSyntax(), CodecFieldTypeFieldName, serializableTypeDescription.TypeSyntax));
            }

            if (serializableTypeDescription.HasComplexBaseType)
            {
                fields.Add(GetBaseTypeField(serializableTypeDescription, libraryTypes));
            }

            if (serializableTypeDescription.UseActivator && !serializableTypeDescription.IsAbstractType)
            {
                fields.Add(new ActivatorFieldDescription(libraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
            }

            int typeIndex = 0;
            foreach (var member in serializableTypeDescription.Members.Distinct(MemberDescriptionTypeComparer.Default))
            {
                fields.Add(new TypeFieldDescription(libraryTypes.Type.ToTypeSyntax(), $"_type{typeIndex}", member.TypeSyntax, member.Type));

                // Add a codec field for any field in the target which does not have a static codec.
                if (libraryTypes.StaticCodecs.FindByUnderlyingType(member.Type) is null)
                {
                    fields.Add(GetCodecDescription(member, typeIndex));
                }

                typeIndex++;
            }

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

            CodecFieldDescription GetCodecDescription(IMemberDescription member, int index)
            {
                var t = member.Type;
                TypeSyntax codecType = null;
                if (t.HasAttribute(libraryTypes.GenerateSerializerAttribute)
                    && (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, libraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(libraryTypes.TypeManifestProviderAttribute))
                    && t is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 0 })
                {
                    // Use the concrete generated type and avoid expensive interface dispatch (except for complex nested cases that will fall back to IFieldCodec<TField>)
                    SimpleNameSyntax name;
                    if (t is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
                    {
                        // Construct the full generic type name
                        name = GenericName(Identifier(GetSimpleClassName(t.Name)), TypeArgumentList(SeparatedList(namedTypeSymbol.TypeArguments.Select(arg => member.GetTypeSyntax(arg)))));
                    }
                    else
                    {
                        name = IdentifierName(GetSimpleClassName(t.Name));
                    }
                    codecType = QualifiedName(ParseName(GetGeneratedNamespaceName(t)), name);
                }
                else if (t is IArrayTypeSymbol { IsSZArray: true } array)
                {
                    codecType = libraryTypes.ArrayCodec.Construct(array.ElementType).ToTypeSyntax();
                }
                else if (libraryTypes.WellKnownCodecs.FindByUnderlyingType(t) is { } codec)
                {
                    // The codec is not a static codec and is also not a generic codec.
                    codecType = codec.CodecType.ToTypeSyntax();
                }
                else if (t is INamedTypeSymbol { ConstructedFrom: { } unboundFieldType } named && libraryTypes.WellKnownCodecs.FindByUnderlyingType(unboundFieldType) is { } genericCodec)
                {
                    // Construct the generic codec type using the field's type arguments.
                    codecType = genericCodec.CodecType.Construct(named.TypeArguments.ToArray()).ToTypeSyntax();
                }
                else
                {
                    // Use the IFieldCodec<TField> interface
                    codecType = libraryTypes.FieldCodec_1.ToTypeSyntax(member.TypeSyntax);
                }

                return new CodecFieldDescription(codecType, $"_codec{index}", t);
            }
        }

        private static BaseCodecFieldDescription GetBaseTypeField(ISerializableTypeDescription serializableTypeDescription, LibraryTypes libraryTypes)
        {
            var baseType = serializableTypeDescription.BaseType;
            if (baseType.HasAttribute(libraryTypes.GenerateSerializerAttribute)
                && (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, libraryTypes.Compilation.Assembly) || baseType.ContainingAssembly.HasAttribute(libraryTypes.TypeManifestProviderAttribute))
                && baseType is not INamedTypeSymbol { IsGenericType: true })
            {
                // Use the concrete generated type and avoid expensive interface dispatch (except for generic types that will fall back to IBaseCodec<T>)
                return new(QualifiedName(ParseName(GetGeneratedNamespaceName(baseType)), IdentifierName(GetSimpleClassName(baseType.Name))), true);
            }

            return new(libraryTypes.BaseCodec_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax));
        }

        private static MemberDeclarationSyntax GenerateSerializeMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> serializerFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes)
        {
            var returnType = PredefinedType(Token(SyntaxKind.VoidKeyword));

            var writerParam = "writer".ToIdentifierName();
            var instanceParam = "instance".ToIdentifierName();

            var body = new List<StatementSyntax>();
            if (type.HasComplexBaseType)
            {
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            BaseTypeSerializerFieldName.ToIdentifierName().Member(SerializeMethodName),
                            ArgumentList(SeparatedList(new[] { Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)), Argument(instanceParam) })))));
                body.Add(ExpressionStatement(InvocationExpression(writerParam.Member("WriteEndBase"), ArgumentList())));
            }

            AddSerializationCallbacks(type, instanceParam, "OnSerializing", body);

            // Order members according to their FieldId, since fields must be serialized in order and FieldIds are serialized as deltas.
            var previousFieldIdVar = "previousFieldId".ToIdentifierName();
            if (type.OmitDefaultMemberValues && members.Count > 0)
            {
                // C#: uint previousFieldId = 0;
                body.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        PredefinedType(Token(SyntaxKind.UIntKeyword)),
                        SingletonSeparatedList(VariableDeclarator(previousFieldIdVar.Identifier)
                            .WithInitializer(EqualsValueClause(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))))))));
            }

            if (type.SupportsPrimaryConstructorParameters)
            {
                AddSerializationMembers(type, serializerFields, members.Where(m => m.IsPrimaryConstructorParameter), libraryTypes, writerParam, instanceParam, previousFieldIdVar, body);
                body.Add(ExpressionStatement(InvocationExpression(writerParam.Member("WriteEndBase"), ArgumentList())));
            }

            AddSerializationMembers(type, serializerFields, members.Where(m => !m.IsPrimaryConstructorParameter), libraryTypes, writerParam, instanceParam, previousFieldIdVar, body);

            AddSerializationCallbacks(type, instanceParam, "OnSerialized", body);

            if (body.Count == 0 && type.IsAbstractType)
                return null;

            var parameters = new[]
            {
                Parameter("writer".ToIdentifier()).WithType(libraryTypes.Writer.ToTypeSyntax()).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("instance".ToIdentifier()).WithType(type.TypeSyntax)
            };

            if (type.IsValueType)
            {
                parameters[1] = parameters[1].WithModifiers(TokenList(Token(SyntaxKind.ScopedKeyword), Token(SyntaxKind.RefKeyword)));
            }

            var res = MethodDeclaration(returnType, SerializeMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());

            res = type.IsAbstractType
                ? res.AddModifiers(Token(SyntaxKind.OverrideKeyword))
                : res.AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter").AddConstraints(TypeConstraint(libraryTypes.IBufferWriter.ToTypeSyntax(PredefinedType(Token(SyntaxKind.ByteKeyword))))));

            return res;
        }

        private static void AddSerializationMembers(ISerializableTypeDescription type, List<GeneratedFieldDescription> serializerFields, IEnumerable<ISerializableMember> members, LibraryTypes libraryTypes, IdentifierNameSyntax writerParam, IdentifierNameSyntax instanceParam, IdentifierNameSyntax previousFieldIdVar, List<StatementSyntax> body)
        {
            uint previousFieldId = 0;
            foreach (var member in members.OrderBy(m => m.Member.FieldId))
            {
                var description = member.Member;
                ExpressionSyntax fieldIdDeltaExpr;
                if (type.OmitDefaultMemberValues)
                {
                    // C#: <fieldId> - previousFieldId
                    fieldIdDeltaExpr = BinaryExpression(SyntaxKind.SubtractExpression, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(description.FieldId)), previousFieldIdVar);
                }
                else
                {
                    var fieldIdDelta = description.FieldId - previousFieldId;
                    previousFieldId = description.FieldId;
                    fieldIdDeltaExpr = LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(fieldIdDelta));
                }

                // Codecs can either be static classes or injected into the constructor.
                // Either way, the member signatures are the same.
                var memberType = description.Type;
                var staticCodec = libraryTypes.StaticCodecs.FindByUnderlyingType(memberType);
                ExpressionSyntax codecExpression;
                if (staticCodec != null && libraryTypes.Compilation.IsSymbolAccessibleWithin(staticCodec.CodecType, libraryTypes.Compilation.Assembly))
                {
                    codecExpression = staticCodec.CodecType.ToNameSyntax();
                }
                else
                {
                    var instanceCodec = serializerFields.First(f => f is CodecFieldDescription cf && SymbolEqualityComparer.Default.Equals(cf.UnderlyingType, memberType));
                    codecExpression = IdentifierName(instanceCodec.FieldName);
                }

                var expectedType = serializerFields.First(f => f is TypeFieldDescription tf && SymbolEqualityComparer.Default.Equals(tf.UnderlyingType, memberType));
                var writeFieldExpr = ExpressionStatement(
                        InvocationExpression(
                            codecExpression.Member("WriteField"),
                            ArgumentList(
                                SeparatedList(
                                    new[]
                                    {
                                        Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(fieldIdDeltaExpr),
                                        Argument(expectedType.FieldName.ToIdentifierName()),
                                        Argument(member.GetGetter(instanceParam))
                                    }))));
                if (!type.OmitDefaultMemberValues)
                {
                    body.Add(writeFieldExpr);
                }
                else
                {
                    ExpressionSyntax condition = member.IsValueType switch
                    {
                        true => BinaryExpression(SyntaxKind.NotEqualsExpression, member.GetGetter(instanceParam), LiteralExpression(SyntaxKind.DefaultLiteralExpression)),
                        false => IsPatternExpression(member.GetGetter(instanceParam), TypePattern(PredefinedType(Token(SyntaxKind.ObjectKeyword))))
                    };

                    body.Add(IfStatement(
                        condition,
                        Block(
                            writeFieldExpr,
                            ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, previousFieldIdVar, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(description.FieldId)))))));
                }
            }
        }

        private static MemberDeclarationSyntax GenerateDeserializeMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> serializerFields,
            List<ISerializableMember> members,
            LibraryTypes libraryTypes)
        {
            var returnType = PredefinedType(Token(SyntaxKind.VoidKeyword));

            var readerParam = "reader".ToIdentifierName();
            var instanceParam = "instance".ToIdentifierName();
            var idVar = "id".ToIdentifierName();
            var headerVar = "header".ToIdentifierName();

            var body = new List<StatementSyntax>();

            if (type.HasComplexBaseType)
            {
                // C#: _baseTypeSerializer.Deserialize(ref reader, instance);
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            BaseTypeSerializerFieldName.ToIdentifierName().Member(DeserializeMethodName),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                Argument(instanceParam)
                            })))));
            }

            AddSerializationCallbacks(type, instanceParam, "OnDeserializing", body);

            int emptyBodyCount;
            var normalMembers = members.FindAll(m => !m.IsPrimaryConstructorParameter);
            if (members.Count == 0 || normalMembers.Count == 0 && !type.SupportsPrimaryConstructorParameters)
            {
                // C#: reader.ConsumeEndBaseOrEndObject();
                body.Add(ExpressionStatement(InvocationExpression(readerParam.Member("ConsumeEndBaseOrEndObject"))));
                emptyBodyCount = 1;
            }
            else
            {
                // C#: uint id = 0;
                body.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        PredefinedType(Token(SyntaxKind.UIntKeyword)),
                        SingletonSeparatedList(VariableDeclarator(idVar.Identifier, null, EqualsValueClause(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))))))));

                // C#: Field header = default;
                body.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        libraryTypes.Field.ToTypeSyntax(),
                        SingletonSeparatedList(VariableDeclarator(headerVar.Identifier, null, EqualsValueClause(LiteralExpression(SyntaxKind.DefaultLiteralExpression)))))));

                emptyBodyCount = 2;

                if (type.SupportsPrimaryConstructorParameters)
                {
                    body.Add(GetDeserializerLoop(members.FindAll(m => m.IsPrimaryConstructorParameter)));
                    body.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, idVar, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)))));
                    body.Add(IfStatement(headerVar.Member("IsEndBaseFields"), GetDeserializerLoop(normalMembers)));
                }
                else
                {
                    body.Add(GetDeserializerLoop(normalMembers));
                }
            }

            AddSerializationCallbacks(type, instanceParam, "OnDeserialized", body);

            if (body.Count == emptyBodyCount && type.IsAbstractType)
                return null;

            var genericParam = ParseTypeName("TReaderInput");
            var parameters = new[]
            {
                Parameter(readerParam.Identifier).WithType(libraryTypes.Reader.ToTypeSyntax(genericParam)).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter(instanceParam.Identifier).WithType(type.TypeSyntax)
            };

            if (type.IsValueType)
            {
                parameters[1] = parameters[1].WithModifiers(TokenList(Token(SyntaxKind.ScopedKeyword), Token(SyntaxKind.RefKeyword)));
            }

            var res = MethodDeclaration(returnType, DeserializeMethodName)
                .AddTypeParameterListParameters(TypeParameter("TReaderInput"))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());

            if (type.IsAbstractType)
                res = res.AddModifiers(Token(SyntaxKind.OverrideKeyword));

            return res;

            // Create the loop body.
            StatementSyntax GetDeserializerLoop(List<ISerializableMember> members)
            {
                var refHeaderVar = ArgumentList(SingletonSeparatedList(Argument(null, Token(SyntaxKind.RefKeyword), headerVar)));
                if (members.Count == 0)
                {
                    // C#: reader.ReadFieldHeader(ref header);
                    // C#: reader.ConsumeEndBaseOrEndObject(ref header);
                    return Block(
                        ExpressionStatement(InvocationExpression(readerParam.Member("ReadFieldHeader"), refHeaderVar)),
                        ExpressionStatement(InvocationExpression(readerParam.Member("ConsumeEndBaseOrEndObject"), refHeaderVar)));
                }

                var loopBody = new List<StatementSyntax>();
                var codecs = serializerFields.OfType<ICodecDescription>()
                        .Concat(libraryTypes.StaticCodecs)
                        .ToList();

                // C#: reader.ReadFieldHeader(ref header);
                // C#: if (header.IsEndBaseOrEndObject) break;
                // C#: id += header.FieldIdDelta;
                var readFieldHeader = ExpressionStatement(InvocationExpression(readerParam.Member("ReadFieldHeader"), ArgumentList(SingletonSeparatedList(Argument(null, Token(SyntaxKind.RefKeyword), headerVar)))));
                var endObjectCheck = IfStatement(headerVar.Member("IsEndBaseOrEndObject"), BreakStatement());
                var idUpdate = ExpressionStatement(AssignmentExpression(SyntaxKind.AddAssignmentExpression, idVar, headerVar.Member("FieldIdDelta")));
                loopBody.Add(readFieldHeader);
                loopBody.Add(endObjectCheck);
                loopBody.Add(idUpdate);

                members.Sort((x, y) => x.Member.FieldId.CompareTo(y.Member.FieldId));
                var contiguousIds = members[members.Count - 1].Member.FieldId == members.Count - 1;
                foreach (var member in members)
                {
                    var description = member.Member;

                    // C#: instance.<member> = <codec>.ReadValue(ref reader, header);
                    // Codecs can either be static classes or injected into the constructor.
                    // Either way, the member signatures are the same.
                    var codec = codecs.First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, description.Type));
                    var memberType = description.Type;
                    var staticCodec = libraryTypes.StaticCodecs.FindByUnderlyingType(memberType);
                    ExpressionSyntax codecExpression;
                    if (staticCodec != null)
                    {
                        codecExpression = staticCodec.CodecType.ToNameSyntax();
                    }
                    else
                    {
                        var instanceCodec = serializerFields.Find(c => c is CodecFieldDescription f && SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
                        codecExpression = IdentifierName(instanceCodec.FieldName);
                    }

                    ExpressionSyntax readValueExpression = InvocationExpression(
                        codecExpression.Member("ReadValue"),
                        ArgumentList(SeparatedList(new[] { Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)), Argument(headerVar) })));
                    if (!codec.UnderlyingType.Equals(member.TypeSyntax))
                    {
                        // If the member type type differs from the codec type (eg because the member is an array), cast the result.
                        readValueExpression = CastExpression(description.TypeSyntax, readValueExpression);
                    }

                    var memberAssignment = ExpressionStatement(member.GetSetter(instanceParam, readValueExpression));

                    BlockSyntax ifBody;
                    if (member != members[members.Count - 1])
                    {
                        ifBody = Block(memberAssignment, readFieldHeader, endObjectCheck, idUpdate);
                    }
                    else if (contiguousIds)
                    {
                        ifBody = Block(memberAssignment, readFieldHeader);
                    }
                    else
                    {
                        idUpdate = ExpressionStatement(PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, idVar));
                        ifBody = Block(memberAssignment, readFieldHeader, endObjectCheck, idUpdate);
                    }

                    // C#: if (id == <fieldId>) { ... }
                    var ifStatement = IfStatement(BinaryExpression(SyntaxKind.EqualsExpression, idVar, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal((int)description.FieldId))),
                        ifBody);

                    loopBody.Add(ifStatement);
                }

                // Consume any unknown fields
                if (contiguousIds)
                {
                    // C#: reader.ConsumeEndBaseOrEndObject(ref header); break;
                    loopBody.Add(ExpressionStatement(InvocationExpression(readerParam.Member("ConsumeEndBaseOrEndObject"), refHeaderVar)));
                    loopBody.Add(BreakStatement());
                }
                else
                {
                    // C#: reader.ConsumeUnknownField(ref header);
                    loopBody.Add(ExpressionStatement(InvocationExpression(readerParam.Member("ConsumeUnknownField"), refHeaderVar)));
                }

                return WhileStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression), Block(loopBody));
            }
        }

        private static void AddSerializationCallbacks(ISerializableTypeDescription type, IdentifierNameSyntax instanceParam, string callbackMethodName, List<StatementSyntax> body)
        {
            for (var hookIndex = 0; hookIndex < type.SerializationHooks.Count; ++hookIndex)
            {
                var hookType = type.SerializationHooks[hookIndex];
                var member = hookType.GetAllMembers<IMethodSymbol>(callbackMethodName, Accessibility.Public).FirstOrDefault();
                if (member is null || member.Parameters.Length != 1)
                {
                    continue;
                }

                var argument = Argument(instanceParam);
                if (member.Parameters[0].RefKind == RefKind.Ref)
                {
                    argument = argument.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                }

                body.Add(ExpressionStatement(InvocationExpression(
                    IdentifierName($"_hook{hookIndex}").Member(callbackMethodName),
                    ArgumentList(SeparatedList(new[] { argument })))));
            }
        }

        private static MemberDeclarationSyntax GenerateCompoundTypeWriteFieldMethod(
            ISerializableTypeDescription type,
            LibraryTypes libraryTypes)
        {
            var returnType = PredefinedType(Token(SyntaxKind.VoidKeyword));

            var writerParam = "writer".ToIdentifierName();
            var fieldIdDeltaParam = "fieldIdDelta".ToIdentifierName();
            var expectedTypeParam = "expectedType".ToIdentifierName();
            var valueParam = "value".ToIdentifierName();

            var innerBody = new List<StatementSyntax>();

            if (type.IsValueType)
            {
                // C#: ReferenceCodec.MarkValueField(reader.Session);
                innerBody.Add(ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("MarkValueField"), ArgumentList(SingletonSeparatedList(Argument(writerParam.Member("Session")))))));
            }
            else
            {
                if (type.TrackReferences)
                {
                    // C#: if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value)) return;
                    innerBody.Add(
                        IfStatement(
                            InvocationExpression(
                                IdentifierName("ReferenceCodec").Member("TryWriteReferenceField"),
                                ArgumentList(SeparatedList(new[]
                                {
                            Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                            Argument(fieldIdDeltaParam),
                            Argument(expectedTypeParam),
                            Argument(valueParam)
                                }))),
                            ReturnStatement())
                    );
                }
                else
                {
                    // C#: if (value is null) { ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta); return; }
                    innerBody.Add(
                        IfStatement(
                            IsPatternExpression(valueParam, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
                            Block(
                                ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("WriteNullReference"),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(fieldIdDeltaParam)
                                    })))),
                                ReturnStatement()))
                    );
                    
                    // C#: ReferenceCodec.MarkValueField(reader.Session);
                    innerBody.Add(ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("MarkValueField"), ArgumentList(SingletonSeparatedList(Argument(writerParam.Member("Session")))))));
                }
            }

            // C#: writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            innerBody.Add(
                ExpressionStatement(InvocationExpression(writerParam.Member("WriteStartObject"),
                ArgumentList(SeparatedList(new[]{
                            Argument(fieldIdDeltaParam),
                            Argument(expectedTypeParam),
                            Argument(IdentifierName(CodecFieldTypeFieldName))
                    })))
                ));

            // C#: this.Serialize(ref writer, [ref] value);
            var valueParamArgument = type.IsValueType switch
            {
                true => Argument(valueParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                false => Argument(valueParam)
            };

            innerBody.Add(
                ExpressionStatement(
                    InvocationExpression(
                        IdentifierName(SerializeMethodName),
                        ArgumentList(
                            SeparatedList(
                                new[]
                                {
                                    Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                    valueParamArgument
                                })))));

            // C#: writer.WriteEndObject();
            innerBody.Add(ExpressionStatement(InvocationExpression(writerParam.Member("WriteEndObject"))));

            List<StatementSyntax> body;
            if (type.IsSealedType)
            {
                body = innerBody;
            }
            else
            {
                // For types which are not sealed/value types, add some extra logic to support sub-types:
                body = new()
                {
                    // C#: if (value is null || value.GetType() == typeof(TField)) { <inner body> }
                    // C#: else writer.SerializeUnexpectedType(fieldIdDelta, expectedType, value);
                    IfStatement(
                        BinaryExpression(SyntaxKind.LogicalOrExpression,
                            IsPatternExpression(valueParam, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
                            BinaryExpression(SyntaxKind.EqualsExpression, InvocationExpression(valueParam.Member("GetType")), TypeOfExpression(type.TypeSyntax))),
                        Block(innerBody),
                        ElseClause(ExpressionStatement(
                            InvocationExpression(
                                writerParam.Member("SerializeUnexpectedType"),
                                ArgumentList(
                                    SeparatedList(new [] {
                                        Argument(fieldIdDeltaParam),
                                        Argument(expectedTypeParam),
                                        Argument(valueParam)
                                    })))
                        )))
                };
            }

            var parameters = new[]
            {
                Parameter("writer".ToIdentifier()).WithType(libraryTypes.Writer.ToTypeSyntax()).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("fieldIdDelta".ToIdentifier()).WithType(PredefinedType(Token(SyntaxKind.UIntKeyword))),
                Parameter("expectedType".ToIdentifier()).WithType(libraryTypes.Type.ToTypeSyntax()),
                Parameter("value".ToIdentifier()).WithType(type.TypeSyntax)
            };

            return MethodDeclaration(returnType, WriteFieldMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
                .AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter").AddConstraints(TypeConstraint(libraryTypes.IBufferWriter.ToTypeSyntax(PredefinedType(Token(SyntaxKind.ByteKeyword))))))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static MemberDeclarationSyntax GenerateCompoundTypeReadValueMethod(
            ISerializableTypeDescription type,
            List<GeneratedFieldDescription> serializerFields,
            LibraryTypes libraryTypes)
        {
            var readerParam = "reader".ToIdentifierName();
            var fieldParam = "field".ToIdentifierName();
            var resultVar = "result".ToIdentifierName();
            var readerInputTypeParam = ParseTypeName("TReaderInput");

            var body = new List<StatementSyntax>();
            var innerBody = type.IsSealedType ? body : new List<StatementSyntax>();

            if (!type.IsValueType)
            {
                // C#: if (field.IsReference) return ReferenceCodec.ReadReference<TField, TReaderInput>(ref reader, field);
                body.Add(
                    IfStatement(
                        fieldParam.Member("IsReference"),
                        ReturnStatement(InvocationExpression(
                            IdentifierName("ReferenceCodec").Member("ReadReference", new[] { type.TypeSyntax, readerInputTypeParam }),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                Argument(fieldParam),
                            })))))
                    );
            }

            // C#: field.EnsureWireTypeTagDelimited();
            body.Add(ExpressionStatement(InvocationExpression(fieldParam.Member("EnsureWireTypeTagDelimited"))));

            ExpressionSyntax createValueExpression = type.UseActivator switch
            {
                true => InvocationExpression(serializerFields.OfType<ActivatorFieldDescription>().Single().FieldName.ToIdentifierName().Member("Create")),
                false => type.GetObjectCreationExpression(libraryTypes)
            };

            // C#: var result = _activator.Create();
            // or C#: var result = new TField();
            // or C#: var result = default(TField);
            innerBody.Add(LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var"),
                    SingletonSeparatedList(VariableDeclarator(resultVar.Identifier)
                    .WithInitializer(EqualsValueClause(createValueExpression))))));

            if (type.TrackReferences)
            {
                // C#: ReferenceCodec.RecordObject(reader.Session, result);
                innerBody.Add(ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("RecordObject"), ArgumentList(SeparatedList(new[] { Argument(readerParam.Member("Session")), Argument(resultVar) })))));
            }
            else
            {
                // C#: ReferenceCodec.MarkValueField(reader.Session);
                innerBody.Add(ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("MarkValueField"), ArgumentList(SingletonSeparatedList(Argument(readerParam.Member("Session")))))));
            }

            // C#: this.Deserializer(ref reader, [ref] result);
            var resultArgument = type.IsValueType switch
            {
                true => Argument(resultVar).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                false => Argument(resultVar)
            };
            innerBody.Add(
                ExpressionStatement(
                    InvocationExpression(
                        IdentifierName(DeserializeMethodName),
                        ArgumentList(
                            SeparatedList(
                                new[]
                                {
                                    Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                    resultArgument
                                })))));

            innerBody.Add(ReturnStatement(resultVar));

            if (!type.IsSealedType)
            {
                // C#: var fieldType = field.FieldType;
                var valueTypeField = "valueType".ToIdentifierName();
                body.Add(
                    LocalDeclarationStatement(
                        VariableDeclaration(
                            libraryTypes.Type.ToTypeSyntax(),
                            SingletonSeparatedList(VariableDeclarator(valueTypeField.Identifier)
                                .WithInitializer(EqualsValueClause(fieldParam.Member("FieldType")))))));
                body.Add(
                    IfStatement(
                        BinaryExpression(SyntaxKind.LogicalOrExpression,
                        IsPatternExpression(valueTypeField, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
                        BinaryExpression(SyntaxKind.EqualsExpression, valueTypeField, IdentifierName(CodecFieldTypeFieldName))),
                        Block(innerBody)));

                body.Add(ReturnStatement(
                                InvocationExpression(
                                    readerParam.Member("DeserializeUnexpectedType", new[] { readerInputTypeParam, type.TypeSyntax }),
                                    ArgumentList(
                                        SingletonSeparatedList(Argument(null, Token(SyntaxKind.RefKeyword), fieldParam))))));
            }

            var parameters = new[]
            {
                Parameter(readerParam.Identifier).WithType(libraryTypes.Reader.ToTypeSyntax(readerInputTypeParam)).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter(fieldParam.Identifier).WithType(libraryTypes.Field.ToTypeSyntax())
            };

            return MethodDeclaration(type.TypeSyntax, ReadValueMethodName)
                .AddTypeParameterListParameters(TypeParameter("TReaderInput"))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static MemberDeclarationSyntax GenerateEnumWriteMethod(
            ISerializableTypeDescription type,
            LibraryTypes libraryTypes)
        {
            var returnType = PredefinedType(Token(SyntaxKind.VoidKeyword));

            var writerParam = "writer".ToIdentifierName();
            var fieldIdDeltaParam = "fieldIdDelta".ToIdentifierName();
            var expectedTypeParam = "expectedType".ToIdentifierName();
            var valueParam = "value".ToIdentifierName();

            var body = new List<StatementSyntax>();

            // Codecs can either be static classes or injected into the constructor.
            // Either way, the member signatures are the same.
            var staticCodec = libraryTypes.StaticCodecs.FindByUnderlyingType(type.BaseType);
            var codecExpression = staticCodec.CodecType.ToNameSyntax();

            body.Add(
                ExpressionStatement(
                    InvocationExpression(
                        codecExpression.Member("WriteField"),
                        ArgumentList(
                            SeparatedList(
                                new[]
                                {
                                    Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                    Argument(fieldIdDeltaParam),
                                    Argument(expectedTypeParam),
                                    Argument(CastExpression(type.BaseTypeSyntax, valueParam)),
                                    Argument(IdentifierName(CodecFieldTypeFieldName))
                                })))));

            var parameters = new[]
            {
                Parameter("writer".ToIdentifier()).WithType(libraryTypes.Writer.ToTypeSyntax()).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("fieldIdDelta".ToIdentifier()).WithType(PredefinedType(Token(SyntaxKind.UIntKeyword))),
                Parameter("expectedType".ToIdentifier()).WithType(libraryTypes.Type.ToTypeSyntax()),
                Parameter("value".ToIdentifier()).WithType(type.TypeSyntax)
            };

            return MethodDeclaration(returnType, WriteFieldMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
                .AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter").AddConstraints(TypeConstraint(libraryTypes.IBufferWriter.ToTypeSyntax(PredefinedType(Token(SyntaxKind.ByteKeyword))))))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static MemberDeclarationSyntax GenerateEnumReadMethod(
            ISerializableTypeDescription type,
            LibraryTypes libraryTypes)
        {
            var readerParam = "reader".ToIdentifierName();
            var fieldParam = "field".ToIdentifierName();

            var staticCodec = libraryTypes.StaticCodecs.FindByUnderlyingType(type.BaseType);
            ExpressionSyntax codecExpression = staticCodec.CodecType.ToNameSyntax();
            ExpressionSyntax readValueExpression = InvocationExpression(
                codecExpression.Member("ReadValue"),
                ArgumentList(SeparatedList(new[] { Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)), Argument(fieldParam) })));

            readValueExpression = CastExpression(type.TypeSyntax, readValueExpression);
            var body = new List<StatementSyntax>
            {
                ReturnStatement(readValueExpression)
            };

            var genericParam = ParseTypeName("TReaderInput");
            var parameters = new[]
            {
                Parameter(readerParam.Identifier).WithType(libraryTypes.Reader.ToTypeSyntax(genericParam)).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter(fieldParam.Identifier).WithType(libraryTypes.Field.ToTypeSyntax())
            };

            return MethodDeclaration(type.TypeSyntax, ReadValueMethodName)
                .AddTypeParameterListParameters(TypeParameter("TReaderInput"))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        internal abstract class GeneratedFieldDescription
        {
            protected GeneratedFieldDescription(TypeSyntax fieldType, string fieldName)
            {
                FieldType = fieldType;
                FieldName = fieldName;
            }

            public readonly TypeSyntax FieldType;
            public readonly string FieldName;
            public abstract bool IsInjected { get; }
        }

        internal sealed class BaseCodecFieldDescription : GeneratedFieldDescription
        {
            public BaseCodecFieldDescription(TypeSyntax fieldType, bool concreteType = false) : base(fieldType, BaseTypeSerializerFieldName)
                => IsInjected = !concreteType;

            public override bool IsInjected { get; }
        }

        internal sealed class ActivatorFieldDescription : GeneratedFieldDescription 
        {
            public ActivatorFieldDescription(TypeSyntax fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
        }

        internal sealed class CodecFieldDescription : GeneratedFieldDescription, ICodecDescription
        {
            public CodecFieldDescription(TypeSyntax fieldType, string fieldName, ITypeSymbol underlyingType) : base(fieldType, fieldName)
            {
                UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => false;
        }

        internal sealed class TypeFieldDescription : GeneratedFieldDescription
        {
            public TypeFieldDescription(TypeSyntax fieldType, string fieldName, TypeSyntax underlyingTypeSyntax, ITypeSymbol underlyingType) : base(fieldType, fieldName)
            {
                UnderlyingType = underlyingType;
                UnderlyingTypeSyntax = underlyingTypeSyntax; 
            }

            public TypeSyntax UnderlyingTypeSyntax { get; }
            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => false;
        }

        internal sealed class CodecFieldTypeFieldDescription : GeneratedFieldDescription
        {
            public CodecFieldTypeFieldDescription(TypeSyntax fieldType, string fieldName, TypeSyntax codecFieldType) : base(fieldType, fieldName)
            {
                CodecFieldType = codecFieldType;
            }

            public TypeSyntax CodecFieldType { get; }
            public override bool IsInjected => false;
        }

        internal sealed class FieldAccessorDescription : GeneratedFieldDescription
        {
            public FieldAccessorDescription(TypeSyntax fieldType, string fieldName, ExpressionSyntax initializationSyntax) : base(fieldType, fieldName)
                => InitializationSyntax = initializationSyntax;

            public override bool IsInjected => false;

            public readonly ExpressionSyntax InitializationSyntax;
        }

        internal sealed class SerializationHookFieldDescription : GeneratedFieldDescription
        {
            public SerializationHookFieldDescription(TypeSyntax fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
        }

        internal interface ISerializableMember
        {
            bool IsShallowCopyable { get; }
            bool IsValueType { get; }
            bool IsPrimaryConstructorParameter { get; }

            IMemberDescription Member { get; }

            /// <summary>
            /// Gets syntax representing the type of this field.
            /// </summary>
            TypeSyntax TypeSyntax { get; }

            /// <summary>
            /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            ExpressionSyntax GetGetter(ExpressionSyntax instance);

            /// <summary>
            /// Returns syntax for setting the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="value">Syntax for the new value.</param>
            /// <returns>Syntax for setting the value of this field.</returns>
            ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value);

            FieldAccessorDescription GetGetterFieldDescription();
            FieldAccessorDescription GetSetterFieldDescription();
        }

        /// <summary>
        /// Represents a serializable member (field/property) of a type.
        /// </summary>
        internal class SerializableMethodMember : ISerializableMember
        {
            private readonly MethodParameterFieldDescription _member;

            public SerializableMethodMember(MethodParameterFieldDescription member)
            {
                _member = member;
            }

            IMemberDescription ISerializableMember.Member => _member;
            public MethodParameterFieldDescription Member => _member;

            private LibraryTypes LibraryTypes => _member.Method.ContainingInterface.CodeGenerator.LibraryTypes;

            public bool IsShallowCopyable => LibraryTypes.IsShallowCopyable(_member.Parameter.Type) || _member.Parameter.HasAnyAttribute(LibraryTypes.ImmutableAttributes);

            /// <summary>
            /// Gets syntax representing the type of this field.
            /// </summary>
            public TypeSyntax TypeSyntax => _member.TypeSyntax;

            public bool IsValueType => _member.Type.IsValueType;

            public bool IsPrimaryConstructorParameter => _member.IsPrimaryConstructorParameter;

            /// <summary>
            /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            public ExpressionSyntax GetGetter(ExpressionSyntax instance) => instance.Member(_member.FieldName);

            /// <summary>
            /// Returns syntax for setting the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="value">Syntax for the new value.</param>
            /// <returns>Syntax for setting the value of this field.</returns>
            public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value) => AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        instance.Member(_member.FieldName),
                        value);

            public FieldAccessorDescription GetGetterFieldDescription() => null;
            public FieldAccessorDescription GetSetterFieldDescription() => null;
        }

        /// <summary>
        /// Represents a serializable member (field/property) of a type.
        /// </summary>
        internal class SerializableMember : ISerializableMember
        {
            private readonly SemanticModel _model;
            private readonly LibraryTypes _libraryTypes;
            private IPropertySymbol _property;
            private readonly IMemberDescription _member;

            /// <summary>
            /// The ordinal assigned to this field.
            /// </summary>
            private readonly int _ordinal;

            public SerializableMember(LibraryTypes libraryTypes, ISerializableTypeDescription type, IMemberDescription member, int ordinal)
            {
                _libraryTypes = libraryTypes;
                _model = type.SemanticModel;
                _ordinal = ordinal;
                _member = member;
            }

            public bool IsShallowCopyable => _libraryTypes.IsShallowCopyable(_member.Type) || (Property is { } prop && prop.HasAnyAttribute(_libraryTypes.ImmutableAttributes)) || _member.Symbol.HasAnyAttribute(_libraryTypes.ImmutableAttributes);

            public bool IsValueType => Type.IsValueType;

            public IMemberDescription Member => _member;

            /// <summary>
            /// Gets the underlying <see cref="Field"/> instance.
            /// </summary>
            private IFieldSymbol Field => (_member as IFieldDescription)?.Field;

            public ITypeSymbol Type => _member.Type;

            public INamedTypeSymbol ContainingType => _member.ContainingType;

            public string MemberName => Field?.Name ?? Property?.Name;

            /// <summary>
            /// Gets the name of the getter field.
            /// </summary>
            private string GetterFieldName => $"getField{_ordinal}";

            /// <summary>
            /// Gets the name of the setter field.
            /// </summary>
            private string SetterFieldName => $"setField{_ordinal}";

            /// <summary>
            /// Gets a value indicating if the member is a property.
            /// </summary>
            private bool IsProperty => Member.Symbol is IPropertySymbol;

            /// <summary>
            /// Gets a value indicating whether or not this member represents an accessible field. 
            /// </summary>
            private bool IsGettableField => Field is { } field && _model.IsAccessible(0, field) && !IsObsolete;

            /// <summary>
            /// Gets a value indicating whether or not this member represents an accessible, mutable field. 
            /// </summary>
            private bool IsSettableField => Field is { } field && IsGettableField && !field.IsReadOnly;

            /// <summary>
            /// Gets a value indicating whether or not this member represents a property with an accessible, non-obsolete getter. 
            /// </summary>
            private bool IsGettableProperty => Property?.GetMethod is { } getMethod && _model.IsAccessible(0, getMethod) && !IsObsolete;

            /// <summary>
            /// Gets a value indicating whether or not this member represents a property with an accessible, non-obsolete setter. 
            /// </summary>
            private bool IsSettableProperty => Property?.SetMethod is { } setMethod && _model.IsAccessible(0, setMethod) && !setMethod.IsInitOnly && !IsObsolete;

            /// <summary>
            /// Gets syntax representing the type of this field.
            /// </summary>
            public TypeSyntax TypeSyntax => Member.Type.TypeKind == TypeKind.Dynamic
                ? PredefinedType(Token(SyntaxKind.ObjectKeyword)) 
                : _member.GetTypeSyntax(Member.Type);

            /// <summary>
            /// Gets the <see cref="Property"/> which this field is the backing property for, or
            /// <see langword="null" /> if this is not the backing field of an auto-property.
            /// </summary>
            private IPropertySymbol Property => _property ??= _property = Member.Symbol as IPropertySymbol ?? PropertyUtility.GetMatchingProperty(Field);

            /// <summary>
            /// Gets a value indicating whether or not this field is obsolete.
            /// </summary>
            private bool IsObsolete => Member.Symbol.HasAttribute(_libraryTypes.ObsoleteAttribute) ||
                                       Property != null && Property.HasAttribute(_libraryTypes.ObsoleteAttribute);

            public bool IsPrimaryConstructorParameter => _member.IsPrimaryConstructorParameter;

            /// <summary>
            /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            public ExpressionSyntax GetGetter(ExpressionSyntax instance)
            {
                // If the field is the backing field for an accessible auto-property use the property directly.
                ExpressionSyntax result;
                if (IsGettableProperty)
                {
                    result = instance.Member(Property.Name);
                }
                else if (IsGettableField)
                {
                    result = instance.Member(Field.Name);
                }
                else
                {

                    var instanceArg = Argument(instance);
                    if (ContainingType?.IsValueType == true)
                    {
                        instanceArg = instanceArg.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                    }

                    // Retrieve the field using the generated getter.
                    result =
                        InvocationExpression(IdentifierName(GetterFieldName))
                            .AddArgumentListArguments(instanceArg);
                }

                return result;
            }

            /// <summary>
            /// Returns syntax for setting the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="value">Syntax for the new value.</param>
            /// <returns>Syntax for setting the value of this field.</returns>
            public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value)
            {
                // If the field is the backing field for an accessible auto-property use the property directly.
                if (IsSettableProperty)
                {
                    return AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        instance.Member(Property.Name),
                        value);
                }

                if (IsSettableField)
                {
                    return AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        instance.Member(Field.Name),

                        value);
                }

                // If the symbol itself is a property but is not settable, then error out, since we do not know how to set it value
                if (IsProperty && !IsPrimaryConstructorParameter)
                {
                    Location location = default;
                    if (Member.Symbol is IPropertySymbol prop && prop.SetMethod is { } setMethod)
                    {
                        location = setMethod.Locations.FirstOrDefault();
                    }

                    location ??= Member.Symbol.Locations.FirstOrDefault();

                    throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSetterDiagnostic.CreateDiagnostic(location, Member.Symbol?.ToDisplayString() ?? $"{ContainingType.ToDisplayString()}.{MemberName}"));
                }

                var instanceArg = Argument(instance);
                if (ContainingType?.IsValueType == true)
                {
                    instanceArg = instanceArg.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                }

                return
                    InvocationExpression(IdentifierName(SetterFieldName))
                        .AddArgumentListArguments(instanceArg, Argument(value));
            }

            public FieldAccessorDescription GetGetterFieldDescription()
            {
                if (IsGettableField || IsGettableProperty) return null;
                return GetFieldAccessor(ContainingType, TypeSyntax, MemberName, GetterFieldName, _libraryTypes, false);
            }

            public FieldAccessorDescription GetSetterFieldDescription()
            {
                if (IsSettableField || IsSettableProperty) return null;
                return GetFieldAccessor(ContainingType, TypeSyntax, MemberName, SetterFieldName, _libraryTypes, true);
            }

            public static FieldAccessorDescription GetFieldAccessor(INamedTypeSymbol containingType, TypeSyntax fieldType, string fieldName, string accessorName, LibraryTypes library, bool setter)
            {
                var valueType = containingType.IsValueType;
                var containingTypeSyntax = containingType.ToTypeSyntax();

                var delegateType = (setter ? (valueType ? library.ValueTypeSetter_2 : library.Action_2) : (valueType ? library.ValueTypeGetter_2 : library.Func_2))
                    .ToTypeSyntax(containingTypeSyntax, fieldType);

                // Generate syntax to initialize the field in the constructor
                var fieldAccessorUtility = AliasQualifiedName("global", IdentifierName("Orleans.Serialization")).Member("Utilities").Member("FieldAccessor");
                var accessorMethod = setter ? (valueType ? "GetValueSetter" : "GetReferenceSetter") : (valueType ? "GetValueGetter" : "GetGetter");
                var accessorInvoke = CastExpression(delegateType,
                    InvocationExpression(fieldAccessorUtility.Member(accessorMethod))
                        .AddArgumentListArguments(Argument(TypeOfExpression(containingTypeSyntax)), Argument(fieldName.GetLiteralExpression())));

                return new(delegateType, accessorName, accessorInvoke);
            }
        }
    }
}
