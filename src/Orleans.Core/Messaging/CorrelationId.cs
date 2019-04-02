using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class CorrelationId : IEquatable<CorrelationId>, IComparable<CorrelationId>
    {
        private readonly long id;
        private static long nextToUse = 1;

        internal CorrelationId(long value)
        {
            id = value;
        }

        internal CorrelationId() : this(0)
        {
        }

        public CorrelationId(CorrelationId other)
        {
            id = other.id;
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
            return rhs.id != lhs.id;
        }

        public int CompareTo(CorrelationId other)
        {
            return id.CompareTo(other.id);
        }

        public override string ToString()
        {
            return id.ToString();
        }

        internal long ToInt64() => this.id;
    }
}
