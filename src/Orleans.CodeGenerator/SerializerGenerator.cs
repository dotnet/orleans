using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.InvokableGenerator;

namespace Orleans.CodeGenerator;

internal class SerializerGenerator(IGeneratorServices generatorServices)
{
    private const string BaseTypeSerializerFieldName = "_baseTypeSerializer";
    private const string ActivatorFieldName = "_activator";
    private const string CodecFieldTypeFieldName = "_codecFieldType";
    private const string CodecProviderFieldName = "_codecProvider";
    private const string ValueParameter = "@value";
    private readonly IGeneratorServices _generatorServices = generatorServices;

    private LibraryTypes LibraryTypes => _generatorServices.LibraryTypes;

    private bool HotReloadSafe => _generatorServices.Options.HotReloadSafe;
    private bool UseHotReloadShape(ISerializableTypeDescription type)
        => HotReloadSafe && !type.IsEnumType && type.SupportsHotReloadMemberAddition;

    /// <summary>Returns the codec declaration for <paramref name="type"/>, written without enclosing indentation.</summary>
    public string Generate(ISerializableTypeDescription type)
    {
        var writer = new SourceWriter();
        Write(writer, CreatePlan(type));
        return writer.ToString();
    }

    internal SerializerEmissionPlan CreatePlan(ISerializableTypeDescription type, SerializableTypeEmissionPlan? typePlan = null)
    {
        var members = new List<ISerializableMember>();
        foreach (var member in type.Members)
        {
            if (!member.IsSerializable)
            {
                continue;
            }

            if (member is ISerializableMember serializable)
            {
                members.Add(serializable);
            }
            else if (member is IFieldDescription or IPropertyDescription)
            {
                members.Add(new SerializableMember(_generatorServices, member));
            }
            else if (member is MethodParameterFieldDescription methodParameter)
            {
                members.Add(new SerializableMethodMember(methodParameter));
            }
        }

        var fieldDescriptions = GetFieldDescriptions(type, members);
        var construction = new GeneratedTypeConstruction(LibraryTypes, fieldDescriptions, HasBaseCodec(type));
        var hotReloadShape = UseHotReloadShape(type);
        var memberPlans = members.Select(member =>
        {
            var memberType = member.Member.Type;
            var staticCodec = LibraryTypes.StaticCodecs.FindByUnderlyingType(memberType);
            var codec = staticCodec is not null ? CodeText.Type(staticCodec.CodecType.ToNameSyntax())
                : GetCodecExpression(fieldDescriptions.OfType<CodecFieldDescription>()
                    .First(field => SymbolEqualityComparer.Default.Equals(field.UnderlyingType, memberType)));
            var expectedType = staticCodec is not null ? null : hotReloadShape
                ? $"typeof({CodeText.Type(member.Member.TypeSyntax)})"
                : CodeText.Identifier(fieldDescriptions.OfType<TypeFieldDescription>()
                    .First(field => SymbolEqualityComparer.Default.Equals(field.UnderlyingType, memberType)).FieldName);
            var getter = member.GetGetter();
            var setter = member.GetSetter();
            return new MemberEmissionPlan(member.Member.FieldId, member.IsPrimaryConstructorParameter,
                member.IsValueType, getter, setter, codec, expectedType, null);
        }).ToImmutableArray();

        var serializerInterface = type.IsValueType ? LibraryTypes.ValueSerializer : construction.HasBaseContract ? LibraryTypes.BaseCodec_1 : null;
        return new(typePlan ?? SerializableTypeEmissionPlan.Create(type, LibraryTypes),
            CodeText.Type((type.IsAbstractType ? LibraryTypes.AbstractTypeSerializer : LibraryTypes.FieldCodec_1).ToTypeSyntax(type.TypeSyntax)),
            serializerInterface is not null ? CodeText.Type(serializerInterface.ToTypeSyntax(type.TypeSyntax)) : null,
            ConstructionEmissionPlan.Create(construction),
            fieldDescriptions.Select(FieldEmissionPlan.Create).ToImmutableArray(), memberPlans,
            type.IsEnumType ? EnumSerializerGenerator.CreatePlan(type, LibraryTypes) : null,
            ReferenceVisibleSerializationContract.CreateAttribute(type, LibraryTypes));
    }

    internal static void Write(SourceWriter writer, SerializerEmissionPlan plan)
    {
        var type = plan.Type;
        var simpleClassName = GetSimpleClassName(type.Name);
        var members = plan.Members.ToList();

        var declaration = new StringBuilder();
        declaration.Append(type.IsPublic ? "public" : "internal").Append(" sealed class ").Append(simpleClassName);
        type.AppendTypeParameterList(declaration);
        declaration.Append(" : ").Append(plan.BaseType);
        if (plan.Enum is null && !type.IsAbstractType && plan.AdditionalInterface is { } additionalInterface)
        {
            declaration.Append(", ").Append(additionalInterface);
        }

        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        GenericParameterEmissionPlan.WriteDeclarationLine(writer, declaration, type.TypeParameters);
        writer.OpenBlock();
        WriteFieldDeclarations(writer, plan.Fields);
        WriteConstructor(writer, simpleClassName, plan.Fields, plan.Construction);

        if (plan.Enum is { } enumPlan)
        {
            EnumSerializerGenerator.WriteWriteMethod(writer, enumPlan);
            EnumSerializerGenerator.WriteReadMethod(writer, enumPlan);
        }
        else
        {
            WriteSerializeMethod(writer, type, members);
            WriteDeserializeMethod(writer, type, members);
            if (!type.IsAbstractType)
            {
                WriteCompoundTypeWriteFieldMethod(writer, type);
                WriteCompoundTypeReadValueMethod(writer, type, plan.Fields);
            }
        }

        writer.CloseBlock();
    }

    internal GeneratedTypeConstruction GetConstruction(ISerializableTypeDescription type)
        => new(LibraryTypes, GetFieldDescriptions(type, []), HasBaseCodec(type));

    private static bool HasBaseCodec(ISerializableTypeDescription type)
        => !type.IsEnumType && !type.IsAbstractType && !type.IsValueType && !type.IsSealedType;

    public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

    public static string GetSimpleClassName(string name) => $"Codec_{name}";

    public static string GetGeneratedNamespaceName(ITypeSymbol type) => type.GetNamespaceAndNesting() switch
    {
        { Length: > 0 } ns => $"{GeneratedCodeUtilities.CodeGeneratorName}.{ns}",
        _ => GeneratedCodeUtilities.CodeGeneratorName
    };

    internal static string GetServiceExpression(string serviceType, string codecProvider)
        => $"OrleansGeneratedCodeHelper.GetService<{serviceType}>(this, {codecProvider})";

    /// <summary>
    /// Returns the expression used to reference a codec or copier field, resolving the field on first use when it was
    /// added by hot reload and therefore skipped by the already-executed constructor.
    /// </summary>
    internal static string GetCodecExpression(GeneratedFieldDescription field)
        => field.LazyInitialization
            ? $"({field.FieldName} ??= {GetServiceExpression(CodeText.Type(field.FieldType), CodecProviderFieldName)})"
            : field.FieldName;

    internal static void WriteFieldDeclarations(SourceWriter writer, EquatableArray<FieldEmissionPlan> fields)
    {
        foreach (var field in fields)
        {
            switch (field)
            {
                case { Kind: EmissionFieldKind.Type }:
                    writer.WriteLine($"private readonly {field.Type} {field.FieldName} = {field.Initializer};");
                    break;
                case { Kind: EmissionFieldKind.Accessor, LazyInitialization: true, Initializer: not null }:
                    writer.WriteLine($"private static {field.Type} {field.FieldName};");
                    break;
                case { Kind: EmissionFieldKind.Accessor, Initializer: not null }:
                    writer.WriteLine($"private static readonly {field.Type} {field.FieldName} = {field.Initializer};");
                    break;
                case { Kind: EmissionFieldKind.Accessor }:
                    WriteUnsafeAccessorDeclaration(writer, field);
                    break;
                default:
                    writer.WriteLine(field.LazyInitialization
                        ? $"private {field.Type} {field.FieldName};"
                        : $"private readonly {field.Type} {field.FieldName};");
                    break;
            }
        }
    }

    internal static void WriteUnsafeAccessorDeclaration(SourceWriter writer, FieldEmissionPlan accessor)
    {
        Debug.Assert(accessor.Kind == EmissionFieldKind.Accessor);
        Debug.Assert(accessor.ContainingType is not null);
        Debug.Assert(accessor.AccessorName is not null);
        var kind = accessor.IsUnsafeFieldAccessor ? "Field" : "Method";
        var name = accessor.IsUnsafeFieldAccessor ? accessor.FieldName : $"set_{accessor.FieldName}";
        writer.WriteLine($"[global::System.Runtime.CompilerServices.UnsafeAccessorAttribute(global::System.Runtime.CompilerServices.UnsafeAccessorKind.{kind}, Name = {CodeText.QuotedText(name)})]");
        var instance = $"{(accessor.ContainingTypeIsValueType ? "ref " : string.Empty)}{accessor.ContainingType} instance";
        writer.WriteLine(accessor.IsUnsafeFieldAccessor
            ? $"private extern static ref {accessor.Type} {accessor.AccessorName}({instance});"
            : $"private extern static void {accessor.AccessorName}({instance}, {accessor.Type} value);");
    }

    private static void WriteConstructor(SourceWriter writer, string simpleClassName, EquatableArray<FieldEmissionPlan> fields, ConstructionEmissionPlan construction)
    {
        if (!fields.Any(HasConstructorStatement))
        {
            return;
        }

        WriteConstructorDeclaration(writer, simpleClassName, construction, baseInitializer: null);
        writer.OpenBlock();
        WriteConstructorStatements(writer, fields);
        writer.CloseBlock();
    }

    internal static bool HasConstructorStatement(FieldEmissionPlan field)
        => field.IsInjected || field.Kind is EmissionFieldKind.CodecProvider || field is { Kind: EmissionFieldKind.Service, IsInjected: false };

    internal static void WriteConstructorDeclaration(SourceWriter writer, string simpleClassName, ConstructionEmissionPlan construction, string? baseInitializer)
    {
        var declaration = new StringBuilder();
        declaration.Append("public ").Append(simpleClassName).Append('(');
        construction.AppendParameters(declaration);
        declaration.Append(')');
        if (baseInitializer is not null)
        {
            declaration.Append(" : ").Append(baseInitializer);
        }

        writer.WriteLine(declaration.ToString());
    }

    internal static void WriteConstructorStatements(SourceWriter writer, EquatableArray<FieldEmissionPlan> fields)
    {
        foreach (var field in fields)
        {
            var name = CodeText.Identifier(field.FieldName);
            switch (field)
            {
                case { IsInjected: true }:
                    writer.WriteLine($"this.{name} = OrleansGeneratedCodeHelper.UnwrapService(this, {name});");
                    break;
                case { Kind: EmissionFieldKind.CodecProvider }:
                    writer.WriteLine($"{name} = codecProvider;");
                    break;
                case { Kind: EmissionFieldKind.Service, IsInjected: false }:
                    writer.WriteLine($"{name} = {GetServiceExpression(field.Type, "codecProvider")};");
                    break;
            }
        }
    }

    private List<GeneratedFieldDescription> GetFieldDescriptions(
        ISerializableTypeDescription serializableTypeDescription,
        List<ISerializableMember> members)
    {
        var fields = new List<GeneratedFieldDescription>();

        if (!serializableTypeDescription.IsAbstractType)
        {
            fields.Add(new CodecFieldTypeFieldDescription(LibraryTypes.Type.ToTypeSyntax(), CodecFieldTypeFieldName, serializableTypeDescription.TypeSyntax));
        }

        var hotReloadShape = UseHotReloadShape(serializableTypeDescription);
        if (hotReloadShape)
        {
            fields.Add(new CodecProviderFieldDescription(LibraryTypes.ICodecProvider.ToTypeSyntax()));
        }

        if (serializableTypeDescription.HasComplexBaseType)
        {
            fields.Add(GetBaseTypeField(serializableTypeDescription));
        }

        if (serializableTypeDescription.UseActivator && !serializableTypeDescription.IsAbstractType)
        {
            fields.Add(new ActivatorFieldDescription(LibraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
        }

        var codecMembers = new List<IMemberDescription>();
        foreach (var member in serializableTypeDescription.Members.Distinct(MemberDescriptionTypeComparer.Default))
        {
            if (!member.IsSerializable)
            {
                continue;
            }

            // Add a codec field for any field in the target which does not have a static codec.
            if (LibraryTypes.StaticCodecs.FindByUnderlyingType(member.Type) is not null)
                continue;

            codecMembers.Add(member);
        }

        var typeFieldNames = GeneratedFieldNames.ForTypes("_type", codecMembers);
        var codecFieldNames = GeneratedFieldNames.ForTypes("_codec", codecMembers);
        for (var i = 0; i < codecMembers.Count; i++)
        {
            var member = codecMembers[i];
            if (!hotReloadShape)
            {
                fields.Add(new TypeFieldDescription(LibraryTypes.Type.ToTypeSyntax(), typeFieldNames[i], member.TypeSyntax, member.Type));
            }

            fields.Add(GetCodecDescription(member, codecFieldNames[i]));
        }

        foreach (var member in members)
        {
            AddFieldAccessorDescriptions(fields, member);
        }

        for (var hookIndex = 0; hookIndex < serializableTypeDescription.SerializationHooks.Count; ++hookIndex)
        {
            var hookType = serializableTypeDescription.SerializationHooks[hookIndex];
            fields.Add(new SerializationHookFieldDescription(hookType.ToTypeSyntax(), $"_hook{hookIndex}"));
        }

        return fields;

        CodecFieldDescription GetCodecDescription(IMemberDescription member, string fieldName)
        {
            var t = member.Type;
            TypeSyntax? codecType = null;
            if (t.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
                && (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, LibraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
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
                codecType = LibraryTypes.ArrayCodec.Construct(array.ElementType).ToTypeSyntax();
            }
            else if (LibraryTypes.WellKnownCodecs.FindByUnderlyingType(t) is { } codec)
            {
                // The codec is not a static codec and is also not a generic codec.
                codecType = codec.CodecType.ToTypeSyntax();
            }
            else if (t is INamedTypeSymbol { ConstructedFrom: { } unboundFieldType } named && LibraryTypes.WellKnownCodecs.FindByUnderlyingType(unboundFieldType) is { } genericCodec)
            {
                // Construct the generic codec type using the field's type arguments.
                codecType = genericCodec.CodecType.Construct([.. named.TypeArguments]).ToTypeSyntax();
            }
            else
            {
                // Use the IFieldCodec<TField> interface
                codecType = LibraryTypes.FieldCodec_1.ToTypeSyntax(member.TypeSyntax);
            }

            return new CodecFieldDescription(codecType, fieldName, t, hotReloadShape);
        }
    }

    internal static void AddFieldAccessorDescriptions(List<GeneratedFieldDescription> fields, ISerializableMember member)
    {
        var getter = member.GetGetterFieldDescription();
        if (getter is not null)
        {
            fields.Add(getter);
        }

        if (member.GetSetterFieldDescription() is { } setter
            && (!setter.IsUnsafeFieldAccessor || setter.AccessorName != getter?.AccessorName))
        {
            fields.Add(setter);
        }
    }

    private BaseCodecFieldDescription GetBaseTypeField(ISerializableTypeDescription serializableTypeDescription)
    {
        var baseType = serializableTypeDescription.BaseType;
        if (baseType.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
            && (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, LibraryTypes.Compilation.Assembly) || baseType.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
            && baseType is not INamedTypeSymbol { IsGenericType: true })
        {
            // Use the concrete generated type and avoid expensive interface dispatch (except for generic types that will fall back to IBaseCodec<T>)
            return new(QualifiedName(ParseName(GetGeneratedNamespaceName(baseType)), IdentifierName(GetSimpleClassName(baseType.Name))), true);
        }

        return new(LibraryTypes.BaseCodec_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax));
    }

    private static int CountCallbacks(SerializableTypeEmissionPlan type, string callbackMethodName)
        => type.SerializationHooks.Count(callback => callback.Method == callbackMethodName);

    private static void WriteSerializeMethod(SourceWriter writer, SerializableTypeEmissionPlan type, List<MemberEmissionPlan> members)
    {
        // An abstract base serializer contributes no member when it has nothing to write.
        if (type.IsAbstractType && !type.HasComplexBaseType && !type.IncludePrimaryConstructorParameters && members.Count == 0
            && CountCallbacks(type, "OnSerializing") == 0 && CountCallbacks(type, "OnSerialized") == 0)
        {
            return;
        }

        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        var instanceModifiers = type.IsValueType ? type.Library.HasScopedKeyword ? "scoped ref " : "ref " : string.Empty;
        writer.WriteLine($"public {(type.IsAbstractType ? "override " : string.Empty)}void Serialize<TBufferWriter>(ref {type.Library.Writer} writer, {instanceModifiers}{type.Type} instance)");
        if (!type.IsAbstractType)
        {
            WriteBufferWriterConstraint(writer, type.Library);
        }

        writer.OpenBlock();
        if (type.HasComplexBaseType)
        {
            writer.WriteLine($"{BaseTypeSerializerFieldName}.Serialize(ref writer, instance);");
            writer.WriteLine("writer.WriteEndBase();");
        }

        WriteSerializationCallbacks(writer, type, "instance", "OnSerializing");

        // Order members according to their FieldId, since fields must be serialized in order and FieldIds are serialized as deltas.
        if (type.OmitDefaultMemberValues && members.Count > 0)
        {
            writer.WriteLine("uint previousFieldId = 0U;");
        }

        if (type.IncludePrimaryConstructorParameters)
        {
            WriteSerializationMembers(writer, type, members.Where(static m => m.IsPrimaryConstructorParameter));
            writer.WriteLine("writer.WriteEndBase();");
        }

        WriteSerializationMembers(writer, type, members.Where(static m => !m.IsPrimaryConstructorParameter));
        WriteSerializationCallbacks(writer, type, "instance", "OnSerialized");
        writer.CloseBlock();
    }

    private static void WriteBufferWriterConstraint(SourceWriter writer, EmissionLibraryTypes library)
    {
        writer.Indent();
        writer.WriteLine($"where TBufferWriter : {library.BufferWriter}");
        writer.Unindent();
    }

    private static void WriteSerializationMembers(SourceWriter writer, SerializableTypeEmissionPlan type, IEnumerable<MemberEmissionPlan> members)
    {
        uint previousFieldId = 0;
        foreach (var member in members.OrderBy(static m => m.FieldId))
        {
            string fieldIdDelta;
            if (type.OmitDefaultMemberValues)
            {
                fieldIdDelta = $"{CodeText.Literal(member.FieldId)} - previousFieldId";
            }
            else
            {
                fieldIdDelta = CodeText.Literal(member.FieldId - previousFieldId);
                previousFieldId = member.FieldId;
            }

            // Codecs can either be static classes or injected into the constructor.
            // Either way, the member signatures are the same.
            // When a static codec is available, the expected type is known to be the static codec's field type.
            Debug.Assert(member.Codec is not null);
            var getter = member.GetGetter("instance");
            var writeField = member.ExpectedType is { } expectedType
                ? $"{member.Codec}.WriteField(ref writer, {fieldIdDelta}, {expectedType}, {getter});"
                : $"{member.Codec}.WriteField(ref writer, {fieldIdDelta}, {getter});";

            if (!type.OmitDefaultMemberValues)
            {
                writer.WriteLine(writeField);
            }
            else
            {
                writer.WriteLine(member.IsValueType ? $"if ({getter} != default)" : $"if ({getter} is object)");
                writer.OpenBlock();
                writer.WriteLine(writeField);
                writer.WriteLine($"previousFieldId = {CodeText.Literal(member.FieldId)};");
                writer.CloseBlock();
            }
        }
    }

    private static void WriteDeserializeMethod(SourceWriter writer, SerializableTypeEmissionPlan type, List<MemberEmissionPlan> members)
    {
        var nonCtorMembers = type.IncludePrimaryConstructorParameters ? members.FindAll(static m => !m.IsPrimaryConstructorParameter) : members;
        var consumesEmptyObject = (members.Count == 0 || nonCtorMembers.Count == 0) && !type.IncludePrimaryConstructorParameters;

        // An abstract base serializer contributes no member when it only consumes the end of the object.
        if (type.IsAbstractType && consumesEmptyObject && !type.HasComplexBaseType
            && CountCallbacks(type, "OnDeserializing") == 0 && CountCallbacks(type, "OnDeserialized") == 0)
        {
            return;
        }

        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        var instanceModifiers = type.IsValueType ? type.Library.HasScopedKeyword ? "scoped ref " : "ref " : string.Empty;
        writer.WriteLine($"public {(type.IsAbstractType ? "override " : string.Empty)}void Deserialize<TReaderInput>(ref {type.Library.Reader} reader, {instanceModifiers}{type.Type} instance)");
        writer.OpenBlock();
        if (type.HasComplexBaseType)
        {
            writer.WriteLine($"{BaseTypeSerializerFieldName}.Deserialize(ref reader, instance);");
        }

        WriteSerializationCallbacks(writer, type, "instance", "OnDeserializing");
        if (consumesEmptyObject)
        {
            writer.WriteLine("reader.ConsumeEndBaseOrEndObject();");
        }
        else
        {
            if (members.Count > 0)
            {
                writer.WriteLine("uint id = 0U;");
            }

            writer.WriteLine($"{type.Library.Field} header = default;");
            if (type.IncludePrimaryConstructorParameters)
            {
                WriteDeserializerLoop(writer, members.FindAll(static m => m.IsPrimaryConstructorParameter));
                if (members.Count > 0)
                {
                    writer.WriteLine("id = 0U;");
                }

                writer.WriteLine("if (header.IsEndBaseFields)");
                if (nonCtorMembers.Count == 0)
                {
                    WriteDeserializerLoop(writer, nonCtorMembers);
                }
                else
                {
                    writer.Indent();
                    WriteDeserializerLoop(writer, nonCtorMembers);
                    writer.Unindent();
                }
            }
            else
            {
                WriteDeserializerLoop(writer, nonCtorMembers);
            }
        }

        WriteSerializationCallbacks(writer, type, "instance", "OnDeserialized");
        writer.CloseBlock();

        // Writes the field loop. An empty member set consumes the remaining fields in a block.
        static void WriteDeserializerLoop(SourceWriter writer, List<MemberEmissionPlan> members)
        {
            if (members.Count == 0)
            {
                writer.OpenBlock();
                writer.WriteLine("reader.ReadFieldHeader(ref header);");
                writer.WriteLine("reader.ConsumeEndBaseOrEndObject(ref header);");
                writer.CloseBlock();
                return;
            }

            writer.WriteLine("while (true)");
            writer.OpenBlock();
            WriteReadFieldHeader(writer, "id += header.FieldIdDelta;");

            members.Sort(static (x, y) => x.FieldId.CompareTo(y.FieldId));
            var contiguousIds = members[members.Count - 1].FieldId == members.Count - 1;
            foreach (var member in members)
            {
                // Codecs can either be static classes or injected into the constructor.
                // Either way, the member signatures are the same.
                Debug.Assert(member.Codec is not null);
                writer.WriteLine($"if (id == {CodeText.Literal(member.FieldId)})");
                writer.OpenBlock();
                writer.WriteLine(member.GetSetter("instance", $"{member.Codec}.ReadValue(ref reader, header)") + ";");
                if (member != members[members.Count - 1])
                {
                    WriteReadFieldHeader(writer, "id += header.FieldIdDelta;");
                }
                else if (contiguousIds)
                {
                    writer.WriteLine("reader.ReadFieldHeader(ref header);");
                }
                else
                {
                    WriteReadFieldHeader(writer, "id++;");
                }

                writer.CloseBlock();
            }

            // Consume any unknown fields
            if (contiguousIds)
            {
                writer.WriteLine("reader.ConsumeEndBaseOrEndObject(ref header);");
                writer.WriteLine("break;");
            }
            else
            {
                writer.WriteLine("reader.ConsumeUnknownField(ref header);");
            }

            writer.CloseBlock();
        }

        static void WriteReadFieldHeader(SourceWriter writer, string idUpdate)
        {
            writer.WriteLine("reader.ReadFieldHeader(ref header);");
            writer.WriteLine("if (header.IsEndBaseOrEndObject)");
            writer.Indent();
            writer.WriteLine("break;");
            writer.Unindent();
            writer.WriteLine(idUpdate);
        }
    }

    private static void WriteSerializationCallbacks(SourceWriter writer, SerializableTypeEmissionPlan type, string instance, string callbackMethodName)
    {
        foreach (var callback in type.SerializationHooks)
        {
            if (callback.Method != callbackMethodName)
            {
                continue;
            }

            writer.WriteLine($"_hook{callback.HookIndex}.{callbackMethodName}({(callback.FirstByRef ? "ref " : string.Empty)}{instance});");
        }
    }

    private static void WriteCompoundTypeWriteFieldMethod(SourceWriter writer, SerializableTypeEmissionPlan type)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public void WriteField<TBufferWriter>(ref {type.Library.Writer} writer, uint fieldIdDelta, {type.Library.SystemType} expectedType, {type.Type} {ValueParameter})");
        WriteBufferWriterConstraint(writer, type.Library);
        writer.OpenBlock();
        if (type.IsSealedType)
        {
            WriteInnerBody(writer, type);
        }
        else
        {
            // For types which are not sealed/value types, add some extra logic to support sub-types.
            writer.WriteLine($"if ({ValueParameter} is null || {ValueParameter}.GetType() == typeof({type.Type}))");
            writer.OpenBlock();
            WriteInnerBody(writer, type);
            writer.CloseBlock();
            writer.ContinueStatement();
            writer.WriteLine("else");
            writer.Indent();
            writer.WriteLine($"writer.SerializeUnexpectedType(fieldIdDelta, expectedType, {ValueParameter});");
            writer.Unindent();
        }

        writer.CloseBlock();

        static void WriteInnerBody(SourceWriter writer, SerializableTypeEmissionPlan type)
        {
            if (type.IsValueType)
            {
                writer.WriteLine("ReferenceCodec.MarkValueField(writer.Session);");
            }
            else if (type.TrackReferences)
            {
                writer.WriteLine($"if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, {ValueParameter}))");
                writer.Indent();
                writer.WriteLine("return;");
                writer.Unindent();
            }
            else
            {
                writer.WriteLine($"if ({ValueParameter} is null)");
                writer.OpenBlock();
                writer.WriteLine("ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);");
                writer.WriteLine("return;");
                writer.CloseBlock();
                writer.WriteLine("ReferenceCodec.MarkValueField(writer.Session);");
            }

            writer.WriteLine($"writer.WriteStartObject(fieldIdDelta, expectedType, {CodecFieldTypeFieldName});");
            writer.WriteLine($"Serialize(ref writer, {(type.IsValueType ? "ref " : string.Empty)}{ValueParameter});");
            writer.WriteLine("writer.WriteEndObject();");
        }
    }

    private static void WriteCompoundTypeReadValueMethod(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<FieldEmissionPlan> serializerFields)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {type.Type} ReadValue<TReaderInput>(ref {type.Library.Reader} reader, {type.Library.Field} field)");
        writer.OpenBlock();
        if (!type.IsValueType)
        {
            writer.WriteLine("if (field.IsReference)");
            writer.Indent();
            writer.WriteLine($"return ReferenceCodec.ReadReference<{type.Type}, TReaderInput>(ref reader, field);");
            writer.Unindent();
        }

        writer.WriteLine("field.EnsureWireTypeTagDelimited();");
        if (type.IsSealedType)
        {
            WriteInnerBody(writer, type, serializerFields);
        }
        else
        {
            writer.WriteLine($"{type.Library.SystemType} valueType = field.FieldType;");
            writer.WriteLine($"if (valueType is null || valueType == {CodecFieldTypeFieldName})");
            writer.OpenBlock();
            WriteInnerBody(writer, type, serializerFields);
            writer.CloseBlock();
            writer.WriteLine($"return reader.DeserializeUnexpectedType<TReaderInput, {type.Type}>(ref field);");
        }

        writer.CloseBlock();

        static void WriteInnerBody(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<FieldEmissionPlan> serializerFields)
        {
            var createValue = type.UseActivator
                ? $"{serializerFields.Single(static field => field.Kind == EmissionFieldKind.Service && field.FieldName == ActivatorFieldName).FieldName}.Create()"
                : type.ObjectCreation;
            writer.WriteLine($"var result = {createValue};");
            writer.WriteLine(type.TrackReferences
                ? "ReferenceCodec.RecordObject(reader.Session, result);"
                : "ReferenceCodec.MarkValueField(reader.Session);");
            writer.WriteLine($"Deserialize(ref reader, {(type.IsValueType ? "ref " : string.Empty)}result);");
            writer.WriteLine("return result;");
        }
    }

    internal abstract class GeneratedFieldDescription(TypeSyntax fieldType, string fieldName)
    {
        public readonly TypeSyntax FieldType = fieldType;
        public readonly string FieldName = fieldName;
        public abstract bool IsInjected { get; }
        public virtual bool LazyInitialization => false;
    }

    internal sealed class CodecProviderFieldDescription(TypeSyntax fieldType) : GeneratedFieldDescription(fieldType, CodecProviderFieldName)
    {
        public override bool IsInjected => false;
    }

    internal sealed class BaseCodecFieldDescription(TypeSyntax fieldType, bool concreteType = false) : GeneratedFieldDescription(fieldType, BaseTypeSerializerFieldName)
    {
        public override bool IsInjected { get; } = !concreteType;
    }

    internal sealed class ActivatorFieldDescription(TypeSyntax fieldType, string fieldName) : GeneratedFieldDescription(fieldType, fieldName)
    {
        public override bool IsInjected => true;
    }

    internal sealed class CodecFieldDescription(TypeSyntax fieldType, string fieldName, ITypeSymbol underlyingType, bool lazyInitialization = false) : GeneratedFieldDescription(fieldType, fieldName)
    {
        public ITypeSymbol UnderlyingType { get; } = underlyingType;
        public override bool IsInjected => false;
        public override bool LazyInitialization { get; } = lazyInitialization;
    }

    internal sealed class TypeFieldDescription(TypeSyntax fieldType, string fieldName, TypeSyntax underlyingTypeSyntax, ITypeSymbol underlyingType) : GeneratedFieldDescription(fieldType, fieldName)
    {
        public TypeSyntax UnderlyingTypeSyntax { get; } = underlyingTypeSyntax;
        public ITypeSymbol UnderlyingType { get; } = underlyingType;
        public override bool IsInjected => false;
    }

    internal sealed class CodecFieldTypeFieldDescription(TypeSyntax fieldType, string fieldName, TypeSyntax codecFieldType) : GeneratedFieldDescription(fieldType, fieldName)
    {
        public TypeSyntax CodecFieldType { get; } = codecFieldType;
        public override bool IsInjected => false;
    }

    /// <summary>
    /// Describes a generated accessor for an inaccessible member: a delegate field (initialized from <see cref="Initialization"/>)
    /// or, without an initializer, an <c>UnsafeAccessor</c> method.
    /// </summary>
    internal sealed class FieldAccessorDescription(TypeSyntax containingType, TypeSyntax fieldType, string fieldName, string accessorName, string? initialization = null, bool lazyInitialization = false, bool isUnsafeFieldAccessor = false, bool containingTypeIsValueType = false) : GeneratedFieldDescription(fieldType, fieldName)
    {
        public override bool IsInjected => false;
        public override bool LazyInitialization { get; } = lazyInitialization;
        public readonly string AccessorName = accessorName;
        public readonly TypeSyntax ContainingType = containingType;
        public readonly string? Initialization = initialization;
        public bool IsUnsafeFieldAccessor { get; } = isUnsafeFieldAccessor;
        public bool ContainingTypeIsValueType { get; } = containingTypeIsValueType;

        /// <summary>The expression which invokes this accessor, resolving a lazily initialized accessor on first use.</summary>
        public string AccessExpression => LazyInitialization && Initialization is { } initialization
            ? $"({AccessorName} ??= {initialization})"
            : AccessorName;
    }

    internal sealed class SerializationHookFieldDescription(TypeSyntax fieldType, string fieldName) : GeneratedFieldDescription(fieldType, fieldName)
    {
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
        /// Returns the plan for reading this member's value from an instance of the containing type.
        /// </summary>
        MemberAccessPlan GetGetter();

        /// <summary>
        /// Returns the plan for assigning this member's value on an instance of the containing type.
        /// </summary>
        MemberAccessPlan GetSetter();

        FieldAccessorDescription? GetGetterFieldDescription();
        FieldAccessorDescription? GetSetterFieldDescription();
    }

    /// <summary>
    /// Represents a serializable member (field/property) of a type.
    /// </summary>
    internal class SerializableMethodMember(InvokableGenerator.MethodParameterFieldDescription member) : ISerializableMember
    {
        IMemberDescription ISerializableMember.Member => Member;
        public MethodParameterFieldDescription Member { get; } = member;

        private LibraryTypes LibraryTypes => Member.LibraryTypes;

        public bool IsShallowCopyable => LibraryTypes.IsShallowCopyable(Member.Parameter.Type) || Member.Parameter.HasAttribute(LibraryTypes.ImmutableAttribute);

        /// <summary>
        /// Gets syntax representing the type of this field.
        /// </summary>
        public TypeSyntax TypeSyntax => Member.TypeSyntax;

        public bool IsValueType => Member.Type.IsValueType;

        public bool IsPrimaryConstructorParameter => Member.IsPrimaryConstructorParameter;

        public MemberAccessPlan GetGetter() => MemberAccessPlan.ForMember(Member.FieldName);

        public MemberAccessPlan GetSetter() => MemberAccessPlan.ForMember(Member.FieldName);

        public FieldAccessorDescription? GetGetterFieldDescription() => null;
        public FieldAccessorDescription? GetSetterFieldDescription() => null;
    }

    /// <summary>
    /// Represents a serializable member (field/property) of a type.
    /// </summary>
    internal class SerializableMember(IGeneratorServices generatorServices, IMemberDescription member) : ISerializableMember
    {
        private readonly IGeneratorServices _generatorServices = generatorServices;

        private Compilation Compilation => _generatorServices.Compilation;
        private LibraryTypes LibraryTypes => _generatorServices.LibraryTypes;

        public bool IsShallowCopyable =>
            LibraryTypes.IsShallowCopyable(Member.Type)
            || Property is { } prop && prop.HasAttribute(LibraryTypes.ImmutableAttribute)
            || Member.Symbol.HasAttribute(LibraryTypes.ImmutableAttribute);

        public bool IsValueType => Type.IsValueType;

        public IMemberDescription Member { get; } = member;

        /// <summary>
        /// Gets the underlying <see cref="Field"/> instance.
        /// </summary>
        private IFieldSymbol? Field => (Member as IFieldDescription)?.Field;

        public ITypeSymbol Type => Member.Type;

        public INamedTypeSymbol ContainingType => Member.ContainingType;

        public string MemberName => Field?.Name ?? Property?.Name ?? Member.Symbol.Name;

        /// <summary>
        /// Gets the name of the accessor used to read the field.
        /// </summary>
        private string GetterFieldName => GeneratedFieldNames.Accessor(UseUnsafeFieldAccessor ? "accessField" : "getField", Member);

        /// <summary>
        /// Gets the name of the accessor used to write the field.
        /// </summary>
        private string SetterFieldName => GeneratedFieldNames.Accessor(UseUnsafeFieldAccessor ? "accessField" : "setField", Member);

        /// <summary>
        /// Gets a value indicating if the member is a property.
        /// </summary>
        private bool IsProperty => Member.Symbol is IPropertySymbol;

        /// <summary>
        /// Gets a value indicating whether or not this member represents an accessible field.
        /// </summary>
        private bool IsGettableField => Field is { } fieldInfo && _generatorServices.Compilation.IsSymbolAccessibleWithin(fieldInfo, Compilation.Assembly) && !IsObsolete;

        /// <summary>
        /// Gets a value indicating whether or not this member represents an accessible, mutable field.
        /// </summary>
        private bool IsSettableField => Field is { } fieldInfo && IsGettableField && !fieldInfo.IsReadOnly;

        /// <summary>
        /// Gets a value indicating whether or not this member represents a property with an accessible, non-obsolete getter.
        /// </summary>
        private bool IsGettableProperty => Property?.GetMethod is { } getMethod && Compilation.IsSymbolAccessibleWithin(getMethod, Compilation.Assembly) && !IsObsolete;

        /// <summary>
        /// Gets a value indicating whether or not this member represents a property with an accessible, non-obsolete setter.
        /// </summary>
        private bool IsSettableProperty => Property?.SetMethod is { } setMethod && Compilation.IsSymbolAccessibleWithin(setMethod, Compilation.Assembly) && !setMethod.IsInitOnly && !IsObsolete;

        /// <summary>
        /// Gets syntax representing the type of this field.
        /// </summary>
        public TypeSyntax TypeSyntax => Member.Type.TypeKind == TypeKind.Dynamic
            ? PredefinedType(Token(SyntaxKind.ObjectKeyword))
            : Member.GetTypeSyntax(Member.Type);

        /// <summary>
        /// Gets the <see cref="Property"/> which this field is the backing property for, or
        /// <see langword="null" /> if this is not the backing field of an auto-property.
        /// </summary>
        private IPropertySymbol? Property => field ??= field = Member.Symbol as IPropertySymbol ?? (Field is { } fieldSymbol ? PropertyUtility.GetMatchingProperty(fieldSymbol) : null);

        /// <summary>
        /// Gets a value indicating whether or not this field is obsolete.
        /// </summary>
        private bool IsObsolete => Member.Symbol.HasAttribute(LibraryTypes.ObsoleteAttribute) ||
                                   Property != null && Property.HasAttribute(LibraryTypes.ObsoleteAttribute);

        public bool IsPrimaryConstructorParameter => Member.IsPrimaryConstructorParameter;

        public MemberAccessPlan GetGetter()
        {
            // If the field is the backing field for an accessible auto-property use the property directly.
            if (Property is { } property && IsGettableProperty)
            {
                return MemberAccessPlan.ForMember(property.Name);
            }

            if (Field is { } field && IsGettableField)
            {
                return MemberAccessPlan.ForMember(field.Name);
            }

            // Retrieve the field using the generated getter.
            return new(MemberAccessKind.Invoke, GetGetterFieldDescription()!.AccessExpression, RefInstance: ContainingType?.IsValueType == true);
        }

        public MemberAccessPlan GetSetter()
        {
            // If the field is the backing field for an accessible auto-property use the property directly.
            if (Property is { } property && IsSettableProperty)
            {
                return MemberAccessPlan.ForMember(property.Name);
            }

            if (Field is { } field && IsSettableField)
            {
                return MemberAccessPlan.ForMember(field.Name);
            }

            // If the symbol itself is a property but is not settable, then error out, since we do not know how to set it value
            if (IsProperty && !IsPrimaryConstructorParameter)
            {
                Location? location = default;
                if (Member.Symbol is IPropertySymbol prop && prop.SetMethod is { } setMethod)
                {
                    location = setMethod.Locations.FirstOrDefault();
                }

                location ??= Member.Symbol.Locations.FirstOrDefault();

                if (!SymbolEqualityComparer.Default.Equals(ContainingType.ContainingAssembly, Compilation.Assembly))
                {
                    location = Location.None;
                }

                throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSetterDiagnostic.CreateDiagnostic(location, Member.Symbol?.ToDisplayString() ?? $"{ContainingType.ToDisplayString()}.{MemberName}"));
            }

            var accessor = GetSetterFieldDescription()!;
            return new(accessor.IsUnsafeFieldAccessor ? MemberAccessKind.FieldReference : MemberAccessKind.Invoke,
                accessor.AccessExpression, RefInstance: ContainingType?.IsValueType == true);
        }

        public FieldAccessorDescription? GetGetterFieldDescription()
        {
            if (IsGettableField || IsGettableProperty) return null;
            return GetFieldAccessor(ContainingType, TypeSyntax, MemberName, GetterFieldName, LibraryTypes, false,
                IsPrimaryConstructorParameter && IsProperty, _generatorServices.Options.HotReloadSafe, UseUnsafeFieldAccessor);
        }

        public FieldAccessorDescription? GetSetterFieldDescription()
        {
            if (IsSettableField || IsSettableProperty) return null;
            return GetFieldAccessor(ContainingType, TypeSyntax, MemberName, SetterFieldName, LibraryTypes, true,
                IsPrimaryConstructorParameter && IsProperty, _generatorServices.Options.HotReloadSafe, UseUnsafeFieldAccessor);
        }

        private bool UseUnsafeFieldAccessor => Field is not null
            && !_generatorServices.Options.HotReloadSafe
            && _generatorServices.Options.SupportsUnsafeAccessors
            && LibraryTypes.HasUnsafeAccessorAttribute
            && (!Field.IsVolatile || _generatorServices.Options.SupportsVolatileUnsafeAccessors)
            && (!ContainingType.IsGenericType || _generatorServices.Options.SupportsGenericUnsafeAccessors);

        public static FieldAccessorDescription GetFieldAccessor(INamedTypeSymbol containingType, TypeSyntax fieldType, string fieldName, string accessorName, LibraryTypes library, bool setter, bool useUnsafeAccessor = false, bool lazyInitialization = false, bool useUnsafeFieldAccessor = false)
        {
            var containingTypeSyntax = containingType.ToTypeSyntax();

            if (useUnsafeFieldAccessor || useUnsafeAccessor)
                return new(containingTypeSyntax, fieldType, fieldName, accessorName,
                    isUnsafeFieldAccessor: useUnsafeFieldAccessor, containingTypeIsValueType: containingType.IsValueType);

            var valueType = containingType.IsValueType;

            var delegateType = (setter ? (valueType ? library.ValueTypeSetter_2 : library.Action_2) : (valueType ? library.ValueTypeGetter_2 : library.Func_2))
                .ToTypeSyntax(containingTypeSyntax, fieldType);

            // The accessor field is initialized from a reflection-backed delegate.
            var accessorMethod = setter ? (valueType ? "GetValueSetter" : "GetReferenceSetter") : (valueType ? "GetValueGetter" : "GetGetter");
            var initialization = $"({CodeText.Type(delegateType)})global::Orleans.Serialization.Utilities.FieldAccessor.{accessorMethod}(typeof({CodeText.Type(containingTypeSyntax)}), {CodeText.QuotedText(fieldName)})";

            // Existing case, accessor is the field in both cases
            return new(containingTypeSyntax, delegateType, accessorName, accessorName, initialization, lazyInitialization);
        }
    }
}
