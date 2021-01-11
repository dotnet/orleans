using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [Serializable, Immutable]
    public sealed class UniqueKey : IComparable<UniqueKey>, IEquatable<UniqueKey>
    {
        /// <summary>
        /// Type id values encoded into UniqueKeys
        /// </summary>
        public enum Category : byte
        {
            None = 0,
            SystemTarget = 1,
            SystemGrain = 2,
            Grain = 3,
            Client = 4,
            KeyExtGrain = 6,
            // 7 was GeoClient 
            KeyExtSystemTarget = 8,
        }

        public UInt64 N0 { get; private set; }
        public UInt64 N1 { get; private set; }
        public UInt64 TypeCodeData { get; private set; }
        public string KeyExt { get; private set; }

        [NonSerialized]
        private uint uniformHashCache;

        public int BaseTypeCode => (int)TypeCodeData;

        public Category IdCategory => GetCategory(TypeCodeData);

        public bool IsLongKey => N0 == 0;

        public bool IsSystemTargetKey
            => IsSystemTarget(IdCategory);

        private static bool IsSystemTarget(Category category)
            => category == Category.SystemTarget || category == Category.KeyExtSystemTarget;

        public bool HasKeyExt => IsKeyExt(IdCategory);

        private static bool IsKeyExt(Category category)
            => category == Category.KeyExtGrain || category == Category.KeyExtSystemTarget;

        internal static readonly UniqueKey Empty = new UniqueKey();

        internal static UniqueKey Parse(ReadOnlySpan<char> input)
        {
            const int minimumValidKeyLength = 48;
            input = input.Trim();
            if (input.Length >= minimumValidKeyLength)
            {
                var n0 = ulong.Parse(input.Slice(0, 16).ToString(), NumberStyles.AllowHexSpecifier);
                var n1 = ulong.Parse(input.Slice(16, 16).ToString(), NumberStyles.AllowHexSpecifier);
                var typeCodeData = ulong.Parse(input.Slice(32, 16).ToString(), NumberStyles.AllowHexSpecifier);
                string keyExt = null;
                if (input.Length > minimumValidKeyLength)
                {
                    if (input[48] != '+') throw new InvalidDataException("UniqueKey hex string missing + separator.");
                    keyExt = input.Slice(49).ToString();
                }

                return NewKey(n0, n1, typeCodeData, keyExt);
            }

            // last, for convenience we attempt to parse the string using GUID syntax. this is needed by unit
            // tests but i don't know if it's needed for production.
            return NewKey(Guid.Parse(input.ToString()));
        }

        internal static UniqueKey NewKey(ulong n0, ulong n1, Category category, long typeData, string keyExt)
            => NewKey(n0, n1, GetTypeCodeData(category, typeData), keyExt);

        internal static UniqueKey NewKey(long longKey, Category category = Category.None, long typeData = 0, string keyExt = null)
        {
            ThrowIfIsSystemTargetKey(category);

            var key = NewKey(GetTypeCodeData(category, typeData), keyExt);
            key.N1 = (ulong)longKey;
            return key;
        }

        public static UniqueKey NewKey() => new UniqueKey { Guid = Guid.NewGuid() };

        internal static UniqueKey NewKey(Guid guid) => new UniqueKey { Guid = guid };

        internal static UniqueKey NewKey(Guid guid, Category category = Category.None, long typeData = 0, string keyExt = null)
        {
            ThrowIfIsSystemTargetKey(category);

            var key = NewKey(GetTypeCodeData(category, typeData), keyExt);
            key.Guid = guid;
            return key;
        }

        internal static UniqueKey NewEmptySystemTargetKey(long typeData)
            => new UniqueKey { TypeCodeData = GetTypeCodeData(Category.SystemTarget, typeData) };

        public static UniqueKey NewSystemTargetKey(Guid guid, long typeData)
            => new UniqueKey { Guid = guid, TypeCodeData = GetTypeCodeData(Category.SystemTarget, typeData) };

        public static UniqueKey NewSystemTargetKey(short systemId)
            => new UniqueKey { N1 = (ulong)systemId, TypeCodeData = GetTypeCodeData(Category.SystemTarget) };

        public static UniqueKey NewGrainServiceKey(short key, long typeData)
            => new UniqueKey { N1 = (ulong)key, TypeCodeData = GetTypeCodeData(Category.SystemTarget, typeData) };

        public static UniqueKey NewGrainServiceKey(string key, long typeData)
            => NewKey(GetTypeCodeData(Category.KeyExtSystemTarget, typeData), key);

        internal static UniqueKey NewKey(ulong n0, ulong n1, ulong typeCodeData, string keyExt)
        {
            var key = NewKey(typeCodeData, keyExt);
            key.N0 = n0;
            key.N1 = n1;
            return key;
        }

        private static UniqueKey NewKey(ulong typeCodeData, string keyExt)
        {
            if (IsKeyExt(GetCategory(typeCodeData)))
            {
                if (string.IsNullOrWhiteSpace(keyExt))
                    throw keyExt is null ? new ArgumentNullException("keyExt") : throw new ArgumentException("Extended key is empty or white space.", "keyExt");
            }
            else if (keyExt != null) throw new ArgumentException("Only key extended grains can specify a non-null key extension.");
            return new UniqueKey { TypeCodeData = typeCodeData, KeyExt = keyExt };
        }

        private void ThrowIfIsNotLong()
        {
            if (!IsLongKey)
                throw new InvalidOperationException("this key cannot be interpreted as a long value");
        }

        private static void ThrowIfIsSystemTargetKey(Category category)
        {
            if (IsSystemTarget(category))
                throw new ArgumentException(
                    "This overload of NewKey cannot be used to construct an instance of UniqueKey containing a SystemTarget id.");
        }

        private void ThrowIfHasKeyExt(string methodName)
        {
            if (KeyExt != null)
                throw new InvalidOperationException(
                    string.Format(
                        "This overload of {0} cannot be used if the grain uses the primary key extension feature.",
                        methodName));
        }

        public long PrimaryKeyToLong(out string extendedKey)
        {
            ThrowIfIsNotLong();

            extendedKey = this.KeyExt;
            return unchecked((long)N1);
        }

        public long PrimaryKeyToLong()
        {
            ThrowIfIsNotLong();
            ThrowIfHasKeyExt("UniqueKey.PrimaryKeyToLong");
            return (long)N1;
        }

        public Guid PrimaryKeyToGuid(out string extendedKey)
        {
            extendedKey = this.KeyExt;
            return Guid;
        }

        public Guid PrimaryKeyToGuid()
        {
            ThrowIfHasKeyExt("UniqueKey.PrimaryKeyToGuid");
            return Guid;
        }

        public override bool Equals(object o) => o is UniqueKey key && Equals(key);

        // We really want Equals to be as fast as possible, as a minimum cost, as close to native as possible.
        // No function calls, no boxing, inline.
        public bool Equals(UniqueKey other)
        {
            return N0 == other.N0
                   && N1 == other.N1
                   && TypeCodeData == other.TypeCodeData
                   && (KeyExt is null || KeyExt == other.KeyExt);
        }

        // We really want CompareTo to be as fast as possible, as a minimum cost, as close to native as possible.
        // No function calls, no boxing, inline.
        public int CompareTo(UniqueKey other)
        {
            return TypeCodeData < other.TypeCodeData ? -1
               : TypeCodeData > other.TypeCodeData ? 1
               : N0 < other.N0 ? -1
               : N0 > other.N0 ? 1
               : N1 < other.N1 ? -1
               : N1 > other.N1 ? 1
               : KeyExt == null ? 0
               : string.CompareOrdinal(KeyExt, other.KeyExt);
        }

        public override int GetHashCode()
        {
            return unchecked((int)GetUniformHashCode());
        }

        internal uint GetUniformHashCode()
        {
            // Disabling this ReSharper warning; hashCache is a logically read-only variable, so accessing them in GetHashCode is safe.
            // ReSharper disable NonReadonlyFieldInGetHashCode
            if (uniformHashCache == 0)
            {
                uint n;
                if (KeyExt != null)
                {
                    n = JenkinsHash.ComputeHash(this.ToByteArray());
                }
                else
                {
                    n = JenkinsHash.ComputeHash(TypeCodeData, N0, N1);
                }
                // Unchecked is required because the Jenkins hash is an unsigned 32-bit integer, 
                // which we need to convert to a signed 32-bit integer.
                uniformHashCache = n;
            }
            return uniformHashCache;
            // ReSharper restore NonReadonlyFieldInGetHashCode
        }

        /// <summary>
        /// If KeyExt not exists, returns following structure
        /// |8 bytes|8 bytes|8 bytes|4 bytes| - total 28 bytes.
        /// If KeyExt exists, adds additional KeyExt bytes length
        /// </summary>
        /// <returns></returns>
        internal ReadOnlySpan<byte> ToByteArray()
        {
            var extBytes = this.KeyExt != null ? Encoding.UTF8.GetBytes(KeyExt) : null;
            var extBytesLength = extBytes?.Length ?? 0;
            var sizeWithoutExtBytes = sizeof(ulong) * 3 + sizeof(int);

            var spanBytes = new byte[sizeWithoutExtBytes + extBytesLength].AsSpan();

            BinaryPrimitives.WriteUInt64LittleEndian(spanBytes, N0);
            BinaryPrimitives.WriteUInt64LittleEndian(spanBytes.Slice(8, 8), N1);
            BinaryPrimitives.WriteUInt64LittleEndian(spanBytes.Slice(16, 8), TypeCodeData);

            const int offset = sizeof(ulong) * 3;
            // Copy KeyExt
            if (extBytes != null)
            {
                BinaryPrimitives.WriteInt32LittleEndian(spanBytes.Slice(offset, sizeof(int)), extBytesLength);
                extBytes.CopyTo(spanBytes.Slice(offset + sizeof(int)));
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(spanBytes.Slice(offset, sizeof(int)), -1);
            }

            return spanBytes;
        }

        private unsafe Guid Guid
        {
            get
            {
                if (BitConverter.IsLittleEndian && sizeof(Guid) == 2 * sizeof(ulong))
                {
                    Guid value;
                    ((ulong*)&value)[0] = N0;
                    ((ulong*)&value)[1] = N1;
                    return value;
                }
                return new Guid((uint)N0, (ushort)(N0 >> 32), (ushort)(N0 >> 48), (byte)N1, (byte)(N1 >> 8), (byte)(N1 >> 16), (byte)(N1 >> 24), (byte)(N1 >> 32), (byte)(N1 >> 40), (byte)(N1 >> 48), (byte)(N1 >> 56));
            }
            set
            {
                if (BitConverter.IsLittleEndian && sizeof(Guid) == 2 * sizeof(ulong))
                {
                    N0 = ((ulong*)&value)[0];
                    N1 = ((ulong*)&value)[1];
                }
                else
                {
                    var guid = value.ToByteArray().AsSpan();
                    N0 = BinaryPrimitives.ReadUInt64LittleEndian(guid);
                    N1 = BinaryPrimitives.ReadUInt64LittleEndian(guid.Slice(8));
                }
            }
        }

        public override string ToString()
        {
            return ToHexString();
        }

        internal string ToHexString()
        {
            const string format = "{0:x16}{1:x16}{2:x16}";
            return KeyExt is null ? string.Format(format, N0, N1, TypeCodeData)
                : string.Format(format + "+{3}", N0, N1, TypeCodeData, KeyExt);
        }

        internal string ToGrainKeyString()
        {
            string keyString;
            if (HasKeyExt)
            {
                string extension;
                keyString = IsLongKey ? PrimaryKeyToLong(out extension).ToString() : PrimaryKeyToGuid(out extension).ToString();
                keyString = $"{keyString}+{extension}";
            }
            else
            {
                keyString = this.IsLongKey ? PrimaryKeyToLong().ToString() : this.PrimaryKeyToGuid().ToString();
            }
            return keyString;
        }

        internal static Category GetCategory(UInt64 typeCodeData)
        {
            return (Category)((typeCodeData >> 56) & 0xFF);
        }

        private static ulong GetTypeCodeData(Category category, long typeData = 0) => ((ulong)category << 56) + ((ulong)typeData & 0x00FFFFFFFFFFFFFF);
    }
}
