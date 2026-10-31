namespace Orleans.CodeGenerator.Model;

// Each fragment is spelled against the current compilation before this symbol-free cache boundary.
internal sealed record EnumSerializerPlan(
    string Name,
    bool IsPublic,
    string Type,
    string UnderlyingType,
    string StaticCodec,
    string CodecInterface,
    EmissionLibraryTypes Library);

// TypeSyntax is the serializable type's identity spelling, which names its source hint.
internal sealed record EnumSerializerOutputPlan(
    EnumSerializerPlan Serializer,
    string AssemblyName,
    TypeMetadataIdentity MetadataIdentity,
    string GeneratedNamespace,
    string TypeSyntax,
    string? DependencyContract);
