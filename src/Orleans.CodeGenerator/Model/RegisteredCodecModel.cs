namespace Orleans.CodeGenerator.Model;

/// <summary>
/// The kind of manually-registered codec/provider.
/// </summary>
internal enum RegisteredCodecKind : byte
{
    Serializer,
    Copier,
    Activator,
    Converter
}

/// <summary>
/// Describes a type annotated with <c>[RegisterSerializer]</c>, <c>[RegisterCopier]</c>,
/// <c>[RegisterActivator]</c>, or <c>[RegisterConverter]</c>.
/// </summary>
internal readonly record struct RegisteredCodecModel(TypeRef Type, RegisteredCodecKind Kind);
