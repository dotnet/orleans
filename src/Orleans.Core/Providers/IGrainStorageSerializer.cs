using System;
using System.Collections.Generic;

namespace Orleans.Storage
{
    /// <summary>
    /// Common interface for grain state serializers
    /// </summary>
    public interface IGrainStorageSerializer
    {
        /// <summary>
        /// Returns the list of supported tags by the implementation
        /// </summary>
        List<string> SupportedTags { get; }

        /// <summary>
        /// Serialize the object in input
        /// </summary>
        /// <param name="t">Type of the object to serialize</param>
        /// <param name="input">Object to serialize</param>
        /// <param name="output">The serialized object will be written to this output</param>
        /// <returns>The tag used for serialization</returns>
        string Serialize(Type t, object input, out BinaryData output);

        /// <summary>
        /// Deserialize the data in input
        /// </summary>
        /// <param name="expected">Expected type of the object to deserialize</param>
        /// <param name="input">Data to deserialize</param>
        /// <param name="tag">Tag returned during serialization</param>
        /// <returns>The deserialized object</returns>
        object Deserialize(Type expected, BinaryData input, string tag);
    }

    public static class WellKnownSerializerTag
    {
        public const string Binary = "binary";
        public const string Json = "json";
        public const string Xml = "xml";
        public const string Text = "text";
    }

    public static class GrainStateSerializerExtensions
    {
        /// <summary>
        /// Serialize the object in input
        /// </summary>
        /// <param name="self">Serializer to use</param>
        /// <param name="t">Type of the object to serialize</param>
        /// <param name="input">Object to serialize</param>
        /// <returns>The serializer tag and the srialized output</returns>
        public static (string tag, ReadOnlyMemory<byte> output) Serialize(this IGrainStorageSerializer self, Type t, object input)
        {
            var tag = self.Serialize(t, input, out var output);
            return (tag, output.ToMemory());
        }

        /// <summary>
        /// Serialize the object in input
        /// </summary>
        /// <param name="self">Serializer to use</param>
        /// <param name="expected">Type of the object to deserialize</param>
        /// <param name="input">Data to deserialize</param>
        /// <param name="tag">Tag returned during serialization</param>
        /// <returns>The deserialized object</returns>
        public static object Deserialize(this IGrainStorageSerializer self, Type expected, ReadOnlyMemory<byte> input, string tag)
        {
            return self.Deserialize(expected, new BinaryData(input), tag);
        }
    }
}
