namespace Orleans.Journaling.MessagePack;

/// <summary>
/// Options for configuring MessagePack-based serialization for Orleans.Journaling.
/// </summary>
/// <remarks>
/// Configure <see cref="SerializerOptions"/> with a resolver that supports every durable value type used by the application.
/// Source-generated or statically registered resolvers are recommended for Native AOT compatibility.
/// </remarks>
public sealed class MessagePackJournalingOptions
{
    /// <summary>
    /// Gets or sets the MessagePack serializer options used for durable entry payload values.
    /// </summary>
    public global::MessagePack.MessagePackSerializerOptions SerializerOptions { get; set; } = global::MessagePack.MessagePackSerializerOptions.Standard;
}
