namespace Orleans.Serialization.WireProtocol
{
    /// <summary>
    /// Represents a 3-bit wire type, shifted into position 
    /// </summary>
    public enum WireType : byte
    {
        VarInt = 0b000 << 5, // Followed by a VarInt
        TagDelimited = 0b001 << 5, // Followed by field specifiers, then an Extended tag with EndTagDelimited as the extended wire type.
        LengthPrefixed = 0b010 << 5, // Followed by VarInt length representing the number of bytes which follow.
        Fixed32 = 0b011 << 5, // Followed by 4 bytes
        Fixed64 = 0b100 << 5, // Followed by 8 bytes
        Reference = 0b110 << 5, // Followed by a VarInt reference to a previously defined object. Note that the SchemaType and type specification must still be included.
        Extended = 0b111 << 5, // This is a control tag. The schema type and embedded field id are invalid. The remaining 5 bits are used for control information.
    }
}