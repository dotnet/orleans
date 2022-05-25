using Newtonsoft.Json;
using System;

namespace Orleans.Serialization;

/// <summary>
/// Options for <see cref="NewtonsoftJsonCodec"/>.
/// </summary>
public class NewtonsoftJsonCodecOptions
{
    /// <summary>
    /// Gets or sets the <see cref="JsonSerializerSettings"/>.
    /// </summary>
    public JsonSerializerSettings SerializerSettings { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the JSON serializer for serialization and deserialization.
    /// </summary>
    public Func<Type, bool?> IsSerializableType { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the JSON serializer for copying.
    /// </summary>
    public Func<Type, bool?> IsCopyableType { get; set; }
}
