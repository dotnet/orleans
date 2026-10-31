using System;

namespace Orleans;

/// <summary>
/// Generates closed serializer and copier registrations for a root type and its serialization dependencies.
/// </summary>
/// <typeparam name="T">The closed root type to register.</typeparam>
/// <remarks>
/// Apply this attribute to a partial class derived from <c>Orleans.Serialization.SerializerContext</c>.
/// Each root must be a closed type supported by the context generator. Register the generated context
/// using <c>AddSerializerContext</c> to enable explicit type lookup.
/// Reference-only models require a producer-certified complete contract: serialized fields and
/// properties are readable and writable from generated code, with visible member types, IDs,
/// and copy policies. Use explicit member IDs or type-local member selection. Internal members
/// are supported when the producer grants friend access. Full implementation metadata supports
/// contracts which use private storage or specialized accessors.
/// Generic attributes require C# 11 or later.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class GenerateSerializerContextAttribute<T> : Attribute;
