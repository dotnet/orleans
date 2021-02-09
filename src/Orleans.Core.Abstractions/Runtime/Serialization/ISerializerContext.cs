using System;

namespace Orleans.Serialization
{
    public interface ISerializerContext
    {
        /// <summary>
        /// Gets the service provider.
        /// </summary>
        IServiceProvider ServiceProvider { get; }
        
        /// <summary>
        /// Gets additional context associated with this instance.
        /// </summary>
        object AdditionalContext { get; }
    }

    public interface ICopyContext : ISerializerContext
    {
        /// <summary>
        /// Record an object-to-copy mapping into the current serialization context.
        /// Used for maintaining the .NET object graph during serialization operations.
        /// Used in generated code.
        /// </summary>
        /// <param name="original">Original object.</param>
        /// <param name="copy">Copy object that will be the serialized form of the original.</param>
        void RecordCopy(object original, object copy);

        object CheckObjectWhileCopying(object raw);

        object DeepCopyInner(object original);
    }

    public interface ISerializationContext : ISerializerContext
    {
        /// <summary>
        /// Gets the stream writer.
        /// </summary>
        IBinaryTokenStreamWriter StreamWriter { get; }

        /// <summary>
        /// Records the provided object at the specified offset into <see cref="StreamWriter"/>.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="offset"></param>
        void RecordObject(object original, int offset);

        int CheckObjectWhileSerializing(object raw);

        int CurrentOffset { get; }

        void SerializeInner(object obj, Type expected);
    }

    public interface IDeserializationContext : ISerializerContext
    {
        /// <summary>
        /// The stream reader.
        /// </summary>
        IBinaryTokenStreamReader StreamReader { get; }

        /// <summary>
        /// The offset of the current object in <see cref="StreamReader"/>.
        /// </summary>
        int CurrentObjectOffset { get; set; }

        /// <summary>
        /// Gets the current position in the stream.
        /// </summary>
        int CurrentPosition { get; }

        /// <summary>
        /// Records deserialization of the provided object.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset">The offset within <see cref="StreamReader"/>.</param>
        void RecordObject(object obj, int offset);

        /// <summary>
        /// Records deserialization of the provided object at the current object offset.
        /// </summary>
        /// <param name="obj"></param>
        void RecordObject(object obj);

        /// <summary>
        /// Returns the object from the specified offset.
        /// </summary>
        /// <param name="offset">The offset within <see cref="StreamReader"/>.</param>
        /// <returns>The object from the specified offset.</returns>
        object FetchReferencedObject(int offset);

        object DeserializeInner(Type expected);
    }
}