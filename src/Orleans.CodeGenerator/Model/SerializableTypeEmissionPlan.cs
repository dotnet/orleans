using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator.Model;

// Emission plans hold the complete, spelled C# fragments which their writers emit: types, names, and expressions.
// They hold no symbols or syntax, and records give them value equality.

internal sealed record EmissionLibraryTypes(
    string SystemType,
    string Writer,
    string Reader,
    string Field,
    string BufferWriter,
    string CodecProvider,
    string CopyContext,
    bool HasScopedKeyword,
    string TimeSpan,
    string MethodInfo,
    string TargetHolder,
    string CancellationToken)
{
    public static EmissionLibraryTypes Create(LibraryTypes library) => new(
        CodeText.Type(library.Type.ToTypeSyntax()),
        CodeText.Type(library.Writer.ToTypeSyntax()),
        CodeText.Type(library.Reader.ToTypeSyntax(ParseTypeName("TReaderInput"))),
        CodeText.Type(library.Field.ToTypeSyntax()),
        CodeText.Type(library.IBufferWriter.ToTypeSyntax(PredefinedType(Token(SyntaxKind.ByteKeyword)))),
        CodeText.Type(library.ICodecProvider.ToTypeSyntax()),
        CodeText.Type(library.CopyContext.ToTypeSyntax()),
        library.HasScopedKeyword(),
        CodeText.Type(library.TimeSpan.ToTypeSyntax()),
        CodeText.Type(library.MethodInfo.ToTypeSyntax()),
        CodeText.Type(library.ITargetHolder.ToTypeSyntax()),
        CodeText.Type(library.CancellationToken.ToTypeSyntax()));
}

internal sealed record GenericParameterEmissionPlan(string Name, EquatableArray<string> Constraints)
{
    public static EquatableArray<GenericParameterEmissionPlan> Create(List<(string Name, ITypeParameterSymbol Parameter)> typeParameters)
        => SyntaxFactoryUtility.GetTypeParameterConstraintTexts(typeParameters)
            .Select(static parameter => new GenericParameterEmissionPlan(parameter.Name, parameter.Constraints.ToImmutableArray()))
            .ToImmutableArray();

    /// <summary>Appends <c>&lt;T1, T2&gt;</c>, or nothing for a non-generic declaration.</summary>
    public static void AppendParameterList(StringBuilder builder, EquatableArray<GenericParameterEmissionPlan> parameters)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        builder.Append('<');
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(parameters[i].Name);
        }

        builder.Append('>');
    }

    /// <summary>Writes a declaration line followed by its constraint clauses on the same line.</summary>
    public static void WriteDeclarationLine(SourceWriter writer, StringBuilder declaration, EquatableArray<GenericParameterEmissionPlan> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Constraints.Count == 0)
            {
                continue;
            }

            declaration.Append(" where ").Append(parameter.Name).Append(" : ");
            for (var i = 0; i < parameter.Constraints.Count; i++)
            {
                if (i > 0)
                {
                    declaration.Append(", ");
                }

                declaration.Append(parameter.Constraints[i]);
            }
        }

        writer.WriteLine(declaration.ToString());
    }
}

internal sealed record SerializationCallbackEmissionPlan(string Method, int HookIndex, bool FirstByRef, bool SecondByRef);

internal sealed record SerializableTypeEmissionPlan(
    string Name,
    bool IsPublic,
    string Type,
    bool IsValueType,
    bool IsSealedType,
    bool IsAbstractType,
    bool HasComplexBaseType,
    bool IncludePrimaryConstructorParameters,
    bool UseActivator,
    bool TrackReferences,
    bool OmitDefaultMemberValues,
    bool IsUnsealedImmutable,
    string ObjectCreation,
    EquatableArray<GenericParameterEmissionPlan> TypeParameters,
    EquatableArray<SerializationCallbackEmissionPlan> SerializationHooks,
    EmissionLibraryTypes Library)
{
    public bool IsGenericType => TypeParameters.Count > 0;

    public static SerializableTypeEmissionPlan Create(ISerializableTypeDescription type, LibraryTypes library)
    {
        var callbacks = ImmutableArray.CreateBuilder<SerializationCallbackEmissionPlan>();
        foreach (var method in new[] { "OnSerializing", "OnSerialized", "OnDeserializing", "OnDeserialized", "OnCopying", "OnCopied" })
        {
            var arity = method is "OnCopying" or "OnCopied" ? 2 : 1;
            for (var index = 0; index < type.SerializationHooks.Count; index++)
            {
                var member = type.SerializationHooks[index].GetAllMembers<IMethodSymbol>(method, Accessibility.Public).FirstOrDefault();
                if (member is null || member.Parameters.Length != arity)
                {
                    continue;
                }

                callbacks.Add(new(method, index, member.Parameters[0].RefKind == RefKind.Ref,
                    arity == 2 && member.Parameters[1].RefKind == RefKind.Ref));
            }
        }

        var typeText = CodeText.Type(type.TypeSyntax);
        return new(type.Name, type.Accessibility == Accessibility.Public, typeText,
            type.IsValueType, type.IsSealedType, type.IsAbstractType, type.HasComplexBaseType,
            type.IncludePrimaryConstructorParameters, type.UseActivator, type.TrackReferences,
            type.OmitDefaultMemberValues, type.IsUnsealedImmutable, GetObjectCreation(type, typeText),
            GenericParameterEmissionPlan.Create(type.TypeParameters), callbacks.ToImmutable(), library.EmissionTypes);
    }

    // Each description creates one of three construction forms; spell the selected form with the plan's type text.
    private static string GetObjectCreation(ISerializableTypeDescription type, string typeText) => type.GetObjectCreationExpression() switch
    {
        DefaultExpressionSyntax => $"default({typeText})",
        ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 0, Initializer: null } => $"new {typeText}()",
        CastExpressionSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax helper } }
            => $"({typeText}){CodeText.Type(helper.Expression)}.GetUninitializedObject(typeof({typeText}))",
        var expression => throw new InvalidOperationException($"Unsupported object creation expression '{expression}'."),
    };

    /// <summary>Appends the type parameter list for a generated declaration of this type.</summary>
    public void AppendTypeParameterList(StringBuilder builder) => GenericParameterEmissionPlan.AppendParameterList(builder, TypeParameters);
}

internal enum EmissionFieldKind
{
    Service,
    CodecProvider,
    Type,
    Accessor,
}

internal sealed record FieldEmissionPlan(
    string Type,
    string FieldName,
    EmissionFieldKind Kind,
    bool IsInjected,
    bool LazyInitialization,
    string? Initializer = null,
    string? ContainingType = null,
    string? AccessorName = null,
    bool IsUnsafeFieldAccessor = false,
    bool ContainingTypeIsValueType = false)
{
    public static FieldEmissionPlan Create(GeneratedFieldDescription field) => field switch
    {
        TypeFieldDescription type => new(CodeText.Type(field.FieldType), field.FieldName, EmissionFieldKind.Type, false, false,
            $"typeof({CodeText.Type(type.UnderlyingTypeSyntax)})"),
        CodecFieldTypeFieldDescription type => new(CodeText.Type(field.FieldType), field.FieldName, EmissionFieldKind.Type, false, false,
            $"typeof({CodeText.Type(type.CodecFieldType)})"),
        FieldAccessorDescription accessor => new(CodeText.Type(field.FieldType), field.FieldName, EmissionFieldKind.Accessor, false,
            field.LazyInitialization, accessor.Initialization, CodeText.Type(accessor.ContainingType), accessor.AccessorName,
            accessor.IsUnsafeFieldAccessor, accessor.ContainingTypeIsValueType),
        CodecProviderFieldDescription => new(CodeText.Type(field.FieldType), field.FieldName, EmissionFieldKind.CodecProvider, false, false),
        _ => new(CodeText.Type(field.FieldType), field.FieldName, EmissionFieldKind.Service, field.IsInjected, field.LazyInitialization),
    };
}

internal enum MemberAccessKind
{
    /// <summary><c>instance.Target</c>, assigned directly.</summary>
    Member,

    /// <summary><c>Target(instance)</c> reads; <c>Target(instance, value)</c> writes.</summary>
    Invoke,

    /// <summary><c>Target(instance)</c> returns a field reference which reads or is assigned.</summary>
    FieldReference,
}

/// <summary>
/// Accesses a member of an instance expression. <see cref="Target"/> is a spelled member name or accessor expression.
/// </summary>
internal sealed record MemberAccessPlan(MemberAccessKind Kind, string Target, bool RefInstance = false)
{
    public static MemberAccessPlan ForMember(string memberName) => new(MemberAccessKind.Member, CodeText.Identifier(memberName));

    public string Get(string instance) => Kind == MemberAccessKind.Member
        ? instance + "." + Target
        : $"{Target}({(RefInstance ? "ref " : string.Empty)}{instance})";

    public string Set(string instance, string value) => Kind switch
    {
        MemberAccessKind.Member => $"{instance}.{Target} = {value}",
        MemberAccessKind.Invoke => $"{Target}({(RefInstance ? "ref " : string.Empty)}{instance}, {value})",
        _ => $"{Target}({(RefInstance ? "ref " : string.Empty)}{instance}) = {value}",
    };
}

internal enum MemberCopyKind
{
    /// <summary>The value is shallow-copyable and is assigned as read.</summary>
    Value,

    /// <summary><c>context.DeepCopy(value)</c>.</summary>
    Context,

    /// <summary><c>Copier.DeepCopy(value, context)</c>, optionally cast to the member type.</summary>
    Copier,
}

internal sealed record MemberCopyPlan(MemberCopyKind Kind, string? Copier = null, string? CastType = null)
{
    public static MemberCopyPlan Value { get; } = new(MemberCopyKind.Value);

    public string Copy(string value, string context) => Kind switch
    {
        MemberCopyKind.Value => value,
        MemberCopyKind.Context => $"{context}.DeepCopy({value})",
        _ => CastType is null
            ? $"{Copier}.DeepCopy({value}, {context})"
            : $"({CastType}){Copier}.DeepCopy({value}, {context})",
    };
}

internal sealed record MemberEmissionPlan(
    uint FieldId,
    bool IsPrimaryConstructorParameter,
    bool IsValueType,
    MemberAccessPlan Getter,
    MemberAccessPlan Setter,
    string? Codec,
    string? ExpectedType,
    MemberCopyPlan? Copy)
{
    public string GetGetter(string instance) => Getter.Get(instance);
    public string GetSetter(string instance, string value) => Setter.Set(instance, value);
    public string GetCopy(string instance, string context)
    {
        Debug.Assert(Copy is not null);
        return Copy!.Copy(Getter.Get(instance), context);
    }
}

internal sealed record SerializerEmissionPlan(
    SerializableTypeEmissionPlan Type,
    string BaseType,
    string? AdditionalInterface,
    ConstructionEmissionPlan Construction,
    EquatableArray<FieldEmissionPlan> Fields,
    EquatableArray<MemberEmissionPlan> Members,
    EnumSerializerPlan? Enum);

internal sealed record CopierEmissionPlan(
    SerializableTypeEmissionPlan Type,
    bool IsShallowCopyable,
    bool IsExceptionType,
    bool OnlyDeepFields,
    string BaseType,
    string AdditionalInterface,
    ConstructionEmissionPlan Construction,
    EquatableArray<FieldEmissionPlan> Fields,
    EquatableArray<MemberEmissionPlan> Members);

// Project shared construction data without retaining the symbol-bearing service fields.
internal sealed record ConstructionEmissionPlan(
    bool HasBaseContract,
    EquatableArray<ConstructorParameterEmissionPlan> Parameters,
    string? DependencyContract)
{
    public static ConstructionEmissionPlan Create(GeneratedTypeConstruction construction, string? dependencyContract)
        => new(construction.HasBaseContract, construction.Parameters.Select(static parameter =>
            new ConstructorParameterEmissionPlan(CodeText.Type(parameter.Type), CodeText.Identifier(parameter.Name))).ToImmutableArray(),
            dependencyContract);

    /// <summary>Appends <c>Type name, ...</c> for the constructor's parameter list.</summary>
    public void AppendParameters(StringBuilder builder)
    {
        for (var i = 0; i < Parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Parameters[i].Type).Append(' ').Append(Parameters[i].Name);
        }
    }
}

internal sealed record ConstructorParameterEmissionPlan(string Type, string Name);

internal sealed record ActivatorEmissionPlan(
    SerializableTypeEmissionPlan Type,
    string BaseInterface,
    EquatableArray<string> ConstructorParameters);
