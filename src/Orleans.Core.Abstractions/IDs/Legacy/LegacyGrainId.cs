using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Core;

namespace Orleans.Runtime
{
    [Serializable]
    public class LegacyGrainId : IEquatable<LegacyGrainId>, IComparable<LegacyGrainId>, IGrainIdentity
    {
        private static readonly object lockable = new object();
        private const int INTERN_CACHE_INITIAL_SIZE = InternerConstants.SIZE_LARGE;
        private static readonly TimeSpan internCacheCleanupInterval = InternerConstants.DefaultCacheCleanupFreq;

        private static Interner<UniqueKey, LegacyGrainId> grainIdInternCache;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        [DataMember]
        protected readonly internal UniqueKey Key;

        public UniqueKey.Category Category => Key.IdCategory;

        public bool IsSystemTarget => Key.IsSystemTargetKey;

        public bool IsGrain => Category == UniqueKey.Category.Grain || Category == UniqueKey.Category.KeyExtGrain;

        public bool IsClient => Category == UniqueKey.Category.Client;

        internal LegacyGrainId(UniqueKey key)
        {
            this.Key = key;
        }

        public static implicit operator GrainId(LegacyGrainId legacy) => legacy.ToGrainId();

        public static LegacyGrainId NewId()
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(Guid.NewGuid(), UniqueKey.Category.Grain));
        }

        public static LegacyGrainId NewClientId()
        {
            return NewClientId(Guid.NewGuid());
        }

        internal static LegacyGrainId NewClientId(Guid id)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(id, UniqueKey.Category.Client, 0));
        }

        internal static LegacyGrainId GetGrainId(UniqueKey key)
        {
            return FindOrCreateGrainId(key);
        }

        // For testing only.
        internal static LegacyGrainId GetGrainIdForTesting(Guid guid)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(guid, UniqueKey.Category.None));
        }

        internal static LegacyGrainId GetSystemTargetGrainId(long typeData)
        {
            return FindOrCreateGrainId(UniqueKey.NewSystemTargetKey(Guid.Empty, typeData));
        }

        internal static LegacyGrainId GetGrainId(long typeCode, long primaryKey, string keyExt = null)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(primaryKey,
                keyExt == null ? UniqueKey.Category.Grain : UniqueKey.Category.KeyExtGrain,
                typeCode, keyExt));
        }

        internal static LegacyGrainId GetGrainId(long typeCode, Guid primaryKey, string keyExt = null)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(primaryKey,
                keyExt == null ? UniqueKey.Category.Grain : UniqueKey.Category.KeyExtGrain,
                typeCode, keyExt));
        }

        internal static LegacyGrainId GetGrainId(long typeCode, string primaryKey)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(0L,
                UniqueKey.Category.KeyExtGrain,
                typeCode, primaryKey));
        }

        internal static LegacyGrainId GetGrainServiceGrainId(short id, int typeData)
        {
            return FindOrCreateGrainId(UniqueKey.NewGrainServiceKey(id, typeData));
        }

        internal static LegacyGrainId GetGrainServiceGrainId(int typeData, string systemGrainId)
        {
            return FindOrCreateGrainId(UniqueKey.NewGrainServiceKey(systemGrainId, typeData));
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

        private GrainType GetGrainType()
        {
            // TODO: intern
            var key = this.Key;
            if (this.IsSystemTarget)
            {
                return GrainType.Create($"{GrainTypePrefix.SystemTargetPrefix}{key.TypeCodeData:X16}");
            }
            else if (this.IsClient)
            {
                return GrainType.Create($"{GrainTypePrefix.ClientPrefix}.{key.TypeCodeData:X16}");
            }
            else
            {
                return GrainType.Create($"{GrainTypePrefix.LegacyGrainPrefix}{key.TypeCodeData:X16}");
            }
        }

        public static GrainType CreateGrainTypeForGrain(int typeCode)
        {
            return GrainType.Create($"{GrainTypePrefix.LegacyGrainPrefix}{typeCode:X16}");
        }

        public static GrainType CreateGrainTypeForSystemTarget(int typeCode)
        {
            return GrainType.Create($"{GrainTypePrefix.SystemTargetPrefix}{typeCode:X16}");
        }

        private IdSpan GetGrainKey()
        {
            // TODO: intern
            var key = this.Key;
            return IdSpan.Create($"{key.N0:X16}{key.N1:X16}{(key.HasKeyExt ? ("+" + key.KeyExt) : string.Empty)}");
        }

        public GrainId ToGrainId()
        {
            var id = GrainId.Create(this.GetGrainType(), this.GetGrainKey());
            return id;
        }

        public static bool TryConvertFromGrainId(GrainId id, out LegacyGrainId legacyId)
        {
            legacyId = FromGrainIdInternal(id);
            return legacyId is object;
        }

        public static unsafe LegacyGrainId FromGrainId(GrainId id)
        {
            return FromGrainIdInternal(id) ?? ThrowNotLegacyGrainId(id);
        }

        private static unsafe LegacyGrainId FromGrainIdInternal(GrainId id)
        {
            var typeSpan = id.Type.AsSpan();
            int prefixLength;
            if (typeSpan.StartsWith(GrainTypePrefix.LegacyGrainPrefixBytes.Span))
            {
                prefixLength = GrainTypePrefix.LegacyGrainPrefixBytes.Length;
            }
            else if (typeSpan.StartsWith(GrainTypePrefix.SystemTargetPrefixBytes.Span))
            {
                prefixLength = GrainTypePrefix.SystemTargetPrefixBytes.Length;
            }
            else if (typeSpan.StartsWith(GrainTypePrefix.ClientPrefixBytes.Span))
            {
                return null;
            }
            else
            {
                return null;
            }

            ulong typeCodeData;
            var typeCodeSlice = typeSpan.Slice(prefixLength);
            fixed (byte* typeCodeBytes = typeCodeSlice)
            {
                // TODO: noalloc
                var typeCodeString = Encoding.UTF8.GetString(typeCodeBytes, 16);
                if (!ulong.TryParse(typeCodeString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out typeCodeData))
                {
                    return null;
                }
            }

            ulong n0, n1;
            string keyExt;
            var keySpan = id.Key.Value.Span;
            fixed (byte* idBytes = keySpan)
            {
                const int fieldLength = 16;

                // TODO: noalloc
                var n0String = Encoding.UTF8.GetString(idBytes, fieldLength);
                if (!ulong.TryParse(n0String, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n0))
                {
                    return null;
                }

                // TODO: noalloc
                var n1String = Encoding.UTF8.GetString(idBytes + fieldLength, fieldLength);
                if (!ulong.TryParse(n1String, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n1))
                {
                    return null;
                }

                const int keySpanPrefixLength = fieldLength + fieldLength + 1;
                if (keySpan.Length > keySpanPrefixLength && idBytes[keySpanPrefixLength - 1] == (byte)'+')
                {
                    // Take every byte after the '+' and interpret it as UTF8
                    keyExt = Encoding.UTF8.GetString(idBytes + keySpanPrefixLength, keySpan.Length - keySpanPrefixLength);
                }
                else
                {
                    keyExt = default;
                }
            }

            return new LegacyGrainId(UniqueKey.NewKey(n0, n1, typeCodeData, keyExt));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LegacyGrainId ThrowNotLegacyGrainId(GrainId id)
        {
            throw new InvalidOperationException($"Cannot convert non-legacy id {id} into legacy id");
        }

        private static LegacyGrainId FindOrCreateGrainId(UniqueKey key)
        {
            // Note: This is done here to avoid a wierd cyclic dependency / static initialization ordering problem involving the GrainId, Constants & Interner classes
            if (grainIdInternCache != null) return grainIdInternCache.FindOrCreate(key, k => new LegacyGrainId(k));

            lock (lockable)
            {
                if (grainIdInternCache == null)
                {
                    grainIdInternCache = new Interner<UniqueKey, LegacyGrainId>(INTERN_CACHE_INITIAL_SIZE, internCacheCleanupInterval);
                }
            }
            return grainIdInternCache.FindOrCreate(key, k => new LegacyGrainId(k));
        }

        public bool Equals(LegacyGrainId other)
        {
            return other != null && Key.Equals(other.Key);
        }

        public override bool Equals(object obj)
        {
            var o = obj as LegacyGrainId;
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
        /// <returns>Hash code for this LegacyGrainId</returns>
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
            // TODO Get name of system/target grain + name of the grain type

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
                    fullString = $"*grn/{typeString}/{idString}";
                    break;
                case UniqueKey.Category.Client:
                    fullString = $"*cli/{idString}";
                    break;
                case UniqueKey.Category.SystemTarget:
                case UniqueKey.Category.KeyExtSystemTarget:
                    fullString = $"*stg/{Key.N1}/{idString}";
                    break;
                case UniqueKey.Category.SystemGrain:
                    fullString = $"*sgn/{Key.PrimaryKeyToGuid()}/{idString}";
                    break;
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
                    "[LegacyGrainId: {0}, IdCategory: {1}, BaseTypeCode: {2} (x{3}), PrimaryKey: {4} (x{5}), UniformHashCode: {6} (0x{7, 8:X8}){8}]",
                    ToDetailedString(),                // 0
                    Category,                          // 1
                    TypeCode,                          // 2
                    TypeCode.ToString("X"),            // 3
                    pks,                               // 4
                    pksHex,                            // 5
                    GetUniformHashCode(),              // 6
                    GetUniformHashCode(),              // 7
                    Key.HasKeyExt ? String.Format(", KeyExtension: {0}", kx) : "");   // 8
        }

        public static bool IsLegacyGrainType(Type type)
        {
            return typeof(IGrainWithGuidKey).IsAssignableFrom(type)
                || typeof(IGrainWithIntegerKey).IsAssignableFrom(type)
                || typeof(IGrainWithStringKey).IsAssignableFrom(type)
                || typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(type)
                || typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(type);
        }

        internal string ToStringWithHashCode()
        {
            return String.Format("{0}-0x{1, 8:X8}", this.ToString(), this.GetUniformHashCode());
        }

        /// <summary>
        /// Return this LegacyGrainId in a standard string form, suitable for later use with the <c>FromParsableString</c> method.
        /// </summary>
        /// <returns>LegacyGrainId in a standard string format.</returns>
        internal string ToParsableString()
        {
            // NOTE: This function must be the "inverse" of FromParsableString, and data must round-trip reliably.

            return Key.ToHexString();
        }

        /// <summary>
        /// Return this LegacyGrainId in a standard components form, suitable for later use with the <see cref="FromKeyInfo"/> method.
        /// </summary>
        /// <returns>LegacyGrainId in a standard components form.</returns>
        internal (ulong, ulong, ulong, string) ToKeyInfo()
        {
            return (Key.N0, Key.N1, Key.TypeCodeData, Key.KeyExt);
        }

        /// <summary>
        /// Create a new LegacyGrainId object by parsing string in a standard form returned from <c>ToParsableString</c> method.
        /// </summary>
        /// <param name="grainId">String containing the LegacyGrainId info to be parsed.</param>
        /// <returns>New LegacyGrainId object created from the input data.</returns>
        internal static LegacyGrainId FromParsableString(string grainId)
        {
            return FromParsableString(grainId.AsSpan());
        }

        /// <summary>
        /// Create a new LegacyGrainId object by parsing string in a standard form returned from <c>ToParsableString</c> method.
        /// </summary>
        /// <param name="grainId">String containing the LegacyGrainId info to be parsed.</param>
        /// <returns>New LegacyGrainId object created from the input data.</returns>
        internal static LegacyGrainId FromParsableString(ReadOnlySpan<char> grainId)
        {
            // NOTE: This function must be the "inverse" of ToParsableString, and data must round-trip reliably.

            var key = UniqueKey.Parse(grainId);
            return FindOrCreateGrainId(key);
        }

        /// <summary>
        /// Create a new LegacyGrainId object by parsing components returned form <see cref="ToKeyInfo"/>.
        /// </summary>
        /// <param name="grainId">Components containing the LegacyGrainId to be parsed.</param>
        /// <returns>New LegacyGrainId object created from the input data.</returns>
        internal static LegacyGrainId FromKeyInfo((ulong, ulong, ulong, string) grainId)
        {
            // NOTE: This function must be the "inverse" of ToKeyInfo, and data must round-trip reliably.

            var (n0, n1, typeCodeData, keyExt) = grainId;
            var key = UniqueKey.NewKey(n0, n1, typeCodeData, keyExt);
            return FindOrCreateGrainId(key);
        }

        public uint GetHashCode_Modulo(uint umod)
        {
            int key = Key.GetHashCode();
            int mod = (int)umod;
            key = ((key % mod) + mod) % mod; // key should be positive now. So assert with checked.
            return checked((uint)key);
        }

        public int CompareTo(LegacyGrainId other)
        {
            return Key.CompareTo(other.Key);
        }
    }
}
