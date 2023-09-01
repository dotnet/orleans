using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.Runtime
{
    [Serializable, GenerateSerializer, Immutable]
    public sealed class LegacyGrainId : IEquatable<LegacyGrainId>, IComparable<LegacyGrainId>
    {
        private static readonly Interner<UniqueKey, LegacyGrainId> grainIdInternCache = new Interner<UniqueKey, LegacyGrainId>(InternerConstants.SIZE_LARGE);
        private static readonly Interner<UniqueKey, byte[]> grainTypeInternCache = new Interner<UniqueKey, byte[]>();
        private static readonly Interner<UniqueKey, byte[]> grainKeyInternCache = new Interner<UniqueKey, byte[]>();
        private static readonly ReadOnlyMemory<byte> ClientPrefixBytes = Encoding.UTF8.GetBytes(GrainTypePrefix.ClientPrefix + ".");

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        [DataMember]
        [Id(0)]
        internal readonly UniqueKey Key;

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
            return FindOrCreateGrainId(UniqueKey.NewKey(guid));
        }

        internal static LegacyGrainId GetSystemTargetGrainId(long typeData)
        {
            return FindOrCreateGrainId(UniqueKey.NewEmptySystemTargetKey(typeData));
        }

        internal static GrainType GetGrainType(long typeCode, bool isKeyExt)
        {
            return GetGrainType(isKeyExt
                ? UniqueKey.NewKey(0, UniqueKey.Category.KeyExtGrain, typeCode, "keyext")
                : UniqueKey.NewKey(0, UniqueKey.Category.Grain, typeCode));
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
            _ = GetPrimaryKey(out key);
            return key;
        }

        public int TypeCode => Key.BaseTypeCode;

        private static GrainType GetGrainType(UniqueKey key)
        {
            return new GrainType(grainTypeInternCache.FindOrCreate(key, key =>
            {
                var prefixBytes = key.IsSystemTargetKey ? GrainTypePrefix.SystemTargetPrefixBytes
                    : key.IdCategory == UniqueKey.Category.Client ? ClientPrefixBytes
                    : GrainTypePrefix.LegacyGrainPrefixBytes;

                return CreateGrainType(prefixBytes, key.TypeCodeData);
            }));
        }

        private static byte[] CreateGrainType(ReadOnlyMemory<byte> prefixBytes, ulong typeCode)
        {
            var prefix = prefixBytes.Span;
            var buf = new byte[prefix.Length + 16];
            prefix.CopyTo(buf);
            Utf8Formatter.TryFormat(typeCode, buf.AsSpan(prefix.Length), out var len, new StandardFormat('X', 16));
            Debug.Assert(len == 16);
            return buf;
        }

        public static GrainType CreateGrainTypeForGrain(int typeCode)
        {
            return new GrainType(CreateGrainType(GrainTypePrefix.LegacyGrainPrefixBytes, (ulong)typeCode));
        }

        public static GrainType CreateGrainTypeForSystemTarget(int typeCode)
        {
            return new GrainType(CreateGrainType(GrainTypePrefix.SystemTargetPrefixBytes, (ulong)typeCode));
        }

        private IdSpan GetGrainKey()
        {
            return new IdSpan(grainKeyInternCache.FindOrCreate(Key, k => Encoding.UTF8.GetBytes($"{k.N0:X16}{k.N1:X16}{(k.HasKeyExt ? "+" : null)}{k.KeyExt}")));
        }

        public GrainId ToGrainId()
        {
            return new GrainId(GetGrainType(Key), this.GetGrainKey());
        }

        public static bool TryConvertFromGrainId(GrainId id, out LegacyGrainId legacyId)
        {
            legacyId = FromGrainIdInternal(id);
            return legacyId is not null;
        }

        public static LegacyGrainId FromGrainId(GrainId id)
        {
            return FromGrainIdInternal(id) ?? ThrowNotLegacyGrainId(id);
        }

        private static LegacyGrainId FromGrainIdInternal(GrainId id)
        {
            var typeSpan = id.Type.AsSpan();
            if (typeSpan.Length != GrainTypePrefix.LegacyGrainPrefix.Length + 16)
                return null;
            if (!typeSpan.StartsWith(GrainTypePrefix.LegacyGrainPrefixBytes.Span))
                return null;

            typeSpan = typeSpan.Slice(GrainTypePrefix.LegacyGrainPrefix.Length, 16);
            if (!Utf8Parser.TryParse(typeSpan, out ulong typeCodeData, out var len, 'X') || len < 16)
                return null;

            string keyExt = null;
            var keySpan = id.Key.Value.Span;
            if (keySpan.Length < 32) return null;

            if (!Utf8Parser.TryParse(keySpan[..16], out ulong n0, out len, 'X') || len < 16)
                return null;

            if (!Utf8Parser.TryParse(keySpan.Slice(16, 16), out ulong n1, out len, 'X') || len < 16)
                return null;

            if (keySpan.Length > 32)
            {
                if (keySpan[32] != '+') return null;
                keyExt = Encoding.UTF8.GetString(keySpan[33..]);
            }

            return FindOrCreateGrainId(UniqueKey.NewKey(n0, n1, typeCodeData, keyExt));
        }

        private static LegacyGrainId ThrowNotLegacyGrainId(GrainId id)
        {
            throw new InvalidOperationException($"Cannot convert non-legacy id {id} into legacy id");
        }

        private static LegacyGrainId FindOrCreateGrainId(UniqueKey key)
        {
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
                    idString = keyString.Substring(24, 8) + keyString[48..];
                else
                    idString = keyString.Substring(24, 8);
            }

            string fullString;
            switch (Category)
            {
                case UniqueKey.Category.Grain:
                case UniqueKey.Category.KeyExtGrain:
                    var typeString = TypeCode.ToString("X");
                    if (!detailed) typeString = typeString[Math.Max(0, typeString.Length - 8)..];
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
            return detailed ? string.Format("{0}-0x{1, 8:X8}", fullString, GetUniformHashCode()) : fullString;
        }

        public static bool IsLegacyGrainType(Type type)
        {
            return typeof(IGrainWithGuidKey).IsAssignableFrom(type)
                || typeof(IGrainWithIntegerKey).IsAssignableFrom(type)
                || typeof(IGrainWithStringKey).IsAssignableFrom(type)
                || typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(type)
                || typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(type);
        }

        public static bool IsLegacyKeyExtGrainType(Type type)
        {
            return typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(type)
                || typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(type);
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
