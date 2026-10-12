using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    private const string MethodImpl = "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]";
    private const string BufferWriterConstraint = "where TBufferWriter : global::System.Buffers.IBufferWriter<byte>";
    internal const string CodecFieldTypeFieldName = "_codecFieldType";

    private LibraryTypes LibraryTypes => generatorServices.LibraryTypes;

    /// <summary>
    /// Describes how generated code reads and writes the values of one case.
    /// </summary>
    /// <param name="Case">The case.</param>
    /// <param name="CaseTypeName">The case type syntax.</param>
    /// <param name="ValueTypeName">The case value type syntax, which is used for type patterns.</param>
    /// <param name="CodecField">The codec field, or <see langword="null"/> when the case value type has a static codec.</param>
    /// <param name="CodecExpression">The expression which refers to the codec.</param>
    internal sealed record CaseCodec(UnionCaseDescription Case, string CaseTypeName, string ValueTypeName, SerializerGenerator.CodecFieldDescription? CodecField, string CodecExpression)
    {
        public bool IsStatic => CodecField is null;
    }

    public List<CaseCodec> GetCaseCodecs(List<UnionCaseDescription> cases)
    {
        var result = new List<CaseCodec>(cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            var unionCase = cases[i];
            var valueType = unionCase.ValueType;
            var caseTypeName = unionCase.CaseType.ToTypeSyntax().ToString();
            var valueTypeName = valueType.ToTypeSyntax().ToString();
            if (LibraryTypes.StaticCodecs.FindByUnderlyingType(valueType) is { } staticCodec)
            {
                result.Add(new(unionCase, caseTypeName, valueTypeName, null, staticCodec.CodecType.ToNameSyntax().ToString()));
                continue;
            }

            var fieldName = $"_caseCodec{i}";
            var field = new SerializerGenerator.CodecFieldDescription(GetCodecType(valueType), fieldName, valueType);
            result.Add(new(unionCase, caseTypeName, valueTypeName, field, fieldName));
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

    /// <summary>
    /// Generates a codec for a struct union type.
    /// </summary>
    public ClassDeclarationSyntax GenerateUnionCodec(ISerializableTypeDescription type, List<UnionCaseDescription> cases)
    {
        var codecs = GetCaseCodecs(cases);
        var typeName = type.TypeSyntax.ToString();
        var className = SerializerGenerator.GetSimpleClassName(type);
        var source = new StringBuilder();
        source.AppendLine($"public sealed class {className} : global::Orleans.Serialization.Codecs.IFieldCodec<{typeName}>, global::Orleans.Serialization.Serializers.IValueSerializer<{typeName}>");
        source.AppendLine("{");
        source.AppendLine($"    private readonly global::System.Type {CodecFieldTypeFieldName} = typeof({typeName});");
        AppendCodecFields(source, codecs);
        AppendConstructor(source, className, codecs.Where(static c => c.CodecField is not null).Select(static c => (c.CodecField!.FieldName, c.CodecField.FieldType.ToString(), Optional: false)));

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref {typeName} instance) {BufferWriterConstraint}");
        source.AppendLine("    {");
        source.AppendLine("        switch (instance)");
        source.AppendLine("        {");
        for (var i = 0; i < codecs.Count; i++)
        {
            source.AppendLine($"            case {codecs[i].ValueTypeName} case{i}: {GetWriteCase(codecs[i], $"case{i}")} break;");
        }

        source.AppendLine("        }");
        source.AppendLine("    }");

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref {typeName} instance)");
        source.AppendLine("    {");
        source.AppendLine($"        var header = {FieldHeaderCodec}.ReadFieldHeader(ref reader);");
        source.AppendLine("        if (header.IsEndBaseOrEndObject)");
        source.AppendLine("        {");
        source.AppendLine("            instance = default;");
        source.AppendLine("            return;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        switch (header.FieldIdDelta)");
        source.AppendLine("        {");
        foreach (var codec in codecs)
        {
            source.AppendLine($"            case {codec.Case.Id}U: instance = ({codec.CaseTypeName}){GetReadCase(codec)}; break;");
        }

        source.AppendLine($"            default: instance = {Helper}.ThrowUnknownUnionCase<{typeName}>(header.FieldIdDelta); break;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine($"        {Helper}.ConsumeEndBaseOrEndObject(ref reader);");
        source.AppendLine("    }");

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, {typeName} value) {BufferWriterConstraint}");
        source.AppendLine("    {");
        source.AppendLine($"        {Helper}.WriteStartUnion(ref writer, fieldIdDelta, expectedType, {CodecFieldTypeFieldName});");
        source.AppendLine("        Serialize(ref writer, ref value);");
        source.AppendLine("        writer.WriteEndObject();");
        source.AppendLine("    }");

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public {typeName} ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)");
        source.AppendLine("    {");
        source.AppendLine($"        {Helper}.ReadStartUnion(ref reader, field);");
        source.AppendLine($"        {typeName} result = default;");
        source.AppendLine("        Deserialize(ref reader, ref result);");
        source.AppendLine("        return result;");
        source.AppendLine("    }");
        source.AppendLine("}");

        return FinishClass(type, source);
    }

    /// <summary>
    /// Generates the field codec members of a closed base type's codec. The base type's members are serialized by the codec's
    /// <c>Serialize</c> and <c>Deserialize</c> methods, which subtype codecs call.
    /// </summary>
    public IEnumerable<MemberDeclarationSyntax> GenerateClosedTypeFieldCodecMembers(ISerializableTypeDescription type, List<CaseCodec> codecs)
    {
        var typeName = type.TypeSyntax.ToString();
        var source = new StringBuilder();
        source.AppendLine("class C {");
        source.AppendLine(MethodImpl);
        source.AppendLine($"    public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, {typeName} value) {BufferWriterConstraint}");
        source.AppendLine("    {");
        source.AppendLine("        if (value is null)");
        source.AppendLine("        {");
        source.AppendLine("            global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);");
        source.AppendLine("            return;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine($"        {Helper}.WriteStartUnion(ref writer, fieldIdDelta, expectedType, {CodecFieldTypeFieldName});");
        source.AppendLine("        switch ((object)value)");
        source.AppendLine("        {");
        for (var i = 0; i < codecs.Count; i++)
        {
            source.AppendLine($"            case {codecs[i].CaseTypeName} case{i}: {GetWriteCase(codecs[i], $"case{i}")} break;");
        }

        source.AppendLine($"            default: {Helper}.ThrowUnsupportedUnionValue<{typeName}>(value); break;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        writer.WriteEndObject();");
        source.AppendLine("    }");

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public {typeName} ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)");
        source.AppendLine("    {");
        source.AppendLine("        if (field.IsReference)");
        source.AppendLine($"            return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<{typeName}, TReaderInput>(ref reader, field);");
        source.AppendLine();
        source.AppendLine("        // Values written before the type used case identifiers encode their runtime type in the field header.");
        source.AppendLine($"        if (field.FieldType is {{ }} fieldType && fieldType != {CodecFieldTypeFieldName})");
        source.AppendLine($"            return {Helper}.DeserializeUnexpectedType<TReaderInput, {typeName}>(ref reader, ref field);");
        source.AppendLine();
        source.AppendLine($"        {Helper}.ReadStartUnion(ref reader, field);");
        source.AppendLine($"        var header = {FieldHeaderCodec}.ReadFieldHeader(ref reader);");
        source.AppendLine("        if (header.IsEndBaseOrEndObject)");
        source.AppendLine($"            return {Helper}.ThrowMissingUnionCase<{typeName}>();");
        source.AppendLine();
        source.AppendLine($"        {typeName} result;");
        source.AppendLine("        switch (header.FieldIdDelta)");
        source.AppendLine("        {");
        foreach (var codec in codecs)
        {
            var conversion = codec.Case.RequiresObjectConversion ? $"({typeName})(object)" : string.Empty;
            source.AppendLine($"            case {codec.Case.Id}U: result = {conversion}{GetReadCase(codec)}; break;");
        }

        source.AppendLine($"            default: result = {Helper}.ThrowUnknownUnionCase<{typeName}>(header.FieldIdDelta); break;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine($"        {Helper}.ConsumeEndBaseOrEndObject(ref reader);");
        source.AppendLine("        return result;");
        source.AppendLine("    }");
        source.AppendLine("}");

        return ((ClassDeclarationSyntax)ParseMemberDeclaration(source.ToString())!).Members;
    }

    /// <summary>
    /// Generates a copier for a struct union type which has at least one case that is not shallow-copyable.
    /// </summary>
    public ClassDeclarationSyntax GenerateUnionCopier(ISerializableTypeDescription type, List<UnionCaseDescription> cases)
    {
        var typeName = type.TypeSyntax.ToString();
        var className = CopierGenerator.GetSimpleClassName(type);
        var copierFields = new List<(string FieldName, string FieldType, bool Optional)>();
        var source = new StringBuilder();
        source.AppendLine($"public sealed class {className} : global::Orleans.Serialization.Cloning.IDeepCopier<{typeName}>");
        source.AppendLine("{");
        var copyCases = new StringBuilder();
        for (var i = 0; i < cases.Count; i++)
        {
            var unionCase = cases[i];
            if (LibraryTypes.IsShallowCopyable(unionCase.CaseType))
            {
                continue;
            }

            var caseTypeName = unionCase.CaseType.ToTypeSyntax().ToString();
            var valueTypeName = unionCase.ValueType.ToTypeSyntax().ToString();
            var fieldName = $"_caseCopier{i}";
            var fieldType = $"global::Orleans.Serialization.Cloning.IDeepCopier<{valueTypeName}>";
            copierFields.Add((fieldName, fieldType, Optional: true));
            source.AppendLine($"    private readonly {fieldType} {fieldName};");
            copyCases.AppendLine($"            case {valueTypeName} case{i}: return {fieldName} is null ? original : ({caseTypeName}){fieldName}.DeepCopy(case{i}, context);");
        }

        AppendConstructor(source, className, copierFields);
        source.AppendLine(MethodImpl);
        source.AppendLine($"    public {typeName} DeepCopy({typeName} original, global::Orleans.Serialization.Cloning.CopyContext context)");
        source.AppendLine("    {");
        source.AppendLine("        switch (original)");
        source.AppendLine("        {");
        source.Append(copyCases);
        source.AppendLine("            default: return original;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        return FinishClass(type, source);
    }

    private static void AppendCodecFields(StringBuilder source, List<CaseCodec> codecs)
    {
        foreach (var codec in codecs)
        {
            if (codec.CodecField is { } field)
            {
                source.AppendLine($"    private readonly {field.FieldType} {field.FieldName};");
            }
        }
    }

    private void AppendConstructor(StringBuilder source, string className, IEnumerable<(string FieldName, string FieldType, bool Optional)> fields)
    {
        var fieldList = fields.ToList();
        if (fieldList.Count == 0)
        {
            return;
        }

        source.AppendLine($"    public {className}({LibraryTypes.ICodecProvider.ToTypeSyntax()} codecProvider)");
        source.AppendLine("    {");
        foreach (var (fieldName, fieldType, optional) in fieldList)
        {
            var service = $"{Helper}.GetService<{fieldType}>(this, codecProvider)";
            source.AppendLine(optional
                ? $"        {fieldName} = {Helper}.GetOptionalCopier({service});"
                : $"        {fieldName} = {service};");
        }

        source.AppendLine("    }");
    }

    private static string GetWriteCase(CaseCodec codec, string value) => codec.IsStatic
        ? $"{codec.CodecExpression}.WriteField(ref writer, {codec.Case.Id}U, {value});"
        : $"{codec.CodecExpression}.WriteField(ref writer, {codec.Case.Id}U, typeof({codec.ValueTypeName}), {value});";

    private static string GetReadCase(CaseCodec codec) => $"{codec.CodecExpression}.ReadValue(ref reader, header)";

    private static ClassDeclarationSyntax FinishClass(ISerializableTypeDescription type, StringBuilder source)
    {
        var classDeclaration = ((ClassDeclarationSyntax)ParseMemberDeclaration(source.ToString())!)
            .WithModifiers(TokenList(Token(type.Accessibility == Accessibility.Public ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword)))
            .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes());
        if (type.IsGenericType)
        {
            classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, type.TypeParameters);
        }

        return classDeclaration;
    }
}
