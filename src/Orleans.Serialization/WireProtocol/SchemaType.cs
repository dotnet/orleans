namespace Orleans.Serialization.WireProtocol
{
    /// <summary>
    /// Identifies the runtime type (schema type) of a field.
    /// </summary>
    public enum SchemaType : uint
    {
        /// <summary>
        /// Indicates that the runtime type is the exact type expected by the current schema.
        /// </summary>
        Expected = 0b00 << 3,

        /// <summary>
        /// Indicates that the runtime type is an instance of a well-known type. Followed by a VarInt type id.
        /// </summary>
        WellKnown = 0b01 << 3,

        /// <summary>
        /// Indicates that the runtime type is encoded as a named type. Followed by an encoded type name.
        /// </summary>
        Encoded = 0b10 << 3,

        /// <summary>
        /// Indicates that the runtime type is a type which was previously specified. Followed by a VarInt indicating which previous type is being reused.
        /// </summary>
        Referenced = 0b11 << 3,
    }
}