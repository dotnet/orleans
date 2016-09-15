using System;
using System.Collections.Generic;

namespace Orleans.Serialization
{
    using System.Runtime.CompilerServices;

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

        private class ReferenceEqualsComparer : EqualityComparer<object>
        {
            /// <summary>
            /// Defines object equality by reference equality (eq, in LISP).
            /// </summary>
            /// <returns>
            /// true if the specified objects are equal; otherwise, false.
            /// </returns>
            /// <param name="x">The first object to compare.</param><param name="y">The second object to compare.</param>
            public override bool Equals(object x, object y)
            {
                return object.ReferenceEquals(x, y);
            }

            public override int GetHashCode(object obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }

        private SerializationContext()
        {
            processedObjects = new Dictionary<object, Record>(new ReferenceEqualsComparer());
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
