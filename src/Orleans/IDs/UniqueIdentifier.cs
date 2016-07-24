using System;
using Newtonsoft.Json;

namespace Orleans.Runtime
{
    [Serializable]
    internal abstract class UniqueIdentifier : IEquatable<UniqueIdentifier>, IComparable<UniqueIdentifier>
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        [JsonProperty]
        protected readonly internal UniqueKey Key;

        protected UniqueIdentifier()
        { }

        protected UniqueIdentifier(UniqueKey key)
        {
            Key = key;
        }

        public override string ToString()
        {
            return Key.ToString();
        }

        public override bool Equals(object obj)
        {
            var other = obj as UniqueIdentifier;
            return other != null && GetType() == other.GetType() && Key.Equals(other.Key);
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        public uint GetHashCode_Modulo(uint umod)
        {
            int key = Key.GetHashCode();
            int mod = (int)umod;
            key = ((key % mod) + mod) % mod; // key should be positive now. So assert with checked.
            return checked((uint)key);
        }

        #region IEquatable<UniqueIdentifier> Members

        public virtual bool Equals(UniqueIdentifier other)
        {
            return other != null && GetType() == other.GetType() && Key.Equals(other.Key);
        }

        #endregion

        #region IComparable<UniqueIdentifier> Members

        public int CompareTo(UniqueIdentifier other)
        {
            return Key.CompareTo(other.Key);
        }

        #endregion
    }
}
