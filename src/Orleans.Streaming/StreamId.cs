using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Orleans.Streams;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a Stream within a provider
    /// </summary>
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    [JsonConverter(typeof(StreamIdJsonConverter))]
    public readonly struct StreamId : IEquatable<StreamId>, IComparable<StreamId>, ISerializable, ISpanFormattable, IUtf8SpanFormattable
    {
        [Id(0)]
        private readonly byte[] fullKey;

        [Id(1)]
        private readonly ushort keyIndex;

        [Id(2)]
        private readonly int hash;

        /// <summary>
        /// Gets the full key.
        /// </summary>
        /// <value>The full key.</value>
        public ReadOnlyMemory<byte> FullKey => fullKey;

        /// <summary>
        /// Gets the namespace.
        /// </summary>
        /// <value>The namespace.</value>
        public ReadOnlyMemory<byte> Namespace => fullKey.AsMemory(0, this.keyIndex);

        /// <summary>
        /// Gets the key.
        /// </summary>
        /// <value>The key.</value>
        public ReadOnlyMemory<byte> Key => fullKey.AsMemory(this.keyIndex);

        private StreamId(byte[] fullKey, ushort keyIndex, int hash)
        {
            this.fullKey = fullKey;
            this.keyIndex = keyIndex;
            this.hash = hash;
        }

        internal StreamId(byte[] fullKey, ushort keyIndex)
            : this(fullKey, keyIndex, (int)StableHash.ComputeHash(fullKey))
        {
        }

        private StreamId(SerializationInfo info, StreamingContext context)
        {
            fullKey = (byte[])info.GetValue("fk", typeof(byte[]))!;
            this.keyIndex = info.GetUInt16("ki");
            this.hash = info.GetInt32("fh");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static StreamId Create(ReadOnlySpan<byte> ns, ReadOnlySpan<byte> key)
        {
            if (key.IsEmpty)
                throw new ArgumentNullException(nameof(key));

            if (!ns.IsEmpty)
            {
                var fullKeysBytes = new byte[ns.Length + key.Length];
                ns.CopyTo(fullKeysBytes.AsSpan());
                key.CopyTo(fullKeysBytes.AsSpan(ns.Length));
                return new(fullKeysBytes, (ushort)ns.Length);
            }
            else
            {
                return new(key.ToArray(), 0);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static StreamId Create(string ns, Guid key)
        {
            if (ns is null)
            {
                var buf = new byte[32];
                Utf8Formatter.TryFormat(key, buf, out var len, 'N');
                Debug.Assert(len == 32);
                return new StreamId(buf, 0);
            }
            else
            {
                var nsLen = Encoding.UTF8.GetByteCount(ns);
                var buf = new byte[nsLen + 32];
                Encoding.UTF8.GetBytes(ns, 0, ns.Length, buf, 0);
                Utf8Formatter.TryFormat(key, buf.AsSpan(nsLen), out var len, 'N');
                Debug.Assert(len == 32);
                return new StreamId(buf, (ushort)nsLen);
            }
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static StreamId Create(string ns, long key) => Create(ns, key.ToString());

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static StreamId Create(string ns, string key)
        {
            if (ns is null)
                return new StreamId(Encoding.UTF8.GetBytes(key), 0);

            var nsLen = Encoding.UTF8.GetByteCount(ns);
            var keyLen = Encoding.UTF8.GetByteCount(key);
            var buf = new byte[nsLen + keyLen];
            Encoding.UTF8.GetBytes(ns, 0, ns.Length, buf, 0);
            Encoding.UTF8.GetBytes(key, 0, key.Length, buf, nsLen);
            return new StreamId(buf, (ushort)nsLen);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamId"/> struct.
        /// </summary>
        /// <param name="streamIdentity">The stream identity.</param>
        public static StreamId Create(IStreamIdentity streamIdentity) => Create(streamIdentity.Namespace, streamIdentity.Guid);

        /// <inheritdoc/>
        public int CompareTo(StreamId other) => fullKey.AsSpan().SequenceCompareTo(other.fullKey);

        /// <inheritdoc/>
        public bool Equals(StreamId other) => fullKey.AsSpan().SequenceEqual(other.fullKey);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is StreamId other ? this.Equals(other) : false;

        /// <summary>
        /// Compares two <see cref="StreamId"/> instances for equality.
        /// </summary>
        /// <param name="s1">The first stream identity.</param>
        /// <param name="s2">The second stream identity.</param>
        /// <returns>The result of the operator.</returns>
        public static bool operator ==(StreamId s1, StreamId s2) => s1.Equals(s2);

        /// <summary>
        /// Compares two <see cref="StreamId"/> instances for equality.
        /// </summary>
        /// <param name="s1">The first stream identity.</param>
        /// <param name="s2">The second stream identity.</param>
        /// <returns>The result of the operator.</returns>
        public static bool operator !=(StreamId s1, StreamId s2) => !s2.Equals(s1);

        /// <inheritdoc/>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("fk", fullKey);
            info.AddValue("ki", this.keyIndex);
            info.AddValue("fh", this.hash);
        }

        /// <inheritdoc/>
        public override string ToString() => $"{this}";
        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var len = Encoding.UTF8.GetCharCount(fullKey);
            if (keyIndex == 0)
            {
                if (destination.Length >= len + 1)
                {
                    destination[0] = '/';
                    charsWritten = Encoding.UTF8.GetChars(fullKey, destination[1..]) + 1;
                    return true;
                }
            }
            else if (destination.Length > len)
            {
                len = Encoding.UTF8.GetChars(fullKey.AsSpan(0, keyIndex), destination);
                destination[len++] = '/';
                charsWritten = Encoding.UTF8.GetChars(fullKey.AsSpan(keyIndex), destination[len..]) + len;
                return true;
            }

            charsWritten = 0;
            return false;
        }

        bool IUtf8SpanFormattable.TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            if (keyIndex == 0)
            {
                if (utf8Destination.Length >= fullKey.Length + 1)
                {
                    utf8Destination[0] = (byte)'/';
                    fullKey.CopyTo(utf8Destination[1..]);
                    bytesWritten = fullKey.Length + 1;
                    return true;
                }
            }
            else if (utf8Destination.Length > fullKey.Length)
            {
                fullKey[..keyIndex].CopyTo(utf8Destination);
                utf8Destination[keyIndex] = (byte)'/';
                fullKey[keyIndex..].CopyTo(utf8Destination[(keyIndex + 1)..]);
                bytesWritten = fullKey.Length + 1;
                return true;
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>
        /// Parses a <see cref="StreamId"/> instance from a <see cref="string"/>.
        /// </summary>
        /// <param name="value">The UTF-8 encoded value.</param>
        /// <returns>The parsed stream identity.</returns>
        public static StreamId Parse(ReadOnlySpan<byte> value)
        {
            var i = value.IndexOf((byte)'/');
            if (i < 0)
            {
                throw new ArgumentException($"Unable to parse \"{Encoding.UTF8.GetString(value)}\" as a stream id");
            }

            return Create(value[..i], value[(i + 1)..]);
        }

        /// <inheritdoc/>
        public override int GetHashCode() => this.hash;

        internal uint GetUniformHashCode() => (uint)hash;

        internal uint GetKeyIndex() => keyIndex;

        /// <summary>
        /// Returns the <see cref="Key"/> component of this instance as a string.
        /// </summary>
        /// <returns>The key component of this instance.</returns>
        public string GetKeyAsString() => Encoding.UTF8.GetString(fullKey, keyIndex, fullKey.Length - keyIndex);

        /// <summary>
        /// Returns the <see cref="Namespace"/> component of this instance as a string.
        /// </summary>
        /// <returns>The namespace component of this instance.</returns>
        public string? GetNamespace() => keyIndex == 0 ? null : Encoding.UTF8.GetString(fullKey, 0, keyIndex);

        internal IdSpan GetKeyIdSpan() => keyIndex == 0 ? IdSpan.UnsafeCreate(fullKey, hash) : new(fullKey.AsSpan(keyIndex).ToArray());
    }

    public sealed class StreamIdJsonConverter : JsonConverter<StreamId>
    {
        // This is backward compatible with Newtonsoft.JsonSerializer
        // which didn't have a JsonConverter for StreamId.
        // StreamId used the default serialization that Newtonsoft provided.

        // Ideally this STJ Converter would write Namespace/Key as a value 

        // This implementation emulates Newtonsoft by both reading and writing
        // the same structure.
        //
        // The alternatives were to
        // 1. break backward compatibility which would have prevented switching from Newtonsoft to STJ if streamIds were stored in persistence.
        // 2. To support reading the Newtonsoft format and the new format, but write using the preferred Key and Namespace format, which would allow a one-way migration, but prevent reverting to Newtonsoft.
        // 3. Add a Newtonsoft.JsonConverter for StreamId which supported the previous default Newtonsoft structure and also the preferred STJ Key and Namespace approach. This would make reverting Orleans a breaking change.

        private readonly string? _byteArrayType = typeof(byte[]).AssemblyQualifiedName;
        private readonly string? _streamIdType = typeof(StreamId).AssemblyQualifiedName;

        public override StreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            // This is backward compatible with the way Newtonsoft writes StreamId

            uint? ki = null;
            byte[]? value = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "ki":
                            ki = reader.GetUInt32();
                            break;
                        case "fk":
                            while (reader.Read())
                            {
                                if (reader.TokenType == JsonTokenType.EndObject)
                                    break;

                                if (reader.TokenType == JsonTokenType.PropertyName)
                                {
                                    propertyName = reader.GetString();
                                    reader.Read();

                                    if (propertyName == "$value")
                                    {
                                        value = reader.GetBytesFromBase64();
                                    }
                                }
                            }
                            break;
                    }
                }
            }

            return value is not { Length: > 0 }
                || !ki.HasValue
                ? default
                : new StreamId(value, (ushort)ki);
        }

        public override void Write(Utf8JsonWriter writer, StreamId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value != default)
            {
                writer.WriteString("$type", _streamIdType);
                writer.WriteStartObject("fk");
                writer.WriteString("$type", _byteArrayType);
                writer.WriteBase64String("$value", value.FullKey.Span);
                writer.WriteEndObject();
                writer.WriteNumber("ki", value.GetKeyIndex());
                writer.WriteNumber("fh", (int)value.GetUniformHashCode());
            }
            writer.WriteEndObject();
        }
        public override StreamId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.HasValueSequence)
            {
                Span<byte> buffer = reader.ValueSequence.Length < 128 ? stackalloc byte[(int)reader.ValueSequence.Length] :
                                                                        new byte[reader.ValueSequence.Length];
                reader.ValueSequence.CopyTo(buffer);
                return StreamId.Parse(buffer);
            }
            else
            {
                return StreamId.Parse(reader.ValueSpan);
            }
        }

        /// <inheritdoc />
        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] StreamId value, JsonSerializerOptions options)
        {
            Span<byte> buf = stackalloc byte[128];

            if (value is IUtf8SpanFormattable formattable
                && formattable.TryFormat(buf, out var written, [], null))
            {
                writer.WritePropertyName(buf[..written]);
            }
            else
            {
                writer.WritePropertyName(value.ToString());
            }
        }
    }
}
