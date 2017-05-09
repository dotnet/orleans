using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

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
    }

    public static class DeserializationContextExtensions
    {
        /// <summary>
        /// Returns a new nested context which begins at the specified position.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="position"></param>
        /// <param name="reader"></param>
        /// <returns></returns>
        public static IDeserializationContext CreateNestedContext(
            this IDeserializationContext context,
            int position,
            BinaryTokenStreamReader reader)
        {
            return new DeserializationContext.NestedDeserializationContext(context, position, reader);
        }
    }

    public class DeserializationContext : IDeserializationContext
    {
        private readonly Dictionary<int, object> taggedObjects;

        public DeserializationContext(SerializationManager serializationManager)
        {
            this.SerializationManager = serializationManager;
            this.taggedObjects = new Dictionary<int, object>();
        }

        /// <inheritdoc />
        public SerializationManager SerializationManager { get; }
        
        /// <inheritdoc />
        public BinaryTokenStreamReader StreamReader { get; set; }

        /// <inheritdoc />
        public int CurrentObjectOffset { get; set; }

        public int CurrentPosition => this.StreamReader.CurrentPosition;

        /// <inheritdoc />
        public void RecordObject(object obj)
        {
            this.RecordObject(obj, this.CurrentObjectOffset);
        }

        /// <inheritdoc />
        public void RecordObject(object obj, int offset)
        {
            taggedObjects[offset] = obj;
        }

        /// <inheritdoc />
        public object FetchReferencedObject(int offset)
        {
            object result;
            if (!taggedObjects.TryGetValue(offset, out result))
            {
                throw new SerializationException("Reference with no referred object");
            }
            return result;
        }

        internal void Reset()
        {
            this.taggedObjects.Clear();
            this.CurrentObjectOffset = 0;
        }

        public IServiceProvider ServiceProvider => this.SerializationManager.ServiceProvider;

        public object AdditionalContext => this.SerializationManager.RuntimeClient;

        internal class NestedDeserializationContext : IDeserializationContext
        {
            private readonly IDeserializationContext parent;
            private readonly int position;

            /// <summary>
            /// Initializes a new <see cref="NestedDeserializationContext"/> instance.
            /// </summary>
            /// <param name="parent"></param>
            /// <param name="position">The position, relative to the outer-most context, at which this context begins.</param>
            /// <param name="reader"></param>
            public NestedDeserializationContext(IDeserializationContext parent, int position, BinaryTokenStreamReader reader)
            {
                this.position = position;
                this.parent = parent;
                this.StreamReader = reader;
            }

            public SerializationManager SerializationManager => this.parent.SerializationManager;
            public IServiceProvider ServiceProvider => this.parent.ServiceProvider;
            public object AdditionalContext => this.parent.AdditionalContext;
            public BinaryTokenStreamReader StreamReader { get; }
            public int CurrentObjectOffset { get; set; }
            public int CurrentPosition => this.position + this.StreamReader.CurrentPosition;
            public void RecordObject(object obj, int offset) => this.parent.RecordObject(obj, offset);
            public void RecordObject(object obj) => this.RecordObject(obj, this.CurrentObjectOffset);
            public object FetchReferencedObject(int offset) => this.parent.FetchReferencedObject(offset);
        }
    }
}
