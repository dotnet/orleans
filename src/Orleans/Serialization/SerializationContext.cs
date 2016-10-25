using System;
using System.Collections.Generic;

namespace Orleans.Serialization
{
    /// <summary>
    /// Maintains context information for current thread during serialization operations.
    /// </summary>
    /// <remarks>
    /// DeepCopier functions in Orleans generated code use the RecordObject method to 
    /// record the mapping of original object to the copied instance of that object
    /// so that object identity can be preserved when serializing .NET object graphs.
    /// </remarks>
    public class SerializationContext
    {
        [ThreadStatic]
        private static SerializationContext ctx;

        /// <summary>
        /// The current serialization context in use for this thread.
        /// Used in generated code.
        /// </summary>
        public static SerializationContext Current {
            get { return ctx ?? (ctx = new SerializationContext()); }
        }

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

        private readonly Dictionary<object, Record> processedObjects;

        private readonly Dictionary<Type, short> processedTypes;

        private SerializationContext()
        {
            processedObjects = new Dictionary<object, Record>(new ReferenceEqualsComparer());
            processedTypes = new Dictionary<Type, short>();
        }

        internal void Reset()
        {
            processedObjects.Clear();
            processedTypes.Clear();
            this.nextTypeIndex = 0;
        }

        /// <summary>
        /// Record an object-to-copy mapping into the current serialization context.
        /// Used for maintaining the .NET object graph during serialization operations.
        /// Used in generated code.
        /// </summary>
        /// <param name="original">Original object.</param>
        /// <param name="copy">Copy object that will be the serialized form of the original.</param>
        public void RecordObject(object original, object copy)
        {
            if (!processedObjects.ContainsKey(original))
            {
                processedObjects[original] = new Record(copy);                
            }
        }

        internal void RecordObject(object original, int offset)
        {
            processedObjects[original] = new Record(offset);
        }

        private short nextTypeIndex;

        internal short CheckTypeWhileSerializing(Type type)
        {
            short typeIndex;
            if (!processedTypes.TryGetValue(type, out typeIndex)) typeIndex = -1;
            
            return typeIndex;
        }

        internal void RecordType(Type type)
        {
            this.processedTypes[type] = this.nextTypeIndex++;
        }

        // Returns an object suitable for insertion if this is a back-reference, or null if it's new
        internal object CheckObjectWhileCopying(object raw)
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
        internal int CheckObjectWhileSerializing(object raw)
        {
            Record record;
            bool found = processedObjects.TryGetValue(raw, out record);
            if (found)
            {
                return record.Offset;
            }

            return -1;
        }
    }
}
