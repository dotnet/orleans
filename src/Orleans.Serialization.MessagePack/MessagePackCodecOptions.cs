using System;
using System.Runtime.Serialization;
using MessagePack;

namespace Orleans.Serialization;

/// <summary>
/// Options for <see cref="MessagePackCodec"/>.
/// </summary>
public class MessagePackCodecOptions
{
    /// <summary>
    /// Gets or sets the <see cref="MessagePackSerializerOptions"/>.
    /// </summary>
    public MessagePackSerializerOptions SerializerOptions { get; set; } = MessagePackSerializerOptions.Standard;

    /// <summary>
    /// Get or sets flag that allows the use of <see cref="DataContractAttribute"/> marked contracts for MessagePackSerializer.
    /// </summary>
    public bool AllowDataContractAttributes { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the MessagePack serializer for serialization and deserialization.
    /// </summary>
    public Func<Type, bool?> IsSerializableType { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the MessagePack serializer for copying.
    /// </summary>
    public Func<Type, bool?> IsCopyableType { get; set; }
}