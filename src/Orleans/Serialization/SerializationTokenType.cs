namespace Orleans.Serialization
{
    internal enum SerializationTokenType : byte
    {
        #region Special values

        Null = 0,
        Reference = 1,      // Followed by uint byte offset from stream start of referred-to object
        Fallback = 2,       // .NET-serialized; followed by a ushort length and the serialized bytes
        True = 3,
        False = 4,
        
        #endregion

        // Type definers

        // Core types
        Boolean = 10,       // Only appears in generic type definitions
        Int = 11,           // Followed by a 4-byte int
        Short = 12,         // Followed by a 2-byte short
        Long = 13,          // Followed by an 8-byte long
        Sbyte = 14,         // Followed by a signed byte
        Uint = 15,          // Followed by a 4-byte uint
        Ushort = 16,        // Followed by a 2-byte ushort
        Ulong = 17,         // Followed by an 8-byte ulong
        Byte = 18,          // Followed by a byte
        Float = 19,         // Followed by a 4-byte single-precision float
        Double = 20,        // Followed by an 8-byte double-precision float
        Decimal = 21,       // Followed by a 16-byte decimal
        String = 22,        // Followed by a 4-byte length and the UTF-8 encoding of the string
        Character = 23,     // Followed by a 2-byte UTF-16 character
        Guid = 24,          // Followed by a 16-byte GUID
        Date = 25,          // Followed by a long tick count (in UTC, not local time)
        TimeSpan = 26,      // Followed by a long tick count
        IpAddress = 27,     // Followed by a 16-byte IPv6 address or 12 bytes of zeroes followed by a 4-byte IPv4 address (IPv4-compatible IPv6 address)
        IpEndPoint = 28,    // Followed by a 16-byte IP address and then a 4-byte int port number
        Object = 29,        // Followed by nothing

        // Orleans types
        GrainId = 40,       // Followed by UniqueKey
        ActivationId = 41,  // Followed by UniqueKey
        SiloAddress = 42,   // Followed by an IP endpoint and an int (epoch) 
        ActivationAddress = 43, // Followed by a grain id, an activation id, and a silo address, in that order
        CorrelationId = 44, // Followed by a long
        RequestId = 45,     // Followed by UniqueKey
        // 47 is no longer used
        Request = 48,       // Followed by the integer interface ID, the integer method ID, the integer argument count, and the arguments
        Response = 49,      // Followed by either the exception or the result
        StringObjDict = 50, // Followed by the integer count, and a sequence of string/serialized object pairs; optimization for message headers
        ObjList = 51,       // Followed by the integer count, and a sequence of serialized objects; optimization for message headers

        // Explicit types
        SpecifiedType = 97, // Followed by the type token, possibly plus generic arguments, or NamedType and the type name
        NamedType = 98,     // Followed by the type name as a string
        ExpectedType = 99,  // Indicates that the type can be deduced and is what can be deduced

        // Generic types and collections
        Tuple = 200,        // Add the count of items to this, followed by that many generic types, then the items
        Array = 210,        // Add the number of dimensions to this, followed by the element type, then the dimension sizes as ints, then the elements
        List = 220,         // Followed by the generic type, then the element count, then the elements
        Dictionary = 221,   // Followed by the generic key type, then the generic value type, then the comparer, then the pair count, 
                            // then the elements as a sequence of key, then corresponding value, then key, then value...
        KeyValuePair = 222, // Followed by the generic key type, then the generic value type, then the key, then the value
        Set = 223,          // Followed by the generic element type, then the comparer, then the element count, then the elements
        SortedList = 224,   // Followed by the generic type, then the comparer, then the element count, then the elements
        SortedSet = 225,    // Followed by the generic type, then the comparer, then the element count, then the elements
        Stack = 226,        // Followed by the generic type, then the element count, then the elements
        Queue = 227,        // Followed by the generic type, then the element count, then the elements
        LinkedList = 228,   // Followed by the generic type, then the element count, then the elements
        Nullable = 229,     // Followed by the generic type, then either Null or the value

        // Optimized arrays
        ByteArray = 240,    // Single-dimension only; followed by the count of elements, then the elements
        ShortArray = 241,   // Single-dimension only; followed by the count of elements, then the elements
        IntArray = 242,     // Single-dimension only; followed by the count of elements, then the elements
        LongArray = 243,    // Single-dimension only; followed by the count of elements, then the elements
        UShortArray = 244,  // Single-dimension only; followed by the count of elements, then the elements
        UIntArray = 245,    // Single-dimension only; followed by the count of elements, then the elements
        ULongArray = 246,   // Single-dimension only; followed by the count of elements, then the elements
        CharArray = 247,    // Single-dimension only; followed by the count of elements, then the elements
        FloatArray = 248,   // Single-dimension only; followed by the count of elements, then the elements
        DoubleArray = 249,  // Single-dimension only; followed by the count of elements, then the elements
        BoolArray = 250,    // Single-dimension only; followed by the count of elements, then the elements
        SByteArray = 251,    // Single-dimension only; followed by the count of elements, then the elements

        // Last but not least...
        Error = 255,
    }
}
