using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    public class DeserializationContext
    {
        [ThreadStatic]
        private static DeserializationContext ctx;

        public static DeserializationContext Current
        {
            get { return ctx ?? (ctx = new DeserializationContext()); }
        }

        private readonly Dictionary<int, object> taggedObjects;

        private DeserializationContext()
        {
            taggedObjects = new Dictionary<int, object>();
        }

        internal void Reset()
        {
            taggedObjects.Clear();
            CurrentObjectOffset = 0;
        }

        internal int CurrentObjectOffset { get; set; }

        internal void RecordObject(int offset, object obj)
        {
            taggedObjects[offset] = obj;
        }

        public void RecordObject(object obj)
        {
            taggedObjects[CurrentObjectOffset] = obj;
        }

        internal object FetchReferencedObject(int offset)
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
