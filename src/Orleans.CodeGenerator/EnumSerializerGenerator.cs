using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class EnumSerializerGenerator
{
    private const string CodecFieldTypeFieldName = "_codecFieldType";

    internal static EnumSerializerPlan CreatePlan(ISerializableTypeDescription type, LibraryTypes libraryTypes)
    {
        Debug.Assert(type.IsEnumType);
        var codec = libraryTypes.StaticCodecs.FindByUnderlyingType(type.BaseType);
        Debug.Assert(codec is not null);
        return new(
            type.Name,
            type.Accessibility == Accessibility.Public,
            CodeText.Type(type.TypeSyntax),
            CodeText.Type(type.BaseTypeSyntax),
            CodeText.Type(codec!.CodecType.ToNameSyntax()),
            CodeText.Type(libraryTypes.FieldCodec_1.ToTypeSyntax(type.TypeSyntax)),
            libraryTypes.EmissionTypes);
    }

    internal static EnumSerializerOutputPlan? CreateOutputPlan(
        ISerializableTypeDescription type,
        LibraryTypes libraryTypes,
        string assemblyName,
        TypeMetadataIdentity metadataIdentity)
    {
        // Ordinary non-generic enums have one codec field and no copier or activator.
        // More general shapes continue through the compilation-aware serializer pipeline.
        if (!type.IsEnumType || type.IsGenericType || type.Members.Count > 0
            || type.SerializationHooks.Count > 0 || type.UseActivator)
        {
            return null;
        }

        return new(CreatePlan(type, libraryTypes), assemblyName, metadataIdentity, type.GeneratedNamespace, type.TypeSyntax.ToString());
    }

    internal static SourceOutputResult GenerateSource(EnumSerializerOutputPlan output)
    {
        var plan = output.Serializer;
        return SourceOutputResult.FromSource(GeneratedSourceOutput.CreateSerializableSourceEntry(
            output.AssemblyName, output.TypeSyntax, output.MetadataIdentity, output.GeneratedNamespace, 0,
            output.GeneratedNamespace, writer => Write(writer, plan)), new(output.MetadataIdentity, output.TypeSyntax));
    }

    private static void Write(SourceWriter writer, EnumSerializerPlan plan)
    {
        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        writer.WriteLine($"{(plan.IsPublic ? "public" : "internal")} sealed class {SerializerGenerator.GetSimpleClassName(plan.Name)} : {plan.CodecInterface}");
        writer.OpenBlock();
        writer.WriteLine($"private readonly {plan.Library.SystemType} {CodecFieldTypeFieldName} = typeof({plan.Type});");
        WriteWriteMethod(writer, plan);
        WriteReadMethod(writer, plan);
        writer.CloseBlock();
    }

    internal static void WriteWriteMethod(SourceWriter writer, EnumSerializerPlan plan)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public void WriteField<TBufferWriter>(ref {plan.Library.Writer} writer, uint fieldIdDelta, {plan.Library.SystemType} expectedType, {plan.Type} @value)");
        writer.Indent();
        writer.WriteLine($"where TBufferWriter : {plan.Library.BufferWriter}");
        writer.Unindent();
        writer.OpenBlock();
        writer.WriteLine($"{plan.StaticCodec}.WriteField(ref writer, fieldIdDelta, expectedType, ({plan.UnderlyingType})@value, {CodecFieldTypeFieldName});");
        writer.CloseBlock();
    }

    internal static void WriteReadMethod(SourceWriter writer, EnumSerializerPlan plan)
    {
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {plan.Type} ReadValue<TReaderInput>(ref {plan.Library.Reader} reader, {plan.Library.Field} field)");
        writer.OpenBlock();
        writer.WriteLine($"return ({plan.Type}){plan.StaticCodec}.ReadValue(ref reader, field);");
        writer.CloseBlock();
    }
}
