using System;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    internal readonly struct CorrelationId : IEquatable<CorrelationId>, IComparable<CorrelationId>
    {
        [Id(1)]
        private readonly long id;
        private static long nextToUse = 1;

        public CorrelationId(long value)
        {
            id = value;
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
            if (obj is not CorrelationId correlationId)
            {
                return false;
            }

            return this.Equals(correlationId);
        }

        public bool Equals(CorrelationId other)
        {
            return id == other.id;
        }

        public static bool operator ==(CorrelationId lhs, CorrelationId rhs)
        {
            return (rhs.id == lhs.id);
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
