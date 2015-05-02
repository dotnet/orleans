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
using Orleans.Concurrency;
using Orleans.Serialization;
using System.Runtime.Serialization;

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
            return guidIdInternCache.Value.FindOrCreate(guid, () => new GuidId(guid));
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
            return this.Guid.ToString().Substring(0, 8);
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