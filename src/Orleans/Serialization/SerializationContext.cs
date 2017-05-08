using System;
using System.Collections.Generic;

namespace Orleans.Serialization
{
    public interface ISerializerContext
    {
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        SerializationManager SerializationManager { get; }

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
    }

    public interface ISerializationContext : ISerializerContext
    {
        /// <summary>
        /// Gets the stream writer.
        /// </summary>
        BinaryTokenStreamWriter StreamWriter { get; }
        
        /// <summary>
        /// Records the provided object at the specified offset into <see cref="StreamWriter"/>.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="offset"></param>
        void RecordObject(object original, int offset);

        int CheckObjectWhileSerializing(object raw);

        int CurrentOffset { get; }
    }

    public static class SerializationContextExtensions
    {
        public static void RecordObject(this ISerializationContext context, object original)
        {
            context.RecordObject(original, context.CurrentOffset);
        }

        public static ISerializationContext CreateNestedContext(
            this ISerializationContext context,
            int position,
            BinaryTokenStreamWriter writer)
        {
            return new SerializationContext.NestedSerializationContext(context, position, writer);
        }
    }

    /// <summary>
    /// Maintains context information for current thread during serialization operations.
    /// </summary>
    /// <remarks>
    /// DeepCopier functions in Orleans generated code use the RecordObject method to 
    /// record the mapping of original object to the copied instance of that object
    /// so that object identity can be preserved when serializing .NET object graphs.
    /// </remarks>
    public class SerializationContext : ICopyContext, ISerializationContext
    {
        private struct Record
        {
            public readonly object Copy;
            public readonly int Offset;

            public Record(object copy)
            {
                Copy = copy;
                Offset = 0;
            }

            public Record(int offset)
            {
                Copy = null;
                Offset = offset;
            }
        }

        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; }

        public BinaryTokenStreamWriter StreamWriter { get; set; }

        private readonly Dictionary<object, Record> processedObjects;

        public SerializationContext(SerializationManager serializationManager)
        {
            this.SerializationManager = serializationManager;
            processedObjects = new Dictionary<object, Record>(ReferenceEqualsComparer.Instance);
        }

        internal void Reset()
        {
            processedObjects.Clear();
        }

        /// <summary>
        /// Record an object-to-copy mapping into the current serialization context.
        /// Used for maintaining the .NET object graph during serialization operations.
        /// Used in generated code.
        /// </summary>
        /// <param name="original">Original object.</param>
        /// <param name="copy">Copy object that will be the serialized form of the original.</param>
        public void RecordCopy(object original, object copy)
        {
            if (!processedObjects.ContainsKey(original))
            {
                processedObjects[original] = new Record(copy);                
            }
        }

        public void RecordObject(object original, int offset)
        {
            processedObjects[original] = new Record(offset);
        }

        // Returns an object suitable for insertion if this is a back-reference, or null if it's new
        public object CheckObjectWhileCopying(object raw)
        {
            Record record;
            bool found = processedObjects.TryGetValue(raw, out record);
            if (found)
            {
                return record.Copy;
            }

            return null;
        }

        // Returns an offset where the first version of this object was seen, or -1 if it's new
        public int CheckObjectWhileSerializing(object raw)
        {
            Record record;
            bool found = processedObjects.TryGetValue(raw, out record);
            if (found)
            {
                return record.Offset;
            }

            return -1;
        }

        public int CurrentOffset => this.StreamWriter.CurrentOffset;

        public IServiceProvider ServiceProvider => this.SerializationManager.ServiceProvider;

        public object AdditionalContext => this.SerializationManager.RuntimeClient;

        internal class NestedSerializationContext : ISerializationContext
        {
            private readonly int initialOffset;
            private readonly ISerializationContext parentContext;

            /// <summary>
            /// Creates a new instance of the <see cref="NestedSerializationContext"/> class.
            /// </summary>
            /// <param name="parent">The parent context.</param>
            /// <param name="offset">The absolute offset at which this stream begins.</param>
            /// <param name="writer">The writer.</param>
            public NestedSerializationContext(ISerializationContext parent, int offset, BinaryTokenStreamWriter writer)
            {
                this.parentContext = parent;
                this.initialOffset = offset;
                this.StreamWriter = writer;
            }

            public SerializationManager SerializationManager => this.parentContext.SerializationManager;
            public IServiceProvider ServiceProvider => this.parentContext.ServiceProvider;
            public object AdditionalContext => this.parentContext.ServiceProvider;
            public BinaryTokenStreamWriter StreamWriter { get; }
            public int CurrentOffset => this.initialOffset + this.StreamWriter.CurrentOffset;
            public void RecordObject(object original, int offset) => this.parentContext.RecordObject(original, offset);
            public int CheckObjectWhileSerializing(object raw) => this.parentContext.CheckObjectWhileSerializing(raw);
        }
    }
}
