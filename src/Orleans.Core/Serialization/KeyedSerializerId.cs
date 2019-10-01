using System;

namespace Orleans.Serialization
{
    /// <summary>
    /// Values for identifying <see cref="IKeyedSerializer"/> serializers.
    /// </summary>
    internal enum KeyedSerializerId : byte
    {
        /// <summary>
        /// Removed.
        /// </summary>
        [Obsolete(message: "Support for this serializer has been removed", error: true)]
        ILBasedSerializer = 1,

        /// <summary>
        /// <see cref="Orleans.Serialization.BinaryFormatterISerializableSerializer"/>
        /// </summary>
        BinaryFormatterISerializable = 2,

        /// <summary>
        /// The maximum reserved value.
        /// </summary>
        MaxReservedValue = 100,
    }
}