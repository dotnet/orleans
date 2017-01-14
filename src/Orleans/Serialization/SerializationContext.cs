using System;
using System.Collections.Generic;

namespace Orleans.Serialization
{
    public interface ICopyContext
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

    public interface ISerializationContext
    {
        BinaryTokenStreamWriter StreamWriter { get; }
        void RecordObject(object original);
        int CheckObjectWhileSerializing(object raw);
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

        public BinaryTokenStreamWriter StreamWriter { get; set; }

        private readonly Dictionary<object, Record> processedObjects;

        public SerializationContext()
        {
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

        public void RecordObject(object original)
        {
            processedObjects[original] = new Record(this.StreamWriter.CurrentOffset);
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
    }
}
