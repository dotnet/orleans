using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
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

    public sealed class DeserializationContext : SerializationContextBase, IDeserializationContext
    {
        private Dictionary<int, object> taggedObjects;

        public DeserializationContext(SerializationManager serializationManager) : base(serializationManager)
        {
            this.Reset();
        }

        /// <inheritdoc />
        public IBinaryTokenStreamReader StreamReader { get; set; }

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
            if (this.taggedObjects is null || this.taggedObjects.Count > this.MaxSustainedSerializationContextCapacity)
            {
                this.taggedObjects = new Dictionary<int, object>();
            }
            else
            {
                this.taggedObjects.Clear();
            }

            this.CurrentObjectOffset = 0;
        }

        public override object AdditionalContext => this.SerializationManager.RuntimeClient;

        public object DeserializeInner(Type expected)
        {
            return SerializationManager.DeserializeInner(expected, this);
        }

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
                this.CurrentObjectOffset = this.parent.CurrentObjectOffset;
            }
            
            public IServiceProvider ServiceProvider => this.parent.ServiceProvider;
            public object AdditionalContext => this.parent.AdditionalContext;
            public IBinaryTokenStreamReader StreamReader { get; }
            public int CurrentObjectOffset { get; set; }
            public int CurrentPosition => this.position + this.StreamReader.CurrentPosition;
            public void RecordObject(object obj, int offset) => this.parent.RecordObject(obj, offset);
            public void RecordObject(object obj) => this.RecordObject(obj, this.CurrentObjectOffset);
            public object FetchReferencedObject(int offset) => this.parent.FetchReferencedObject(offset);
            public object DeserializeInner(Type expected) => SerializationManager.DeserializeInner(expected, this);
        }
    }
}