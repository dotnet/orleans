namespace Orleans.Serialization.WireProtocol
{
    public enum SchemaType : byte
    {
        Expected = 0b00 << 3, // This value has the type expected by the current schema.
        WellKnown = 0b01 << 3, // This value is an instance of a well-known type. Followed by a VarInt type id.
        Encoded = 0b10 << 3, // This value is of a named type. Followed by an encoded type name.
        Referenced = 0b11 << 3, // This value is of a type which was previously specified. Followed by a VarInt indicating which previous type is being reused.
    }
}