namespace Orleans.Serialization
{
    public interface IDeserializationContext : ISerializerContext
    {
        /// <summary>
        /// The stream reader.
        /// </summary>
        BinaryTokenStreamReader StreamReader { get; }
        
        /// <summary>
        /// The offset of the current object in <see cref="StreamReader"/>.
        /// </summary>
        int CurrentObjectOffset { get; set; }

        /// <summary>
        /// Records deserialization of the provided object.
        /// </summary>
        /// <param name="obj"></param>
        void RecordObject(object obj);

        /// <summary>
        /// Returns the object from the specified offset.
        /// </summary>
        /// <param name="offset">The offset within <see cref="StreamReader"/>.</param>
        /// <returns>The object from the specified offset.</returns>
        object FetchReferencedObject(int offset);
    }
}