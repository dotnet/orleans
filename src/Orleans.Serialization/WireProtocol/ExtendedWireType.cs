namespace Orleans.Serialization.WireProtocol
{
    /// <summary>
    /// Represents an extended wire type
    /// </summary>
    public enum ExtendedWireType : uint
    {        
        /// <summary>
        /// Marks the end of a tag-delimited field.
        /// </summary>
        EndTagDelimited = 0b00 << 3,

        /// <summary>
        /// Marks the end of base-type fields in a tag-delimited object.
        /// </summary>
        EndBaseFields = 0b01 << 3,
    }
}