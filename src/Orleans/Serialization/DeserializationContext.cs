/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
