using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
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

        /// <inheritdoc />
        public void RecordObject(object obj)
        {
            taggedObjects[CurrentObjectOffset] = obj;
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
    }
}
