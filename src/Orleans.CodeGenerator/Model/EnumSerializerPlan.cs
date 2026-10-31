namespace Orleans.CodeGenerator.Model;

// Each type syntax is resolved against the current compilation before this symbol-free cache boundary.
internal sealed record EnumSerializerPlan(
    string Name,
    bool IsPublic,
    TypeRef Type,
    TypeRef UnderlyingType,
    TypeRef StaticCodec,
    TypeRef CodecInterface,
    TypeRef SystemType,
    TypeRef Writer,
    TypeRef Reader,
    TypeRef Field,
    TypeRef BufferWriter);

internal sealed record EnumSerializerOutputPlan(
    EnumSerializerPlan Serializer,
    string AssemblyName,
    TypeMetadataIdentity MetadataIdentity,
    string GeneratedNamespace);
