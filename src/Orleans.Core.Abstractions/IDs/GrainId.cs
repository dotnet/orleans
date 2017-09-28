using System;
using System.Globalization;
using Orleans.Core;

namespace Orleans.Runtime
{
    [Serializable]
    internal class GrainId : UniqueIdentifier, IEquatable<GrainId>, IGrainIdentity
    {
        public UniqueKey.Category Category { get { return Key.IdCategory; } }

        public bool IsSystemTarget { get { return Key.IsSystemTargetKey; } }

        public bool IsGrain { get { return Category == UniqueKey.Category.Grain || Category == UniqueKey.Category.KeyExtGrain; } }

        public bool IsClient { get { return Category == UniqueKey.Category.Client || Category == UniqueKey.Category.GeoClient; } }

        internal GrainId(UniqueKey key)
            : base(key)
        {
        }

        public Guid PrimaryKey
        {
            get { return GetPrimaryKey(); }
        }

        public long PrimaryKeyLong
        {
            get { return GetPrimaryKeyLong(); }
        }

        public string PrimaryKeyString
        {
            get { return GetPrimaryKeyString(); }
        }

        public string IdentityString
        {
            get { return ToDetailedString(); }
        }

        public bool IsLongKey
        {
            get { return Key.IsLongKey; }
        }

        public long GetPrimaryKeyLong(out string keyExt)
        {
            return Key.PrimaryKeyToLong(out keyExt);
        }

        internal long GetPrimaryKeyLong()
        {
            return Key.PrimaryKeyToLong();
        }

        public Guid GetPrimaryKey(out string keyExt)
        {
            return Key.PrimaryKeyToGuid(out keyExt);
        }

        internal Guid GetPrimaryKey()
        {
            return Key.PrimaryKeyToGuid();
        }

        internal string GetPrimaryKeyString()
        {
            string key;
            var tmp = GetPrimaryKey(out key);
            return key;
        }

        public int TypeCode => Key.BaseTypeCode;

        #region IEquatable<GrainId> Members

        public bool Equals(GrainId other)
        {
            return other != null && Key.Equals(other.Key);
        }

        #endregion

        public override bool Equals(UniqueIdentifier obj)
        {
            var o = obj as GrainId;
            return o != null && Key.Equals(o.Key);
        }

        public override bool Equals(object obj)
        {
            var o = obj as GrainId;
            return o != null && Key.Equals(o.Key);
        }

        // Keep compiler happy -- it does not like classes to have Equals(...) without GetHashCode() methods
        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        /// <summary>
        /// Get a uniformly distributed hash code value for this grain, based on Jenkins Hash function.
        /// NOTE: Hash code value may be positive or NEGATIVE.
        /// </summary>
        /// <returns>Hash code for this GrainId</returns>
        public uint GetUniformHashCode()
        {
            return Key.GetUniformHashCode();
        }

        public override string ToString()
        {
            return ToStringImpl(false);
        }

        // same as ToString, just full primary key and type code
        internal string ToDetailedString()
        {
            return ToStringImpl(true);
        }

        // same as ToString, just full primary key and type code
        private string ToStringImpl(bool detailed)
        {
            string name = string.Empty;
#if ABSTRACTIONS_TODO
            if (Constants.TryGetSystemGrainName(this, out name))
            {
                return name;
            }
#endif

            var keyString = Key.ToString();
            // this should grab the least-significant half of n1, suffixing it with the key extension.
            string idString = keyString;
            if (!detailed)
            {
                if (keyString.Length >= 48)
                    idString = keyString.Substring(24, 8) + keyString.Substring(48);
                else
                    idString = keyString.Substring(24, 8);
            }

            string fullString = null;
            switch (Category)
            {
                case UniqueKey.Category.Grain:
                case UniqueKey.Category.KeyExtGrain:
                    var typeString = TypeCode.ToString("X");
                    if (!detailed) typeString = typeString.Substring(Math.Max(0, typeString.Length - 8));
                    fullString = String.Format("*grn/{0}/{1}", typeString, idString);
                    break;
                case UniqueKey.Category.Client:
                    fullString = "*cli/" + idString;
                    break;
                case UniqueKey.Category.GeoClient:
                    fullString = string.Format("*gcl/{0}/{1}", Key.KeyExt, idString);
                    break;
                case UniqueKey.Category.SystemTarget:
#if ABSTRACTIONS_TODO
                    string explicitName = Constants.SystemTargetName(this);
                    if (TypeCode != 0)
                    {
                        var typeStr = TypeCode.ToString("X");
                        return String.Format("{0}/{1}/{2}", explicitName, typeStr, idString);
                    }
                    fullString = explicitName;
                    break;
#endif
                default:
                    fullString = "???/" + idString;
                    break;
            }
            return detailed ? String.Format("{0}-0x{1, 8:X8}", fullString, GetUniformHashCode()) : fullString;
        }

        internal string ToFullString()
        {
            string kx;
            string pks =
                Key.IsLongKey ?
                    GetPrimaryKeyLong(out kx).ToString(CultureInfo.InvariantCulture) :
                    GetPrimaryKey(out kx).ToString();
            string pksHex =
                Key.IsLongKey ?
                    GetPrimaryKeyLong(out kx).ToString("X") :
                    GetPrimaryKey(out kx).ToString("X");
            return
                String.Format(
                    "[GrainId: {0}, IdCategory: {1}, BaseTypeCode: {2} (x{3}), PrimaryKey: {4} (x{5}), UniformHashCode: {6} (0x{7, 8:X8}){8}]",
                    ToDetailedString(),                // 0
                    Category,                          // 1
                    TypeCode,                          // 2
                    TypeCode.ToString("X"),            // 3
                    pks,                               // 4
                    pksHex,                            // 5
                    GetUniformHashCode(),              // 6
                    GetUniformHashCode(),              // 7
                    Key.HasKeyExt ?  String.Format(", KeyExtension: {0}", kx) : "");   // 8
        }

        internal string ToStringWithHashCode()
        {
            return String.Format("{0}-0x{1, 8:X8}", this.ToString(), this.GetUniformHashCode()); 
        }

        /// <summary>
        /// Return this GrainId in a standard string form, suitable for later use with the <c>FromParsableString</c> method.
        /// </summary>
        /// <returns>GrainId in a standard string format.</returns>
        internal string ToParsableString()
        {
            // NOTE: This function must be the "inverse" of FromParsableString, and data must round-trip reliably.

            return Key.ToHexString();
        }
    }
}
