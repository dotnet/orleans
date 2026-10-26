using System.Diagnostics;
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
    private bool HotReloadSafe => generatorServices.Options.HotReloadSafe;

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

            // Case identifiers preserve field identities when hot reload inserts a case earlier in the list.
            var fieldName = $"_caseCodec{unionCase.Id}";
            var field = new SerializerGenerator.CodecFieldDescription(GetCodecType(valueType), fieldName, valueType, HotReloadSafe);
            result.Add(new(unionCase, caseTypeName, valueTypeName, field, SerializerGenerator.GetCodecExpression(field).NormalizeWhitespace().ToFullString()));
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

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref {typeName} instance) {BufferWriterConstraint}");
        source.AppendLine("    {");
        AppendSerializationCallbacks(source, type, "OnSerializing");
        source.AppendLine("        switch (instance)");
        source.AppendLine("        {");
        for (var i = 0; i < codecs.Count; i++)
        {
            source.AppendLine($"            case {codecs[i].ValueTypeName} case{i}: {GetWriteCase(codecs[i], $"case{i}")} break;");
        }

        source.AppendLine("        }");
        AppendSerializationCallbacks(source, type, "OnSerialized");
        source.AppendLine("    }");

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref {typeName} instance)");
        source.AppendLine("    {");
        AppendSerializationCallbacks(source, type, "OnDeserializing");
        source.AppendLine($"        var header = {FieldHeaderCodec}.ReadFieldHeader(ref reader);");
        source.AppendLine("        if (header.IsEndBaseOrEndObject)");
        source.AppendLine("        {");
        source.AppendLine("            instance = default;");
        AppendSerializationCallbacks(source, type, "OnDeserialized");
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
        AppendSerializationCallbacks(source, type, "OnDeserialized");
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
        source.AppendLine("        // Null references and values which encode their runtime type are read as case values, so payloads written for a closed type");
        source.AppendLine("        // hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.");
        source.AppendLine("        if (field.IsReference)");
        source.AppendLine("            return ReadCaseValue(ref reader, ref field);");
        source.AppendLine();
        source.AppendLine($"        if (field.FieldType is {{ }} fieldType && fieldType != {CodecFieldTypeFieldName})");
        source.AppendLine("            return ReadCaseValue(ref reader, ref field);");
        source.AppendLine();
        source.AppendLine("        field.EnsureWireTypeTagDelimited();");
        source.AppendLine("        global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);");
        source.AppendLine($"        {typeName} result = default;");
        source.AppendLine("        Deserialize(ref reader, ref result);");
        source.AppendLine("        return result;");
        source.AppendLine("    }");

        source.AppendLine($"    private {typeName} ReadCaseValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, ref global::Orleans.Serialization.WireProtocol.Field field)");
        source.AppendLine("    {");
        source.AppendLine($"        {typeName} instance = default;");
        AppendSerializationCallbacks(source, type, "OnDeserializing");
        source.AppendLine("        var value = field.IsReference");
        source.AppendLine("            ? global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<object, TReaderInput>(ref reader, field)");
        source.AppendLine($"            : {Helper}.DeserializeUnexpectedType<TReaderInput, object>(ref reader, ref field);");
        source.AppendLine("        switch (value)");
        source.AppendLine("        {");
        source.AppendLine("            case null: instance = default; break;");
        for (var i = 0; i < codecs.Count; i++)
        {
            source.AppendLine($"            case {codecs[i].ValueTypeName} case{i}: instance = ({codecs[i].CaseTypeName})case{i}; break;");
        }

        source.AppendLine($"            default: {Helper}.ThrowUnsupportedUnionValue<{typeName}>(value); break;");
        source.AppendLine("        }");
        AppendSerializationCallbacks(source, type, "OnDeserialized");
        source.AppendLine("        return instance;");
        source.AppendLine("    }");
        source.AppendLine("}");

        return AddDependencies(FinishClass(type, source), type, codecs.Where(static c => c.CodecField is not null).Select(static c => c.CodecField!).ToList<SerializerGenerator.GeneratedFieldDescription>(), isCopier: false);
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
        source.AppendLine("        // A null value is an empty union field, which is also how an empty union value is written.");
        source.AppendLine($"        {Helper}.WriteStartUnion(ref writer, fieldIdDelta, expectedType, {CodecFieldTypeFieldName});");
        source.AppendLine("        switch (value)");
        source.AppendLine("        {");
        source.AppendLine("            case null: break;");
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
        source.AppendLine("        field.EnsureWireTypeTagDelimited();");
        source.AppendLine("        global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);");
        source.AppendLine($"        var header = {FieldHeaderCodec}.ReadFieldHeader(ref reader);");
        source.AppendLine("        if (header.IsEndBaseOrEndObject)");
        source.AppendLine("            return null;");
        source.AppendLine();
        source.AppendLine($"        {typeName} result;");
        source.AppendLine("        switch (header.FieldIdDelta)");
        source.AppendLine("        {");
        foreach (var codec in codecs)
        {
            source.AppendLine($"            case {codec.Case.Id}U: result = {GetReadCase(codec)}; break;");
        }

        source.AppendLine($"            default: result = {Helper}.ThrowUnknownUnionCase<{typeName}>(header.FieldIdDelta); break;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine($"        {Helper}.ConsumeEndBaseOrEndObject(ref reader);");
        source.AppendLine("        return result;");
        source.AppendLine("    }");
        source.AppendLine("}");

        return ParseClass(source).Members;
    }

    /// <summary>
    /// Generates a copier for a struct union type with deep-copyable cases or serialization callbacks.
    /// </summary>
    public ClassDeclarationSyntax GenerateUnionCopier(ISerializableTypeDescription type, List<UnionCaseDescription> cases)
    {
        var typeName = type.TypeSyntax.ToString();
        var className = CopierGenerator.GetSimpleClassName(type);
        var copierFields = new List<SerializerGenerator.GeneratedFieldDescription>();
        var source = new StringBuilder();
        source.AppendLine($"public sealed class {className} : global::Orleans.Serialization.Cloning.IDeepCopier<{typeName}>");
        source.AppendLine("{");
        var copyCases = new StringBuilder();
        for (var i = 0; i < cases.Count; i++)
        {
            var unionCase = cases[i];
            if (type.IsImmutable || LibraryTypes.IsShallowCopyable(unionCase.CaseType))
            {
                continue;
            }

            var caseTypeName = unionCase.CaseType.ToTypeSyntax().ToString();
            var valueTypeName = unionCase.ValueType.ToTypeSyntax().ToString();
            var fieldName = $"_caseCopier{unionCase.Id}";
            var field = new CopierGenerator.CopierFieldDescription(LibraryTypes.DeepCopier_1.ToTypeSyntax(unionCase.ValueType.ToTypeSyntax()), fieldName, unionCase.ValueType, HotReloadSafe);
            copierFields.Add(field);
            var copier = SerializerGenerator.GetCodecExpression(field).NormalizeWhitespace().ToFullString();
            copyCases.AppendLine($"            case {valueTypeName} case{i}: result = ({caseTypeName}){copier}.DeepCopy(case{i}, context); break;");
        }

        source.AppendLine(MethodImpl);
        source.AppendLine($"    public {typeName} DeepCopy({typeName} original, global::Orleans.Serialization.Cloning.CopyContext context)");
        source.AppendLine("    {");
        source.AppendLine($"        {typeName} result = default;");
        AppendCopyCallbacks(source, type, "OnCopying");
        source.AppendLine("        switch (original)");
        source.AppendLine("        {");
        source.Append(copyCases);
        source.AppendLine("            default: result = original; break;");
        source.AppendLine("        }");
        AppendCopyCallbacks(source, type, "OnCopied");
        source.AppendLine("        return result;");
        source.AppendLine("    }");
        source.AppendLine("}");

        return AddDependencies(FinishClass(type, source), type, copierFields, isCopier: true);
    }

    private ClassDeclarationSyntax AddDependencies(ClassDeclarationSyntax declaration, ISerializableTypeDescription type, List<SerializerGenerator.GeneratedFieldDescription> fields, bool isCopier)
    {
        if (HotReloadSafe)
        {
            fields.Insert(0, new SerializerGenerator.CodecProviderFieldDescription(LibraryTypes.ICodecProvider.ToTypeSyntax()));
        }

        for (var i = 0; i < type.SerializationHooks.Count; i++)
        {
            fields.Add(new SerializerGenerator.SerializationHookFieldDescription(type.SerializationHooks[i].ToTypeSyntax(), $"_hook{i}"));
        }

        var constructor = isCopier
            ? new CopierGenerator(generatorServices).GenerateConstructor(declaration.Identifier.Text, fields, isExceptionType: false)
            : new SerializerGenerator(generatorServices).GenerateConstructor(declaration.Identifier.Text, fields);
        declaration = declaration.AddMembers(SerializerGenerator.GetFieldDeclarations(fields));
        return constructor is null ? declaration : declaration.AddMembers(constructor);
    }

    private static void AppendSerializationCallbacks(StringBuilder source, ISerializableTypeDescription type, string methodName)
    {
        var statements = new List<StatementSyntax>();
        SerializerGenerator.AddSerializationCallbacks(type, IdentifierName("instance"), methodName, statements);
        foreach (var statement in statements)
        {
            source.AppendLine(statement.NormalizeWhitespace().ToFullString());
        }
    }

    private static void AppendCopyCallbacks(StringBuilder source, ISerializableTypeDescription type, string methodName)
    {
        var statements = new List<StatementSyntax>();
        CopierGenerator.AddSerializationCallbacks(type, IdentifierName("original"), IdentifierName("result"), methodName, statements);
        foreach (var statement in statements)
        {
            source.AppendLine(statement.NormalizeWhitespace().ToFullString());
        }
    }

    private static string GetWriteCase(CaseCodec codec, string value) => codec.IsStatic
        ? $"{codec.CodecExpression}.WriteField(ref writer, {codec.Case.Id}U, {value});"
        : $"{codec.CodecExpression}.WriteField(ref writer, {codec.Case.Id}U, typeof({codec.ValueTypeName}), {value});";

    private static string GetReadCase(CaseCodec codec) => $"{codec.CodecExpression}.ReadValue(ref reader, header)";

    private static ClassDeclarationSyntax ParseClass(StringBuilder source)
    {
        var declaration = (ClassDeclarationSyntax)ParseMemberDeclaration(source.ToString())!;
        Debug.Assert(!declaration.ContainsDiagnostics, $"Generated source does not parse: {string.Join("; ", declaration.GetDiagnostics())}");
        return declaration;
    }

    private static ClassDeclarationSyntax FinishClass(ISerializableTypeDescription type, StringBuilder source)
    {
        var classDeclaration = ParseClass(source)
            .WithModifiers(TokenList(Token(type.Accessibility == Accessibility.Public ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword)))
            .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes());
        if (type.IsGenericType)
        {
            classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, type.TypeParameters);
        }

        return classDeclaration;
    }
}
