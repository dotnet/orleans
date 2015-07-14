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

namespace Orleans.Runtime
{
    [Serializable]
    internal class CorrelationId : IEquatable<CorrelationId>, IComparable<CorrelationId>
    {
        public const int SIZE_BYTES = 8;

        private readonly long id;
        private static long nextToUse = 1;

        internal CorrelationId(long value)
        {
            id = value;
        }

        internal CorrelationId(string s)
        {
            id = Int64.Parse(s);
        }

        public CorrelationId()
        {
            id = 0;
        }

        public CorrelationId(CorrelationId other)
        {
            id = other.id;
        }

        internal CorrelationId(byte[] a)
        {
            id = BitConverter.ToInt64(a, 0);
        }

        public static CorrelationId GetNext()
        {
            long val = System.Threading.Interlocked.Increment(ref nextToUse);
            return new CorrelationId(val);
        }

        public override int GetHashCode()
        {
 	        return id.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            if (!(obj is CorrelationId))
            {
                return false;
            }
            return this.Equals((CorrelationId)obj);
        }

        public bool Equals(CorrelationId other)
        {
            return !ReferenceEquals(other, null) && (id == other.id);
        }

        public static bool operator ==(CorrelationId lhs, CorrelationId rhs)
        {
            if (ReferenceEquals(lhs, null))
            {
                return ReferenceEquals(rhs, null);
            }
            else if (ReferenceEquals(rhs, null))
            {
                return false;
            }
            else
            {
                return (rhs.id == lhs.id);
            }
        }

        public static bool operator !=(CorrelationId lhs, CorrelationId rhs)
        {
            return (rhs.id != lhs.id);
        }

        public int CompareTo(CorrelationId other)
        {
            return id.CompareTo(other.id);
        }

        public override string ToString()
        {
            return id.ToString();
        }

        internal byte[] ToByteArray()
        {
            return BitConverter.GetBytes(id);
        }
    }
}