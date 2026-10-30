using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

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
            new(type.TypeSyntax.ToString()),
            new(type.BaseTypeSyntax.ToString()),
            new(codec!.CodecType.ToNameSyntax().ToString()),
            new(libraryTypes.FieldCodec_1.ToTypeSyntax(type.TypeSyntax).ToString()),
            new(libraryTypes.Type.ToTypeSyntax().ToString()),
            new(libraryTypes.Writer.ToTypeSyntax().ToString()),
            new(libraryTypes.Reader.ToTypeSyntax(ParseTypeName("TReaderInput")).ToString()),
            new(libraryTypes.Field.ToTypeSyntax().ToString()),
            new(libraryTypes.IBufferWriter.ToTypeSyntax(PredefinedType(Token(SyntaxKind.ByteKeyword))).ToString()));
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

        return new(CreatePlan(type, libraryTypes), assemblyName, metadataIdentity, type.GeneratedNamespace);
    }

    internal static SourceOutputResult GenerateSource(EnumSerializerOutputPlan output)
    {
        var plan = output.Serializer;
        var serializer = ClassDeclaration(SerializerGenerator.GetSimpleClassName(plan.Name))
            .AddBaseListTypes(SimpleBaseType(ParseTypeName(plan.CodecInterface.SyntaxString)))
            .AddModifiers(Token(plan.IsPublic ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
            .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes())
            .AddMembers(
                FieldDeclaration(VariableDeclaration(
                    ParseTypeName(plan.SystemType.SyntaxString),
                    SingletonSeparatedList(VariableDeclarator(CodecFieldTypeFieldName)
                        .WithInitializer(EqualsValueClause(TypeOfExpression(ParseTypeName(plan.Type.SyntaxString)))))))
                    .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)),
                GenerateWriteMethod(plan),
                GenerateReadMethod(plan));

        return SourceOutputResult.FromSource(GeneratedSourceOutput.CreateSerializableSourceEntry(
            output.AssemblyName, plan.Type.SyntaxString, output.MetadataIdentity,
            output.GeneratedNamespace, 0, serializer, null, null, output.GeneratedNamespace));
    }

    internal static MemberDeclarationSyntax GenerateWriteMethod(EnumSerializerPlan plan)
    {
        var writerParam = "writer".ToIdentifierName();
        var fieldIdDeltaParam = "fieldIdDelta".ToIdentifierName();
        var expectedTypeParam = "expectedType".ToIdentifierName();
        var valueParam = "value".ToIdentifierName();
        var statement = ExpressionStatement(
            InvocationExpression(
                ParseName(plan.StaticCodec.SyntaxString).Member("WriteField"),
                ArgumentList(SeparatedList(
                [
                    Argument(writerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                    Argument(fieldIdDeltaParam),
                    Argument(expectedTypeParam),
                    Argument(CastExpression(ParseTypeName(plan.UnderlyingType.SyntaxString), valueParam)),
                    Argument(IdentifierName(CodecFieldTypeFieldName))
                ]))));

        return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "WriteField")
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(
                Parameter("writer".ToIdentifier()).WithType(ParseTypeName(plan.Writer.SyntaxString)).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter("fieldIdDelta".ToIdentifier()).WithType(PredefinedType(Token(SyntaxKind.UIntKeyword))),
                Parameter("expectedType".ToIdentifier()).WithType(ParseTypeName(plan.SystemType.SyntaxString)),
                Parameter("value".ToIdentifier()).WithType(ParseTypeName(plan.Type.SyntaxString)))
            .AddTypeParameterListParameters(TypeParameter("TBufferWriter"))
            .AddConstraintClauses(TypeParameterConstraintClause("TBufferWriter")
                .AddConstraints(TypeConstraint(ParseTypeName(plan.BufferWriter.SyntaxString))))
            .AddAttributeLists(AttributeList(SingletonSeparatedList(GeneratedCodeUtilities.GetMethodImplAttributeSyntax())))
            .AddBodyStatements(statement);
    }

    internal static MemberDeclarationSyntax GenerateReadMethod(EnumSerializerPlan plan)
    {
        var readerParam = "reader".ToIdentifierName();
        var fieldParam = "field".ToIdentifierName();
        var readValue = InvocationExpression(
            ParseName(plan.StaticCodec.SyntaxString).Member("ReadValue"),
            ArgumentList(SeparatedList(
            [
                Argument(readerParam).WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)),
                Argument(fieldParam)
            ])));

        return MethodDeclaration(ParseTypeName(plan.Type.SyntaxString), "ReadValue")
            .AddTypeParameterListParameters(TypeParameter("TReaderInput"))
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(
                Parameter(readerParam.Identifier).WithType(ParseTypeName(plan.Reader.SyntaxString)).WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))),
                Parameter(fieldParam.Identifier).WithType(ParseTypeName(plan.Field.SyntaxString)))
            .AddAttributeLists(AttributeList(SingletonSeparatedList(GeneratedCodeUtilities.GetMethodImplAttributeSyntax())))
            .AddBodyStatements(ReturnStatement(CastExpression(ParseTypeName(plan.Type.SyntaxString), readValue)));
    }
}
