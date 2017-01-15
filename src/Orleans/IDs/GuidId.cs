using System;
using System.Runtime.Serialization;
using Orleans.Concurrency;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Wrapper object around Guid.
    /// Can be used in places where Guid is optional and in those cases it can be set to null and will not use the storage of an empty Guid struct.
    /// </summary>
    [Serializable]
    [Immutable]
    public sealed class GuidId : IEquatable<GuidId>, IComparable<GuidId>, ISerializable
    {
        private static readonly Lazy<Interner<Guid, GuidId>> guidIdInternCache = new Lazy<Interner<Guid, GuidId>>(
                    () => new Interner<Guid, GuidId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

        public readonly Guid Guid;

        // TODO: Need to integrate with Orleans serializer to really use Interner.
        private GuidId(Guid guid)
        {
            this.Guid = guid;
        }

        public static GuidId GetNewGuidId()
        {
            return FindOrCreateGuidId(Guid.NewGuid());
        }

        public static GuidId GetGuidId(Guid guid)
        {
            return FindOrCreateGuidId(guid);
        }

        private static GuidId FindOrCreateGuidId(Guid guid)
        {
            return guidIdInternCache.Value.FindOrCreate(guid, g => new GuidId(g));
        }

        #region IComparable<GuidId> Members

        public int CompareTo(GuidId other)
        {
            return this.Guid.CompareTo(other.Guid);
        }

        #endregion

        #region IEquatable<GuidId> Members

        public bool Equals(GuidId other)
        {
            return other != null && this.Guid.Equals(other.Guid);
        }

        #endregion

        public override bool Equals(object obj)
        {
            return this.Equals(obj as GuidId);
        }

        public override int GetHashCode()
        {
            return this.Guid.GetHashCode();
        }

        public override string ToString()
        {
            return this.Guid.ToString();
        }

        internal string ToDetailedString()
        {
            return this.Guid.ToString();
        }

        public string ToParsableString()
        {
            return Guid.ToString();
        }

        public static GuidId FromParsableString(string guidId)
        {
            Guid id = System.Guid.Parse(guidId);
            return GetGuidId(id);
        }

        public void SerializeToStream(BinaryTokenStreamWriter stream)
        {
            stream.Write(this.Guid);
        }

        internal static GuidId DeserializeFromStream(BinaryTokenStreamReader stream)
        {
            Guid guid = stream.ReadGuid();
            return GuidId.GetGuidId(guid);
        }

        #region Operators

        public static bool operator ==(GuidId a, GuidId b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (ReferenceEquals(a, null)) return false;
            if (ReferenceEquals(b, null)) return false;
            return a.Guid.Equals(b.Guid);
        }

        public static bool operator !=(GuidId a, GuidId b)
        {
            return !(a == b);
        }

        #endregion

        #region ISerializable Members

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Guid", Guid, typeof(Guid));
        }

        // The special constructor is used to deserialize values. 
        private GuidId(SerializationInfo info, StreamingContext context)
        {
            Guid = (Guid) info.GetValue("Guid", typeof(Guid));
        }

        #endregion
    }
}
