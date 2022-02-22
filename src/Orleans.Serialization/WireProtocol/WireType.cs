namespace Orleans.Serialization.WireProtocol
{
    /// <summary>
    /// Represents a 3-bit wire type, shifted into position 
    /// </summary>
    public enum WireType : byte
    {
        /// <summary>
        /// A variable-length integer vlaue.
        /// </summary>
        /// <remarks>        
        /// Followed by a variable-length integer. 
        /// </remarks>        
        VarInt = 0b000 << 5,

        /// <summary>
        /// A compound value comprised of a collection of tag-delimited fields.
        /// </summary>
        /// <remarks>        
        /// Followed by field specifiers, then an <see cref="WireType.Extended"/> tag with <see cref="ExtendedWireType.EndTagDelimited"/> as the extended wire type. 
        /// </remarks>        
        TagDelimited = 0b001 << 5,

        /// <summary>
        /// A length-prefixed value.
        /// </summary>
        /// <remarks>        
        /// Followed by VarInt length representing the number of bytes which follow. 
        /// </remarks>        
        LengthPrefixed = 0b010 << 5,

        /// <summary>
        /// A 32-bit value.
        /// </summary>
        /// <remarks>        
        /// Followed by 4 bytes.
        /// </remarks>        
        Fixed32 = 0b011 << 5,

        /// <summary>
        /// A 64-bit value.
        /// </summary>
        /// <remarks>        
        /// Followed by 8 bytes.
        /// </remarks>        
        Fixed64 = 0b100 << 5,

        /// <summary>
        /// A reference to a previously encoded value.
        /// </summary>
        /// <remarks>        
        /// Followed by 8 bytes.
        /// </remarks>        
        Reference = 0b110 << 5, // Followed by a VarInt reference to a previously defined object. Note that the SchemaType and type specification must still be included.
        Extended = 0b111 << 5, // This is a control tag. The schema type and embedded field id are invalid. The remaining 5 bits are used for control information.
    }
}