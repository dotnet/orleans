using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Orleans.CodeGenerator.Diagnostics;

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
            var classDeclaration = ClassDeclaration(simpleClassName)
                .AddBaseListTypes(SimpleBaseType(libraryTypes.FieldCodec_1.ToTypeSyntax(type.TypeSyntax)))
                .AddModifiers(Token(accessibility), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(fieldDeclarations)
                .AddMembers(ctor);

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
                var writeFieldMethod = GenerateCompoundTypeWriteFieldMethod(type, libraryTypes);
                var readValueMethod = GenerateCompoundTypeReadValueMethod(type, fieldDescriptions, libraryTypes);
                classDeclaration = classDeclaration.AddMembers(serializeMethod, deserializeMethod, writeFieldMethod, readValueMethod);

                var serializerInterface = type.IsValueType ? libraryTypes.ValueSerializer : libraryTypes.BaseCodec_1;
                classDeclaration = classDeclaration.AddBaseListTypes(SimpleBaseType(serializerInterface.ToTypeSyntax(type.TypeSyntax)));
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

            var fieldAccessorUtility = AliasQualifiedName("global", IdentifierName("Orleans.Serialization")).Member("Utilities").Member("FieldAccessor");

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
                        case CodecFieldDescription codec when !field.IsInjected:
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
            fields.AddRange(serializableTypeDescription.Members
                .Distinct(MemberDescriptionTypeComparer.Default)
                .Select(member => GetTypeDescription(member)));

            fields.Add(new CodecFieldTypeFieldDescription(libraryTypes.Type.ToTypeSyntax(), CodecFieldTypeFieldName, serializableTypeDescription.TypeSyntax)); 

            if (serializableTypeDescription.HasComplexBaseType)
            {
                fields.Add(new BaseCodecFieldDescription(libraryTypes.BaseCodec_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax), BaseTypeSerializerFieldName));
            }

            if (serializableTypeDescription.UseActivator)
            {
                fields.Add(new ActivatorFieldDescription(libraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
            }

            // Add a codec field for any field in the target which does not have a static codec.
            fields.AddRange(serializableTypeDescription.Members
                .Distinct(MemberDescriptionTypeComparer.Default)
                .Where(t => !libraryTypes.StaticCodecs.Any(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t.Type)))
                .Select(member => GetCodecDescription(member)));

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

            CodecFieldDescription GetCodecDescription(IMemberDescription member)
            {
                TypeSyntax codecType = null;
                if (member.Type.HasAttribute(libraryTypes.GenerateSerializerAttribute)
                    && (!SymbolEqualityComparer.Default.Equals(member.Type.ContainingAssembly, libraryTypes.Compilation.Assembly) || member.Type.ContainingAssembly.HasAttribute(libraryTypes.TypeManifestProviderAttribute)))
                {
                    // Use the concrete generated type and avoid expensive interface dispatch
                    if (member.Type is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
                    {
                        // Construct the full generic type name
                        var ns = ParseName(GetGeneratedNamespaceName(member.Type));
                        var name = GenericName(Identifier(GetSimpleClassName(member.Type.Name)), TypeArgumentList(SeparatedList(namedTypeSymbol.TypeArguments.Select(arg => member.GetTypeSyntax(arg)))));
                        codecType = QualifiedName(ns, name);
                    }
                    else
                    {
                        var simpleName = $"{GetGeneratedNamespaceName(member.Type)}.{GetSimpleClassName(member.Type.Name)}";
                        codecType = ParseTypeName(simpleName);
                    }
                }
                else if (libraryTypes.WellKnownCodecs.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, member.Type)) is WellKnownCodecDescription codec)
                {
                    // The codec is not a static codec and is also not a generic codec.
                    codecType = codec.CodecType.ToTypeSyntax();
                }
                else if (member.Type is INamedTypeSymbol named && libraryTypes.WellKnownCodecs.Find(c => member.Type is INamedTypeSymbol named && named.ConstructedFrom is ISymbol unboundFieldType && SymbolEqualityComparer.Default.Equals(c.UnderlyingType, unboundFieldType)) is WellKnownCodecDescription genericCodec)
                {
                    // Construct the generic codec type using the field's type arguments.
                    codecType = genericCodec.CodecType.Construct(named.TypeArguments.ToArray()).ToTypeSyntax();
                }
                else
                {
                    // Use the IFieldCodec<TField> interface
                    codecType = libraryTypes.FieldCodec_1.ToTypeSyntax(member.TypeSyntax);
                }

                var fieldName = '_' + ToLowerCamelCase(member.TypeNameIdentifier) + "Codec";
                return new CodecFieldDescription(codecType, fieldName, member.Type);
            }

            TypeFieldDescription GetTypeDescription(IMemberDescription member)
            {
                var fieldName = '_' + ToLowerCamelCase(member.TypeNameIdentifier) + "Type";
                return new TypeFieldDescription(libraryTypes.Type.ToTypeSyntax(), fieldName, member.TypeSyntax, member.Type);
            }

            static string ToLowerCamelCase(string input) => char.IsLower(input, 0) ? input : char.ToLowerInvariant(input[0]) + input.Substring(1);
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
                            ThisExpression().Member(BaseTypeSerializerFieldName.ToIdentifierName()).Member(SerializeMethodName),
                            ArgumentList(SeparatedList(new[] { Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)), Argument(instanceParam) })))));
                body.Add(ExpressionStatement(InvocationExpression(writerParam.Member("WriteEndBase"), ArgumentList())));
            }

            body.AddRange(AddSerializationCallbacks(type, instanceParam, "OnSerializing"));

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

            if (type.SupportsPrimaryContstructorParameters)
            {
                body.AddRange(AddSerializationMembers(type, serializerFields, members.Where(m => m.IsPrimaryConstructorParameter), libraryTypes, writerParam, instanceParam, previousFieldIdVar));
                body.Add(ExpressionStatement(InvocationExpression(writerParam.Member("WriteEndBase"), ArgumentList())));
            }

            body.AddRange(AddSerializationMembers(type, serializerFields, members.Where(m => !m.IsPrimaryConstructorParameter), libraryTypes, writerParam, instanceParam, previousFieldIdVar));

            body.AddRange(AddSerializationCallbacks(type, instanceParam, "OnSerialized"));

            var parameters = new[]
            {
                Parameter("writer".ToIdentifier()).WithType(libraryTypes.Writer.ToTypeSyntax()).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("instance".ToIdentifier()).WithType(type.TypeSyntax)
            };

            if (type.IsValueType)
            {
                parameters[1] = parameters[1].WithModifiers(TokenList(Token(SyntaxKind.RefKeyword)));
            }

            return MethodDeclaration(returnType, SerializeMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
                .AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter").AddConstraints(TypeConstraint(libraryTypes.IBufferWriter.Construct(libraryTypes.Byte).ToTypeSyntax())))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static IEnumerable<StatementSyntax> AddSerializationMembers(ISerializableTypeDescription type, List<GeneratedFieldDescription> serializerFields, IEnumerable<ISerializableMember> members, LibraryTypes libraryTypes, IdentifierNameSyntax writerParam, IdentifierNameSyntax instanceParam, IdentifierNameSyntax previousFieldIdVar)
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
                var staticCodec = libraryTypes.StaticCodecs.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, memberType));
                ExpressionSyntax codecExpression;
                if (staticCodec != null && libraryTypes.Compilation.IsSymbolAccessibleWithin(staticCodec.CodecType, libraryTypes.Compilation.Assembly))
                {
                    codecExpression = staticCodec.CodecType.ToNameSyntax();
                }
                else
                {
                    var instanceCodec = serializerFields.OfType<CodecFieldDescription>().First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
                    codecExpression = ThisExpression().Member(instanceCodec.FieldName);
                }

                var expectedType = serializerFields.OfType<TypeFieldDescription>().First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
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
                    yield return writeFieldExpr;
                }
                else
                {
                    ExpressionSyntax condition = member.IsValueType switch
                    {
                        true => BinaryExpression(SyntaxKind.NotEqualsExpression, member.GetGetter(instanceParam), LiteralExpression(SyntaxKind.DefaultLiteralExpression)),
                        false => IsPatternExpression(member.GetGetter(instanceParam), TypePattern(libraryTypes.Object.ToTypeSyntax()))
                    };

                    yield return IfStatement(
                        condition,
                        Block(
                            writeFieldExpr,
                            ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, previousFieldIdVar, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(description.FieldId))))));
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
            var readHeaderLocalFunc = "ReadHeader".ToIdentifierName();
            var readHeaderEndLocalFunc = "ReadHeaderExpectingEndBaseOrEndObject".ToIdentifierName();

            var body = new List<StatementSyntax>
            {
                // C#: int id = 0;
                LocalDeclarationStatement(
                    VariableDeclaration(
                        PredefinedType(Token(SyntaxKind.IntKeyword)),
                        SingletonSeparatedList(VariableDeclarator(idVar.Identifier)
                            .WithInitializer(EqualsValueClause(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))))))),

                // C#: Field header = default;
                LocalDeclarationStatement(
                    VariableDeclaration(
                        libraryTypes.Field.ToTypeSyntax(),
                        SingletonSeparatedList(VariableDeclarator(headerVar.Identifier)
                            .WithInitializer(EqualsValueClause(LiteralExpression(SyntaxKind.DefaultLiteralExpression))))))
            };

            if (type.HasComplexBaseType)
            {
                // C#: this.baseTypeSerializer.Deserialize(ref reader, instance);
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            ThisExpression().Member(BaseTypeSerializerFieldName.ToIdentifierName()).Member(DeserializeMethodName),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                Argument(instanceParam)
                            })))));
            }

            body.AddRange(AddSerializationCallbacks(type, instanceParam, "OnDeserializing"));

            if (type.SupportsPrimaryContstructorParameters)
            {
                body.Add(WhileStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression), Block(GetDeserializerLoopBody(members.Where(m => m.IsPrimaryConstructorParameter)))));
                body.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(idVar.Identifier), LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)))));

                body.Add(
                    IfStatement(
                        IdentifierName(headerVar.Identifier).Member("IsEndBaseFields"),
                        Block(WhileStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression), Block(GetDeserializerLoopBody(members.Where(m => !m.IsPrimaryConstructorParameter)))))));
            }
            else
            {
                body.Add(WhileStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression), Block(GetDeserializerLoopBody(members.Where(m => !m.IsPrimaryConstructorParameter)))));
            }

            body.AddRange(AddSerializationCallbacks(type, instanceParam, "OnDeserialized"));

            var genericParam = ParseTypeName("TReaderInput");
            var parameters = new[]
            {
                Parameter(readerParam.Identifier).WithType(libraryTypes.Reader.ToTypeSyntax(genericParam)).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter(instanceParam.Identifier).WithType(type.TypeSyntax)
            };

            if (type.IsValueType)
            {
                parameters[1] = parameters[1].WithModifiers(TokenList(Token(SyntaxKind.RefKeyword)));
            }

            return MethodDeclaration(returnType, DeserializeMethodName)
                .AddTypeParameterListParameters(TypeParameter("TReaderInput"))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());

            // Create the loop body.
            List<StatementSyntax> GetDeserializerLoopBody(IEnumerable<ISerializableMember> members)
            {
                var loopBody = new List<StatementSyntax>();
                var codecs = serializerFields.OfType<ICodecDescription>()
                        .Concat(libraryTypes.StaticCodecs)
                        .ToList();

                var orderedMembers = members.OrderBy(m => m.Member.FieldId).ToList();
                var lastMember = orderedMembers.LastOrDefault();

                // C#: id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
                {
                    var readHeaderMethodName = orderedMembers.Count == 0 ? "ReadHeaderExpectingEndBaseOrEndObject" : "ReadHeader";
                    var readFieldHeader =
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName(idVar.Identifier),
                                InvocationExpression(
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), IdentifierName(readHeaderMethodName)),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(readerParam).WithRefKindKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(headerVar).WithRefKindKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(idVar)
                                    })))));
                    loopBody.Add(readFieldHeader);
                }

                foreach (var member in orderedMembers)
                {
                    var description = member.Member;

                    // C#: instance.<member> = <codec>.ReadValue(ref reader, header);
                    // Codecs can either be static classes or injected into the constructor.
                    // Either way, the member signatures are the same.
                    var codec = codecs.First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, description.Type));
                    var memberType = description.Type;
                    var staticCodec = libraryTypes.StaticCodecs.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, memberType));
                    ExpressionSyntax codecExpression;
                    if (staticCodec != null)
                    {
                        codecExpression = staticCodec.CodecType.ToNameSyntax();
                    }
                    else
                    {
                        var instanceCodec = serializerFields.OfType<CodecFieldDescription>().First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
                        codecExpression = ThisExpression().Member(instanceCodec.FieldName);
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

                    var readHeaderMethodName = ReferenceEquals(member, lastMember) ? "ReadHeaderExpectingEndBaseOrEndObject" : "ReadHeader";
                    var readFieldHeader =
                        ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                IdentifierName(idVar.Identifier),
                                InvocationExpression(
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), IdentifierName(readHeaderMethodName)),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(readerParam).WithRefKindKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(headerVar).WithRefKindKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(idVar)
                                    })))));

                    var ifBody = Block(List(new StatementSyntax[] { memberAssignment, readFieldHeader }));

                    // C#: if (id == <fieldId>) { ... }
                    var ifStatement = IfStatement(BinaryExpression(SyntaxKind.EqualsExpression, IdentifierName(idVar.Identifier), LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal((int)description.FieldId))),
                        ifBody);

                    loopBody.Add(ifStatement);
                }

                // C#: if (id == -1) { break; }
                loopBody.Add(IfStatement(BinaryExpression(SyntaxKind.EqualsExpression, IdentifierName(idVar.Identifier), LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(-1))),
                    Block(List(new StatementSyntax[] { BreakStatement() }))));

                // Consume any unknown fields
                // C#: reader.ConsumeUnknownField(header);
                var consumeUnknown = ExpressionStatement(InvocationExpression(readerParam.Member("ConsumeUnknownField"),
                    ArgumentList(SeparatedList(new[] { Argument(headerVar) }))));
                loopBody.Add(consumeUnknown);

                return loopBody;
            }
        }

        private static IEnumerable<StatementSyntax> AddSerializationCallbacks(ISerializableTypeDescription type, IdentifierNameSyntax instanceParam, string callbackMethodName)
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

                yield return ExpressionStatement(InvocationExpression(
                    ThisExpression().Member($"_hook{hookIndex}").Member(callbackMethodName),
                    ArgumentList(SeparatedList(new[] { argument }))));
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
            var valueTypeField = "valueType".ToIdentifierName();

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
                    // C#: if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value)) { return; }
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
                            Block(ReturnStatement()))
                    );
                }
                else
                {
                    // C#: if (value is null) { ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta, expectedType); return; }
                    innerBody.Add(
                        IfStatement(
                            IsPatternExpression(valueParam, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
                            Block(
                                ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("WriteNullReference"),
                                    ArgumentList(SeparatedList(new[]
                                    {
                                        Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                        Argument(fieldIdDeltaParam),
                                        Argument(expectedTypeParam)
                                    })))),
                                ReturnStatement()))
                    );
                    
                    // C#: ReferenceCodec.MarkValueField(reader.Session);
                    innerBody.Add(ExpressionStatement(InvocationExpression(IdentifierName("ReferenceCodec").Member("MarkValueField"), ArgumentList(SingletonSeparatedList(Argument(writerParam.Member("Session")))))));
                }
            }

            // Generate the most appropriate expression to get the field type.
            ExpressionSyntax valueTypeInitializer = type.IsValueType switch {
                true => IdentifierName(CodecFieldTypeFieldName),
                false => ConditionalAccessExpression(valueParam, InvocationExpression(MemberBindingExpression(IdentifierName("GetType"))))
            };

            ExpressionSyntax valueTypeExpression = type.IsSealedType switch
            {
                true => IdentifierName(CodecFieldTypeFieldName),
                false => valueTypeField
            };

            // C#: writer.WriteStartObject(fieldIdDelta, expectedType, fieldType);
            innerBody.Add(
                ExpressionStatement(InvocationExpression(writerParam.Member("WriteStartObject"),
                ArgumentList(SeparatedList(new[]{
                            Argument(fieldIdDeltaParam),
                            Argument(expectedTypeParam),
                            Argument(valueTypeExpression)
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
                        ThisExpression().Member(SerializeMethodName),
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
                body = new List<StatementSyntax>
                {
                    // C#: var fieldType = value?.GetType();
                    LocalDeclarationStatement(
                        VariableDeclaration(
                            libraryTypes.Type.ToTypeSyntax(),
                            SingletonSeparatedList(VariableDeclarator(valueTypeField.Identifier)
                                .WithInitializer(EqualsValueClause(valueTypeInitializer))))),
                        
                    // C#: if (fieldType is null || fieldType == typeof(TField)) { <inner body> }
                    // C#: else { OrleansGeneratedCodeHelper.SerializeUnexpectedType(ref writer, fieldIdDelta, expectedType, value); }
                    IfStatement(
                        BinaryExpression(SyntaxKind.LogicalOrExpression,
                        IsPatternExpression(valueTypeField, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
                        BinaryExpression(SyntaxKind.EqualsExpression, valueTypeField, IdentifierName(CodecFieldTypeFieldName))),
                        Block(innerBody),
                        ElseClause(Block(new StatementSyntax[]
                        {
                            ExpressionStatement(
                                InvocationExpression(
                                    IdentifierName("OrleansGeneratedCodeHelper").Member("SerializeUnexpectedType"),
                                    ArgumentList(
                                        SeparatedList(new [] {
                                            Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                            Argument(fieldIdDeltaParam),
                                            Argument(expectedTypeParam),
                                            Argument(valueParam)
                                        }))))
                        })))
                };
            }

            var parameters = new[]
            {
                Parameter("writer".ToIdentifier()).WithType(libraryTypes.Writer.ToTypeSyntax()).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("fieldIdDelta".ToIdentifier()).WithType(libraryTypes.UInt32.ToTypeSyntax()),
                Parameter("expectedType".ToIdentifier()).WithType(libraryTypes.Type.ToTypeSyntax()),
                Parameter("value".ToIdentifier()).WithType(type.TypeSyntax)
            };

            return MethodDeclaration(returnType, WriteFieldMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
                .AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter").AddConstraints(TypeConstraint(libraryTypes.IBufferWriter.Construct(libraryTypes.Byte).ToTypeSyntax())))
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
            var innerBody = new List<StatementSyntax>();

            if (!type.IsValueType)
            {
                // C#: if (field.WireType == WireType.Reference) { return ReferenceCodec.ReadReference<TField, TReaderInput>(ref reader, field); }
                body.Add(
                    IfStatement(
                        BinaryExpression(SyntaxKind.EqualsExpression, fieldParam.Member("WireType"), libraryTypes.WireType.ToTypeSyntax().Member("Reference")),
                        Block(ReturnStatement(InvocationExpression(
                            IdentifierName("ReferenceCodec").Member("ReadReference", new[] { type.TypeSyntax, readerInputTypeParam }),
                            ArgumentList(SeparatedList(new[]
                            {
                                Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                Argument(fieldParam),
                            }))))))
                    );
            }

            ExpressionSyntax createValueExpression = type.UseActivator switch
            {
                true => InvocationExpression(serializerFields.OfType<ActivatorFieldDescription>().Single().FieldName.ToIdentifierName().Member("Create")),
                false => type.GetObjectCreationExpression(libraryTypes)
            };

            // C#: TField result = _activator.Create();
            // or C#: TField result = new TField();
            innerBody.Add(LocalDeclarationStatement(
                VariableDeclaration(
                    type.TypeSyntax,
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
                        ThisExpression().Member(DeserializeMethodName),
                        ArgumentList(
                            SeparatedList(
                                new[]
                                {
                                    Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                    resultArgument
                                })))));

            innerBody.Add(ReturnStatement(resultVar));

            if (type.IsSealedType)
            {
                body.AddRange(innerBody);
            }
            else
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
                                    IdentifierName("OrleansGeneratedCodeHelper").Member("DeserializeUnexpectedType", new[] { readerInputTypeParam, type.TypeSyntax }),
                                    ArgumentList(
                                        SeparatedList(new[] {
                                            Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                                            Argument(fieldParam)
                                        })))));
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
            var staticCodec = libraryTypes.StaticCodecs.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, type.BaseType));
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
                                    Argument(TypeOfExpression(type.TypeSyntax))
                                })))));

            var parameters = new[]
            {
                Parameter("writer".ToIdentifier()).WithType(libraryTypes.Writer.ToTypeSyntax()).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("fieldIdDelta".ToIdentifier()).WithType(libraryTypes.UInt32.ToTypeSyntax()),
                Parameter("expectedType".ToIdentifier()).WithType(libraryTypes.Type.ToTypeSyntax()),
                Parameter("value".ToIdentifier()).WithType(type.TypeSyntax)
            };

            return MethodDeclaration(returnType, WriteFieldMethodName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters)
                .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
                .AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter").AddConstraints(TypeConstraint(libraryTypes.IBufferWriter.Construct(libraryTypes.Byte).ToTypeSyntax())))
                .AddAttributeLists(AttributeList(SingletonSeparatedList(CodeGenerator.GetMethodImplAttributeSyntax())))
                .AddBodyStatements(body.ToArray());
        }

        private static MemberDeclarationSyntax GenerateEnumReadMethod(
            ISerializableTypeDescription type,
            LibraryTypes libraryTypes)
        {
            var readerParam = "reader".ToIdentifierName();
            var fieldParam = "field".ToIdentifierName();

            var staticCodec = libraryTypes.StaticCodecs.Find(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, type.BaseType));
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

            public TypeSyntax FieldType { get; }
            public string FieldName { get; }
            public abstract bool IsInjected { get; }
        }

        internal class BaseCodecFieldDescription : GeneratedFieldDescription
        {
            public BaseCodecFieldDescription(TypeSyntax fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
        }

        internal class ActivatorFieldDescription : GeneratedFieldDescription 
        {
            public ActivatorFieldDescription(TypeSyntax fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
        }

        internal class CodecFieldDescription : GeneratedFieldDescription, ICodecDescription
        {
            public CodecFieldDescription(TypeSyntax fieldType, string fieldName, ITypeSymbol underlyingType) : base(fieldType, fieldName)
            {
                UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => false;
        }

        internal class TypeFieldDescription : GeneratedFieldDescription
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

        internal class CodecFieldTypeFieldDescription : GeneratedFieldDescription
        {
            public CodecFieldTypeFieldDescription(TypeSyntax fieldType, string fieldName, TypeSyntax codecFieldType) : base(fieldType, fieldName)
            {
                CodecFieldType = codecFieldType;
            }

            public TypeSyntax CodecFieldType { get; }
            public override bool IsInjected => false;
        }

        internal class SetterFieldDescription : GeneratedFieldDescription
        {
            public SetterFieldDescription(TypeSyntax fieldType, string fieldName, StatementSyntax initializationSyntax) : base(fieldType, fieldName)
            {
                InitializationSyntax = initializationSyntax;
            }

            public override bool IsInjected => false;

            public StatementSyntax InitializationSyntax { get; }
        }

        internal class GetterFieldDescription : GeneratedFieldDescription
        {
            public GetterFieldDescription(TypeSyntax fieldType, string fieldName, StatementSyntax initializationSyntax) : base(fieldType, fieldName)
            {
                InitializationSyntax = initializationSyntax;
            }

            public override bool IsInjected => false;

            public StatementSyntax InitializationSyntax { get; }
        }

        internal class SerializationHookFieldDescription : GeneratedFieldDescription
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

            GetterFieldDescription GetGetterFieldDescription();
            SetterFieldDescription GetSetterFieldDescription();
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

            public GetterFieldDescription GetGetterFieldDescription() => null;
            public SetterFieldDescription GetSetterFieldDescription() => null;
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
            private string GetterFieldName => "getField" + _ordinal;

            /// <summary>
            /// Gets the name of the setter field.
            /// </summary>
            private string SetterFieldName => "setField" + _ordinal;

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
                    // Retrieve the field using the generated getter.
                    result =
                        InvocationExpression(IdentifierName(GetterFieldName))
                            .AddArgumentListArguments(Argument(instance));
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
                if (IsProperty)
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
                if (ContainingType != null && ContainingType.IsValueType)
                {
                    instanceArg = instanceArg.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                }

                return
                    InvocationExpression(IdentifierName(SetterFieldName))
                        .AddArgumentListArguments(instanceArg, Argument(value));
            }

            public GetterFieldDescription GetGetterFieldDescription()
            {
                if (IsGettableField || IsGettableProperty) return null;

                var getterType = _libraryTypes.Func_2.ToTypeSyntax(_member.GetTypeSyntax(ContainingType), TypeSyntax);

                // Generate syntax to initialize the field in the constructor
                var fieldAccessorUtility = AliasQualifiedName("global", IdentifierName("Orleans.Serialization")).Member("Utilities").Member("FieldAccessor");
                var fieldInfo = GetGetFieldInfoExpression(ContainingType, MemberName);
                var accessorInvoke = CastExpression(
                    getterType,
                    InvocationExpression(fieldAccessorUtility.Member("GetGetter")).AddArgumentListArguments(Argument(fieldInfo)));
                var initializationSyntax = ExpressionStatement(
                    AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(GetterFieldName), accessorInvoke));

                return new GetterFieldDescription(getterType, GetterFieldName, initializationSyntax);
            }

            public SetterFieldDescription GetSetterFieldDescription()
            {
                if (IsSettableField || IsProperty) return null;

                TypeSyntax fieldType;
                if (ContainingType != null && ContainingType.IsValueType)
                {
                    fieldType = _libraryTypes.ValueTypeSetter_2.ToTypeSyntax(_member.GetTypeSyntax(ContainingType), TypeSyntax);
                }
                else
                {
                    fieldType = _libraryTypes.Action_2.ToTypeSyntax(_member.GetTypeSyntax(ContainingType), TypeSyntax);
                }

                // Generate syntax to initialize the field in the constructor
                var fieldAccessorUtility = AliasQualifiedName("global", IdentifierName("Orleans.Serialization")).Member("Utilities").Member("FieldAccessor");
                var fieldInfo = GetGetFieldInfoExpression(ContainingType, MemberName);
                var isContainedByValueType = ContainingType != null && ContainingType.IsValueType;
                var accessorMethod = isContainedByValueType ? "GetValueSetter" : "GetReferenceSetter";
                var accessorInvoke = CastExpression(
                    fieldType,
                    InvocationExpression(fieldAccessorUtility.Member(accessorMethod))
                        .AddArgumentListArguments(Argument(fieldInfo)));

                var initializationSyntax = ExpressionStatement(
                    AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(SetterFieldName), accessorInvoke));

                return new SetterFieldDescription(fieldType, SetterFieldName, initializationSyntax);
            }

            public static InvocationExpressionSyntax GetGetFieldInfoExpression(INamedTypeSymbol containingType, string fieldName)
            {
                var bindingFlags = SymbolSyntaxExtensions.GetBindingFlagsParenthesizedExpressionSyntax(
                    SyntaxKind.BitwiseOrExpression,
                    BindingFlags.Instance,
                    BindingFlags.NonPublic,
                    BindingFlags.Public);
                return InvocationExpression(TypeOfExpression(containingType.ToTypeSyntax()).Member("GetField"))
                            .AddArgumentListArguments(
                                Argument(fieldName.GetLiteralExpression()),
                                Argument(bindingFlags));
            }
        }
    }
}
