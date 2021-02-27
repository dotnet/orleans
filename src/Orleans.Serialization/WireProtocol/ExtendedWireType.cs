namespace Orleans.Serialization.WireProtocol
{
    public enum ExtendedWireType : byte
    {
        EndTagDelimited = 0b00 << 3, // This tag marks the end of a tag-delimited object.
        EndBaseFields = 0b01 << 3, // This tag marks the end of a base object in a tag-delimited object.
    }
}