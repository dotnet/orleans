using System.Collections.Generic;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    public interface IDeserializationContext
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

        /// <summary>
        /// Gets the <see cref="IGrainFactory"/> associated with this instance.
        /// </summary>
        IGrainFactory GrainFactory { get; }
    }

    public class DeserializationContext : IDeserializationContext
    {
        private readonly Dictionary<int, object> taggedObjects;
        private IGrainFactory grainFactory;

        public DeserializationContext(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
            taggedObjects = new Dictionary<int, object>();
        }

        internal void Reset()
        {
            taggedObjects.Clear();
            CurrentObjectOffset = 0;
        }
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
        public IGrainFactory GrainFactory => this.grainFactory ?? (this.grainFactory =  RuntimeClient.Current?.InternalGrainFactory);

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
    }
}
