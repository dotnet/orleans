using System;

namespace Orleans.Serialization
{
    /// <summary>
    /// Values for identifying <see cref="IKeyedSerializer"/> serializers.
    /// </summary>
    internal enum KeyedSerializerId : byte
    {
        /// <summary>
        /// <see cref="Orleans.Serialization.ILBasedSerializer"/>
        /// </summary>
        ILBasedSerializer = 1,

        [Obsolete("Removed")]
        BinaryFormatterISerializable = 2,

        /// <summary>
        /// <see cref="DotNetSerializableSerializer"/>
        /// </summary>
        ISerializableSerializer = 3,

        /// <summary>
        /// The maximum reserved value.
        /// </summary>
        MaxReservedValue = 100,
    }
}