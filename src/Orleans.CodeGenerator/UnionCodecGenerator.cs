using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator;

/// <summary>
/// Generates tagged-union codecs and copiers for union types and closed type hierarchies.
/// </summary>
/// <remarks>
/// A tagged-union value is written as a tag-delimited field containing at most one field, whose field identifier is the case identifier
/// and whose expected type is the case type. The runtime type of a case value is therefore encoded only when it differs from the case type.
/// </remarks>
internal sealed class UnionCodecGenerator(IGeneratorServices generatorServices)
{
    private const string Helper = "global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper";
    private const string FieldHeaderCodec = "global::Orleans.Serialization.Codecs.FieldHeaderCodec";
    private const string BufferWriterConstraint = "where TBufferWriter : global::System.Buffers.IBufferWriter<byte>";
    private const string CodecFieldTypeFieldName = "_codecFieldType";

    private LibraryTypes LibraryTypes => generatorServices.LibraryTypes;
    private bool HotReloadSafe => generatorServices.Options.HotReloadSafe;

    internal sealed record CaseCodec(UnionCaseDescription Case, string CaseTypeName, string ValueTypeName, SerializerGenerator.CodecFieldDescription? CodecField, string CodecExpression)
    {
        public UnionCaseSerializerPlan ToEmissionPlan() => new(Case.Id, CaseTypeName, ValueTypeName, CodecExpression, CodecField is null);
    }

    public List<CaseCodec> GetCaseCodecs(List<UnionCaseDescription> cases)
    {
        var result = new List<CaseCodec>(cases.Count);
        foreach (var unionCase in cases)
        {
            var valueType = unionCase.ValueType;
            var caseTypeName = CodeText.Type(unionCase.CaseType.ToTypeSyntax());
            var valueTypeName = CodeText.Type(valueType.ToTypeSyntax());
            if (LibraryTypes.StaticCodecs.FindByUnderlyingType(valueType) is { } staticCodec)
            {
                result.Add(new(unionCase, caseTypeName, valueTypeName, null, CodeText.Type(staticCodec.CodecType.ToNameSyntax())));
                continue;
            }

            // Case identifiers preserve field identities when hot reload inserts a case earlier in the list.
            var fieldName = $"_caseCodec{unionCase.Id}";
            var field = new SerializerGenerator.CodecFieldDescription(GetCodecType(valueType), fieldName, valueType, HotReloadSafe);
            result.Add(new(unionCase, caseTypeName, valueTypeName, field, SerializerGenerator.GetCodecExpression(field)));
        }

        return result;
    }

    private TypeSyntax GetCodecType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: false } named
            && named.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
            && (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, LibraryTypes.Compilation.Assembly)
                || named.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute)))
        {
            // Use the concrete generated codec to avoid interface dispatch.
            return QualifiedName(
                ParseName(SerializerGenerator.GetGeneratedNamespaceName(named)),
                IdentifierName(SerializerGenerator.GetSimpleClassName(named.Name)));
        }

        return LibraryTypes.FieldCodec_1.ToTypeSyntax(type.ToTypeSyntax());
    }

    public SerializerEmissionPlan CreateSerializerPlan(ISerializableTypeDescription type, List<UnionCaseDescription> cases, SerializableTypeEmissionPlan? typePlan)
    {
        var codecs = GetCaseCodecs(cases);
        var fields = new List<SerializerGenerator.GeneratedFieldDescription>
        {
            new SerializerGenerator.CodecFieldTypeFieldDescription(LibraryTypes.Type.ToTypeSyntax(), CodecFieldTypeFieldName, type.TypeSyntax),
        };
        fields.AddRange(codecs.Where(static codec => codec.CodecField is not null).Select(static codec => codec.CodecField!));
        AddDependencies(type, fields);
        return new(typePlan ?? SerializableTypeEmissionPlan.Create(type, LibraryTypes),
            CodeText.Type(LibraryTypes.FieldCodec_1.ToTypeSyntax(type.TypeSyntax)),
            CodeText.Type(LibraryTypes.ValueSerializer.ToTypeSyntax(type.TypeSyntax)),
            ConstructionEmissionPlan.Create(new(LibraryTypes, fields, hasBaseContract: false)),
            fields.Select(FieldEmissionPlan.Create).ToImmutableArray(), [], null,
            codecs.Select(static codec => codec.ToEmissionPlan()).ToImmutableArray());
    }

    public CopierEmissionPlan CreateCopierPlan(ISerializableTypeDescription type, List<UnionCaseDescription> cases, SerializableTypeEmissionPlan? typePlan)
    {
        var fields = new List<SerializerGenerator.GeneratedFieldDescription>();
        var copyCases = ImmutableArray.CreateBuilder<UnionCaseCopierPlan>();
        foreach (var unionCase in cases)
        {
            if (type.IsImmutable || LibraryTypes.IsShallowCopyable(unionCase.CaseType))
            {
                continue;
            }

            var field = new CopierGenerator.CopierFieldDescription(
                LibraryTypes.DeepCopier_1.ToTypeSyntax(unionCase.ValueType.ToTypeSyntax()),
                $"_caseCopier{unionCase.Id}", unionCase.ValueType, HotReloadSafe);
            fields.Add(field);
            copyCases.Add(new(CodeText.Type(unionCase.CaseType.ToTypeSyntax()),
                CodeText.Type(unionCase.ValueType.ToTypeSyntax()), SerializerGenerator.GetCodecExpression(field)));
        }

        AddDependencies(type, fields);
        return new(typePlan ?? SerializableTypeEmissionPlan.Create(type, LibraryTypes), false, false, false,
            CodeText.Type(LibraryTypes.DeepCopier_1.ToTypeSyntax(type.TypeSyntax)),
            CodeText.Type(LibraryTypes.BaseCopier_1.ToTypeSyntax(type.TypeSyntax)),
            ConstructionEmissionPlan.Create(new(LibraryTypes, fields, hasBaseContract: false)),
            fields.Select(FieldEmissionPlan.Create).ToImmutableArray(), [], copyCases.ToImmutable());
    }

    private void AddDependencies(ISerializableTypeDescription type, List<SerializerGenerator.GeneratedFieldDescription> fields)
    {
        if (HotReloadSafe)
        {
            fields.Insert(0, new SerializerGenerator.CodecProviderFieldDescription(LibraryTypes.ICodecProvider.ToTypeSyntax()));
        }

        for (var i = 0; i < type.SerializationHooks.Count; i++)
        {
            fields.Add(new SerializerGenerator.SerializationHookFieldDescription(type.SerializationHooks[i].ToTypeSyntax(), $"_hook{i}"));
        }
    }

    internal static void WriteUnionCodecMethods(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<UnionCaseSerializerPlan> cases)
    {
        WriteUnionSerializeMethod(writer, type, cases);
        WriteUnionDeserializeMethod(writer, type, cases);
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public void WriteField<TBufferWriter>(ref {type.Library.Writer} writer, uint fieldIdDelta, global::System.Type expectedType, {type.Type} value) {BufferWriterConstraint}");
        writer.OpenBlock();
        writer.WriteLine($"{Helper}.WriteStartUnion(ref writer, fieldIdDelta, expectedType, {CodecFieldTypeFieldName});");
        writer.WriteLine("Serialize(ref writer, ref value);");
        writer.WriteLine("writer.WriteEndObject();");
        writer.CloseBlock();

        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {type.Type} ReadValue<TReaderInput>(ref {type.Library.Reader} reader, global::Orleans.Serialization.WireProtocol.Field field)");
        writer.OpenBlock();
        writer.WriteLine("// Null references and values which encode their runtime type are read as case values, so payloads written for a closed type");
        writer.WriteLine("// hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.");
        writer.WriteLine("if (field.IsReference)");
        writer.Indent();
        writer.WriteLine("return ReadCaseValue(ref reader, ref field);");
        writer.Unindent();
        writer.BlankLine();
        writer.WriteLine($"if (field.FieldType is {{ }} fieldType && fieldType != {CodecFieldTypeFieldName})");
        writer.Indent();
        writer.WriteLine("return ReadCaseValue(ref reader, ref field);");
        writer.Unindent();
        writer.BlankLine();
        writer.WriteLine("field.EnsureWireTypeTagDelimited();");
        writer.WriteLine("global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);");
        writer.WriteLine($"{type.Type} result = default;");
        writer.WriteLine("Deserialize(ref reader, ref result);");
        writer.WriteLine("return result;");
        writer.CloseBlock();

        WriteReadCaseValueMethod(writer, type, cases);
    }

    private static void WriteUnionSerializeMethod(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<UnionCaseSerializerPlan> cases)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public void Serialize<TBufferWriter>(ref {type.Library.Writer} writer, scoped ref {type.Type} instance) {BufferWriterConstraint}");
        writer.OpenBlock();
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnSerializing");
        writer.WriteLine("switch (instance)");
        writer.OpenBlock();
        for (var i = 0; i < cases.Count; i++)
        {
            writer.WriteLine($"case {cases[i].ValueType} case{i}:");
            writer.Indent();
            writer.WriteLine(GetWriteCase(cases[i], $"case{i}"));
            writer.WriteLine("break;");
            writer.Unindent();
        }

        writer.CloseBlock();
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnSerialized");
        writer.CloseBlock();
    }

    private static void WriteUnionDeserializeMethod(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<UnionCaseSerializerPlan> cases)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public void Deserialize<TReaderInput>(ref {type.Library.Reader} reader, scoped ref {type.Type} instance)");
        writer.OpenBlock();
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnDeserializing");
        writer.WriteLine($"var header = {FieldHeaderCodec}.ReadFieldHeader(ref reader);");
        writer.WriteLine("if (header.IsEndBaseOrEndObject)");
        writer.OpenBlock();
        writer.WriteLine("instance = default;");
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnDeserialized");
        writer.WriteLine("return;");
        writer.CloseBlock();
        writer.WriteLine("switch (header.FieldIdDelta)");
        writer.OpenBlock();
        foreach (var unionCase in cases)
        {
            writer.WriteLine($"case {CodeText.Literal(unionCase.Id)}:");
            writer.Indent();
            writer.WriteLine($"instance = ({unionCase.CaseType}){GetReadCase(unionCase)};");
            writer.WriteLine("break;");
            writer.Unindent();
        }

        writer.WriteLine("default:");
        writer.Indent();
        writer.WriteLine($"instance = {Helper}.ThrowUnknownUnionCase<{type.Type}>(header.FieldIdDelta);");
        writer.WriteLine("break;");
        writer.Unindent();
        writer.CloseBlock();
        writer.WriteLine($"{Helper}.ConsumeEndBaseOrEndObject(ref reader);");
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnDeserialized");
        writer.CloseBlock();
    }

    private static void WriteReadCaseValueMethod(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<UnionCaseSerializerPlan> cases)
    {
        writer.WriteLine($"private {type.Type} ReadCaseValue<TReaderInput>(ref {type.Library.Reader} reader, ref global::Orleans.Serialization.WireProtocol.Field field)");
        writer.OpenBlock();
        writer.WriteLine($"{type.Type} instance = default;");
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnDeserializing");
        writer.WriteLine("var value = field.IsReference");
        writer.Indent();
        writer.WriteLine("? global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<object, TReaderInput>(ref reader, field)");
        writer.WriteLine($": {Helper}.DeserializeUnexpectedType<TReaderInput, object>(ref reader, ref field);");
        writer.Unindent();
        writer.WriteLine("switch (value)");
        writer.OpenBlock();
        writer.WriteLine("case null:");
        writer.Indent();
        writer.WriteLine("instance = default;");
        writer.WriteLine("break;");
        writer.Unindent();
        for (var i = 0; i < cases.Count; i++)
        {
            writer.WriteLine($"case {cases[i].ValueType} case{i}:");
            writer.Indent();
            writer.WriteLine($"instance = ({cases[i].CaseType})case{i};");
            writer.WriteLine("break;");
            writer.Unindent();
        }

        writer.WriteLine("default:");
        writer.Indent();
        writer.WriteLine($"{Helper}.ThrowUnsupportedUnionValue<{type.Type}>(value);");
        writer.WriteLine("break;");
        writer.Unindent();
        writer.CloseBlock();
        SerializerGenerator.WriteSerializationCallbacks(writer, type, "instance", "OnDeserialized");
        writer.WriteLine("return instance;");
        writer.CloseBlock();
    }

    /// <summary>
    /// Writes field codec methods for a closed base type. Its Serialize and Deserialize methods serialize the base members for its cases.
    /// </summary>
    internal static void WriteClosedTypeFieldCodecMethods(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<UnionCaseSerializerPlan> cases)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public void WriteField<TBufferWriter>(ref {type.Library.Writer} writer, uint fieldIdDelta, global::System.Type expectedType, {type.Type} value) {BufferWriterConstraint}");
        writer.OpenBlock();
        writer.WriteLine("// A null value is an empty union field, which is also how an empty union value is written.");
        writer.WriteLine($"{Helper}.WriteStartUnion(ref writer, fieldIdDelta, expectedType, {CodecFieldTypeFieldName});");
        writer.WriteLine("switch (value)");
        writer.OpenBlock();
        writer.WriteLine("case null:");
        writer.Indent();
        writer.WriteLine("break;");
        writer.Unindent();
        for (var i = 0; i < cases.Count; i++)
        {
            writer.WriteLine($"case {cases[i].CaseType} case{i}:");
            writer.Indent();
            writer.WriteLine(GetWriteCase(cases[i], $"case{i}"));
            writer.WriteLine("break;");
            writer.Unindent();
        }

        writer.WriteLine("default:");
        writer.Indent();
        writer.WriteLine($"{Helper}.ThrowUnsupportedUnionValue<{type.Type}>(value);");
        writer.WriteLine("break;");
        writer.Unindent();
        writer.CloseBlock();
        writer.WriteLine("writer.WriteEndObject();");
        writer.CloseBlock();

        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {type.Type} ReadValue<TReaderInput>(ref {type.Library.Reader} reader, global::Orleans.Serialization.WireProtocol.Field field)");
        writer.OpenBlock();
        writer.WriteLine("if (field.IsReference)");
        writer.Indent();
        writer.WriteLine($"return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<{type.Type}, TReaderInput>(ref reader, field);");
        writer.Unindent();
        writer.BlankLine();
        writer.WriteLine("// Values written before the type used case identifiers encode their runtime type in the field header.");
        writer.WriteLine($"if (field.FieldType is {{ }} fieldType && fieldType != {CodecFieldTypeFieldName})");
        writer.Indent();
        writer.WriteLine($"return {Helper}.DeserializeUnexpectedType<TReaderInput, {type.Type}>(ref reader, ref field);");
        writer.Unindent();
        writer.BlankLine();
        writer.WriteLine("field.EnsureWireTypeTagDelimited();");
        writer.WriteLine("global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);");
        writer.WriteLine($"var header = {FieldHeaderCodec}.ReadFieldHeader(ref reader);");
        writer.WriteLine("if (header.IsEndBaseOrEndObject)");
        writer.Indent();
        writer.WriteLine("return null;");
        writer.Unindent();
        writer.BlankLine();
        writer.WriteLine($"{type.Type} result;");
        writer.WriteLine("switch (header.FieldIdDelta)");
        writer.OpenBlock();
        foreach (var unionCase in cases)
        {
            writer.WriteLine($"case {CodeText.Literal(unionCase.Id)}:");
            writer.Indent();
            writer.WriteLine($"result = {GetReadCase(unionCase)};");
            writer.WriteLine("break;");
            writer.Unindent();
        }

        writer.WriteLine("default:");
        writer.Indent();
        writer.WriteLine($"result = {Helper}.ThrowUnknownUnionCase<{type.Type}>(header.FieldIdDelta);");
        writer.WriteLine("break;");
        writer.Unindent();
        writer.CloseBlock();
        writer.WriteLine($"{Helper}.ConsumeEndBaseOrEndObject(ref reader);");
        writer.WriteLine("return result;");
        writer.CloseBlock();
    }

    internal static void WriteUnionCopyMethod(SourceWriter writer, SerializableTypeEmissionPlan type, EquatableArray<UnionCaseCopierPlan> cases)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {type.Type} DeepCopy({type.Type} original, global::Orleans.Serialization.Cloning.CopyContext context)");
        writer.OpenBlock();
        var hasCopyingCallbacks = type.SerializationHooks.Any(static callback => callback.Method == "OnCopying");
        var initialValue = cases.Count == 0 && !hasCopyingCallbacks ? "original" : "default";
        writer.WriteLine($"{type.Type} result = {initialValue};");
        CopierGenerator.WriteSerializationCallbacks(writer, type, "original", "result", "OnCopying");
        if (cases.Count > 0)
        {
            writer.WriteLine("switch (original)");
            writer.OpenBlock();
            for (var i = 0; i < cases.Count; i++)
            {
                writer.WriteLine($"case {cases[i].ValueType} case{i}:");
                writer.Indent();
                writer.WriteLine($"result = ({cases[i].CaseType}){cases[i].Copier}.DeepCopy(case{i}, context);");
                writer.WriteLine("break;");
                writer.Unindent();
            }

            writer.WriteLine("default:");
            writer.Indent();
            writer.WriteLine("result = original;");
            writer.WriteLine("break;");
            writer.Unindent();
            writer.CloseBlock();
        }
        else if (hasCopyingCallbacks)
        {
            writer.WriteLine("result = original;");
        }

        CopierGenerator.WriteSerializationCallbacks(writer, type, "original", "result", "OnCopied");
        writer.WriteLine("return result;");
        writer.CloseBlock();
    }

    private static string GetWriteCase(UnionCaseSerializerPlan unionCase, string value) => unionCase.IsStatic
        ? $"{unionCase.Codec}.WriteField(ref writer, {CodeText.Literal(unionCase.Id)}, {value});"
        : $"{unionCase.Codec}.WriteField(ref writer, {CodeText.Literal(unionCase.Id)}, typeof({unionCase.ValueType}), {value});";

    private static string GetReadCase(UnionCaseSerializerPlan unionCase) => $"{unionCase.Codec}.ReadValue(ref reader, header)";
}
