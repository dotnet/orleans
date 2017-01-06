using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    public interface IDeserializationContext
    {
        BinaryTokenStreamReader StreamReader { get; }
        int CurrentObjectOffset { get; set; }
        void RecordObject(object obj);
        object FetchReferencedObject(int offset);
    }

    public class DeserializationContext : IDeserializationContext
    {
        private readonly Dictionary<int, object> taggedObjects;

        public DeserializationContext()
        {
            taggedObjects = new Dictionary<int, object>();
        }

        public BinaryTokenStreamReader StreamReader { get; set; }

        internal void Reset()
        {
            taggedObjects.Clear();
            CurrentObjectOffset = 0;
        }

        public int CurrentObjectOffset { get; set; }

        public void RecordObject(object obj)
        {
            taggedObjects[CurrentObjectOffset] = obj;
        }

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
