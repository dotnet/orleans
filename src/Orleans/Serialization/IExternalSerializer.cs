using Orleans.Runtime;
using System;

namespace Orleans.Serialization
{
    /// <summary>
    /// Interface that allows third-party serializers to perform serialization, even when
    /// the types being serialized are not known (generics) at initialization time.
    /// 
    /// Types that inherit this interface are discovered through dependency injection and 
    /// automatically incorporated in the Serialization Manager.
    /// </summary>
    public interface IExternalSerializer
    {
        /// <summary>
        /// Initializes the external serializer. Called once when the serialization manager creates 
        /// an instance of this type
        /// </summary>
        void Initialize(TraceLogger logger);

        /// <summary>
        /// Informs the serialization manager whether this serializer supports the type for serialization.
        /// </summary>
        /// <param name="itemType">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the item can be serialized.</returns>
        bool IsSupportedType(Type itemType);

        /// <summary>
        /// Tries to create a copy of source.
        /// </summary>
        /// <param name="source">The item to create a copy of</param>
        /// <returns>The copy</returns>
        object DeepCopy(object source);

        /// <summary>
        /// Tries to serialize an item.
        /// </summary>
        /// <param name="item">The instance of the object being serialized</param>
        /// <param name="writer">The writer used for serialization</param>
        /// <param name="expectedType">The type that the deserializer will expect</param>
        void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType);

        /// <summary>
        /// Tries to deserialize an item.
        /// </summary>
        /// <param name="reader">The reader used for binary deserialization</param>
        /// <param name="expectedType">The type that should be deserialzied</param>
        /// <returns>The deserialized object</returns>
        object Deserialize(Type expectedType, BinaryTokenStreamReader reader);
    }
}
