using System;
using MemoryPack;

namespace Orleans.Serialization;

/// <summary>
/// Options for <see cref="MemoryPackCodec"/>.
/// </summary>
public class MemoryPackCodecOptions
{
    /// <summary>
    /// Gets or sets the <see cref="MemoryPackSerializerOptions"/>.
    /// </summary>
    public MemoryPackSerializerOptions SerializerOptions { get; set; } = MemoryPackSerializerOptions.Default;

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the MemoryPack serializer for serialization and deserialization.
    /// </summary>
    public Func<Type, bool?> IsSerializableType { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the MemoryPack serializer for copying.
    /// </summary>
    public Func<Type, bool?> IsCopyableType { get; set; }
}
