using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents the composite key used by the legacy Orleans grain identifier encoding.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    [SuppressReferenceTracking]
    [JsonConverter(typeof(UniqueKeyJsonConverter))]
    public sealed class UniqueKey : IComparable<UniqueKey>, IEquatable<UniqueKey>
    {
        /// <summary>
        /// Type id values encoded into UniqueKeys
        /// </summary>
        public enum Category : byte
        {
            /// <summary>
            /// No category is specified.
            /// </summary>
            None = 0,

            /// <summary>
            /// A system target identifier.
            /// </summary>
            SystemTarget = 1,

            /// <summary>
            /// A system grain identifier.
            /// </summary>
            SystemGrain = 2,

            /// <summary>
            /// A grain identifier.
            /// </summary>
            Grain = 3,

            /// <summary>
            /// A client identifier.
            /// </summary>
            Client = 4,

            /// <summary>
            /// A grain identifier with a key extension.
            /// </summary>
            KeyExtGrain = 6,
            // 7 was GeoClient

            /// <summary>
            /// A system target identifier with a key extension.
            /// </summary>
            KeyExtSystemTarget = 8,
        }

        /// <summary>
        /// Gets the first 64 bits of the primary key.
        /// </summary>
        [Id(0)]
        public ulong N0 { get; private set; }

        /// <summary>
        /// Gets the second 64 bits of the primary key.
        /// </summary>
        [Id(1)]
        public ulong N1 { get; private set; }

        /// <summary>
        /// Gets the packed category and type code data.
        /// </summary>
        [Id(2)]
        public ulong TypeCodeData { get; private set; }

        /// <summary>
        /// Gets the optional primary key extension.
        /// </summary>
        [Id(3)]
        public string? KeyExt { get; private set; }

        [NonSerialized]
        private uint uniformHashCache;

        /// <summary>
        /// Gets the 32-bit legacy type code.
        /// </summary>
        public int BaseTypeCode => (int)TypeCodeData;

        /// <summary>
        /// Gets the identifier category encoded in <see cref="TypeCodeData"/>.
        /// </summary>
        public Category IdCategory => GetCategory(TypeCodeData);

        /// <summary>
        /// Gets a value indicating whether the primary key is encoded as a <see cref="long"/>.
        /// </summary>
        public bool IsLongKey => N0 == 0;

        /// <summary>
        /// Gets a value indicating whether this key represents a system target.
        /// </summary>
        public bool IsSystemTargetKey
            => IsSystemTarget(IdCategory);

        private static bool IsSystemTarget(Category category)
            => category == Category.SystemTarget || category == Category.KeyExtSystemTarget;

        /// <summary>
        /// Gets a value indicating whether this key has a primary key extension.
        /// </summary>
        public bool HasKeyExt => IsKeyExt(IdCategory);

        private static bool IsKeyExt(Category category)
            => category == Category.KeyExtGrain || category == Category.KeyExtSystemTarget;

        internal static readonly UniqueKey Empty = new UniqueKey();

        internal static UniqueKey Parse(ReadOnlySpan<char> input) => ParseCore(input, trim: true);

        internal static UniqueKey ParseCanonical(ReadOnlySpan<char> input) => ParseCore(input, trim: false);

        private static UniqueKey ParseCore(ReadOnlySpan<char> input, bool trim)
        {
            const int minimumValidKeyLength = 48;
            if (trim)
            {
                input = input.Trim();
            }

            if (input.Length >= minimumValidKeyLength)
            {
                var n0 = ulong.Parse(input[..16].ToString(), NumberStyles.AllowHexSpecifier);
                var n1 = ulong.Parse(input.Slice(16, 16).ToString(), NumberStyles.AllowHexSpecifier);
                var typeCodeData = ulong.Parse(input.Slice(32, 16).ToString(), NumberStyles.AllowHexSpecifier);
                string? keyExt = null;
                if (input.Length > minimumValidKeyLength)
                {
                    if (input[48] != '+') throw new InvalidDataException("UniqueKey hex string missing + separator.");
                    keyExt = input[49..].ToString();
                }

                return NewKey(n0, n1, typeCodeData, keyExt);
            }

            // last, for convenience we attempt to parse the string using GUID syntax. this is needed by unit
            // tests but i don't know if it's needed for production.
            return NewKey(Guid.Parse(input.ToString()));
        }

        internal static UniqueKey NewKey(ulong n0, ulong n1, Category category, long typeData, string? keyExt)
            => NewKey(n0, n1, GetTypeCodeData(category, typeData), keyExt);

        internal static UniqueKey NewKey(long longKey, Category category = Category.None, long typeData = 0, string? keyExt = null)
        {
            ThrowIfIsSystemTargetKey(category);

            var key = NewKey(GetTypeCodeData(category, typeData), keyExt);
            key.N1 = (ulong)longKey;
            return key;
        }

        /// <summary>
        /// Creates a key with a randomly generated GUID primary key.
        /// </summary>
        /// <returns>A new unique key.</returns>
        public static UniqueKey NewKey() => new UniqueKey { Guid = Guid.NewGuid() };

        internal static UniqueKey NewKey(Guid guid) => new UniqueKey { Guid = guid };

        internal static UniqueKey NewKey(Guid guid, Category category = Category.None, long typeData = 0, string? keyExt = null)
        {
            ThrowIfIsSystemTargetKey(category);

            var key = NewKey(GetTypeCodeData(category, typeData), keyExt);
            key.Guid = guid;
            return key;
        }

        internal static UniqueKey NewEmptySystemTargetKey(long typeData)
            => new UniqueKey { TypeCodeData = GetTypeCodeData(Category.SystemTarget, typeData) };

        /// <summary>
        /// Creates a system target key with a GUID primary key.
        /// </summary>
        /// <param name="guid">The primary key.</param>
        /// <param name="typeData">The system target type data.</param>
        /// <returns>A new system target key.</returns>
        public static UniqueKey NewSystemTargetKey(Guid guid, long typeData)
            => new UniqueKey { Guid = guid, TypeCodeData = GetTypeCodeData(Category.SystemTarget, typeData) };

        /// <summary>
        /// Creates a system target key from a system identifier.
        /// </summary>
        /// <param name="systemId">The system identifier.</param>
        /// <returns>A new system target key.</returns>
        public static UniqueKey NewSystemTargetKey(short systemId)
            => new UniqueKey { N1 = (ulong)systemId, TypeCodeData = GetTypeCodeData(Category.SystemTarget) };

        /// <summary>
        /// Creates a grain service key with an integer primary key.
        /// </summary>
        /// <param name="key">The primary key.</param>
        /// <param name="typeData">The grain service type data.</param>
        /// <returns>A new grain service key.</returns>
        public static UniqueKey NewGrainServiceKey(short key, long typeData)
            => new UniqueKey { N1 = (ulong)key, TypeCodeData = GetTypeCodeData(Category.SystemTarget, typeData) };

        /// <summary>
        /// Creates a grain service key with a string primary key extension.
        /// </summary>
        /// <param name="key">The primary key extension.</param>
        /// <param name="typeData">The grain service type data.</param>
        /// <returns>A new grain service key.</returns>
        public static UniqueKey NewGrainServiceKey(string key, long typeData)
            => NewKey(GetTypeCodeData(Category.KeyExtSystemTarget, typeData), key);

        internal static UniqueKey NewKey(ulong n0, ulong n1, ulong typeCodeData, string? keyExt)
        {
            var key = NewKey(typeCodeData, keyExt);
            key.N0 = n0;
            key.N1 = n1;
            return key;
        }

        private static UniqueKey NewKey(ulong typeCodeData, string? keyExt)
        {
            if (IsKeyExt(GetCategory(typeCodeData)))
            {
                if (string.IsNullOrWhiteSpace(keyExt))
                    throw keyExt is null ? new ArgumentNullException(nameof(keyExt)) : throw new ArgumentException("Extended key is empty or white space.", nameof(keyExt));
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

        /// <summary>
        /// Gets the integer primary key and its optional key extension.
        /// </summary>
        /// <param name="extendedKey">When this method returns, contains the key extension, or <see langword="null"/> if none is present.</param>
        /// <returns>The integer primary key.</returns>
        public long PrimaryKeyToLong(out string? extendedKey)
        {
            ThrowIfIsNotLong();

            extendedKey = this.KeyExt;
            return unchecked((long)N1);
        }

        /// <summary>
        /// Gets the integer primary key of a key without an extension.
        /// </summary>
        /// <returns>The integer primary key.</returns>
        public long PrimaryKeyToLong()
        {
            ThrowIfIsNotLong();
            ThrowIfHasKeyExt("UniqueKey.PrimaryKeyToLong");
            return (long)N1;
        }

        /// <summary>
        /// Gets the GUID primary key and its optional key extension.
        /// </summary>
        /// <param name="extendedKey">When this method returns, contains the key extension, or <see langword="null"/> if none is present.</param>
        /// <returns>The GUID primary key.</returns>
        public Guid PrimaryKeyToGuid(out string? extendedKey)
        {
            extendedKey = this.KeyExt;
            return Guid;
        }

        /// <summary>
        /// Gets the GUID primary key of a key without an extension.
        /// </summary>
        /// <returns>The GUID primary key.</returns>
        public Guid PrimaryKeyToGuid()
        {
            ThrowIfHasKeyExt("UniqueKey.PrimaryKeyToGuid");
            return Guid;
        }

        /// <inheritdoc />
        public override bool Equals(object? o) => o is UniqueKey key && Equals(key);

        // We really want Equals to be as fast as possible, as a minimum cost, as close to native as possible.
        // No function calls, no boxing, inline.
        /// <inheritdoc />
        public bool Equals(UniqueKey? other)
        {
            return other is not null
                   && N0 == other.N0
                   && N1 == other.N1
                   && TypeCodeData == other.TypeCodeData
                   && (KeyExt is null || KeyExt == other.KeyExt);
        }

        // We really want CompareTo to be as fast as possible, as a minimum cost, as close to native as possible.
        // No function calls, no boxing, inline.
        /// <inheritdoc />
        public int CompareTo(UniqueKey? other)
        {
            if (other is null) return 1;

            return TypeCodeData < other.TypeCodeData ? -1
               : TypeCodeData > other.TypeCodeData ? 1
               : N0 < other.N0 ? -1
               : N0 > other.N0 ? 1
               : N1 < other.N1 ? -1
               : N1 > other.N1 ? 1
               : KeyExt == null ? 0
               : string.CompareOrdinal(KeyExt, other.KeyExt);
        }

        /// <inheritdoc />
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
                if (KeyExt != null)
                {
                    uniformHashCache = StableHash.ComputeHash(this.ToByteArray());
                }
                else
                {
                    Span<byte> data = stackalloc byte[24];
                    BinaryPrimitives.WriteUInt64LittleEndian(data, TypeCodeData);
                    BinaryPrimitives.WriteUInt64LittleEndian(data[8..], N0);
                    BinaryPrimitives.WriteUInt64LittleEndian(data[16..], N1);
                    uniformHashCache = StableHash.ComputeHash(data);
                }
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
                extBytes.CopyTo(spanBytes[(offset + sizeof(int))..]);
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
                    N1 = BinaryPrimitives.ReadUInt64LittleEndian(guid[8..]);
                }
            }
        }

        /// <inheritdoc />
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
                string? extension;
                keyString = IsLongKey ? PrimaryKeyToLong(out extension).ToString() : PrimaryKeyToGuid(out extension).ToString();
                keyString = $"{keyString}+{extension ?? string.Empty}";
            }
            else
            {
                keyString = this.IsLongKey ? PrimaryKeyToLong().ToString() : this.PrimaryKeyToGuid().ToString();
            }
            return keyString;
        }

        internal static Category GetCategory(ulong typeCodeData)
        {
            return (Category)((typeCodeData >> 56) & 0xFF);
        }

        private static ulong GetTypeCodeData(Category category, long typeData = 0) => ((ulong)category << 56) + ((ulong)typeData & 0x00FFFFFFFFFFFFFF);
    }

    /// <summary>
    /// Functionality for converting <see cref="UniqueKey"/> instances to and from their JSON representation.
    /// </summary>
    public sealed class UniqueKeyJsonConverter : JsonConverter<UniqueKey>
    {
        private const int MaxBufferSize = 256;

        /// <inheritdoc />
        public override UniqueKey? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Span<char> buffer = stackalloc char[MaxBufferSize];
            return GetUniqueKey(ref reader, buffer) ?? throw new JsonException($"Could not deserialize {nameof(UniqueKey)}.");
        }

        private static UniqueKey? GetUniqueKey(ref Utf8JsonReader reader, scoped Span<char> buffer)
        {
            if (reader.TokenType is not JsonTokenType.String and not JsonTokenType.PropertyName)
            {
                throw new JsonException($"Could not deserialize {nameof(UniqueKey)}.");
            }

            if (reader.HasValueSequence)
            {
                var valueLength = checked((int)reader.ValueSequence.Length);
                if (valueLength < buffer.Length)
                {
                    var written = reader.CopyString(buffer);
                    return UniqueKey.ParseCanonical(buffer[..written]);
                }
            }
            else
            {
                if (reader.ValueSpan.Length < buffer.Length)
                {
                    var written = reader.CopyString(buffer);
                    return UniqueKey.ParseCanonical(buffer[..written]);
                }
            }

            var str = reader.GetString();
            return str is null ? null : UniqueKey.ParseCanonical(str);
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, UniqueKey value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToHexString());

        /// <inheritdoc />
        public override UniqueKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Span<char> buffer = stackalloc char[MaxBufferSize];
            return GetUniqueKey(ref reader, buffer) ?? throw new JsonException("Failed to parse UniqueKey from property name.");
        }

        /// <inheritdoc />
        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] UniqueKey value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToHexString());
        }
    }
}
