using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text;
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
    public readonly struct StreamId : IEquatable<StreamId>, IComparable<StreamId>, ISerializable, ISpanFormattable
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
                if (destination.Length >= len + 5)
                {
                    "null/".CopyTo(destination);
                    charsWritten = Encoding.UTF8.GetChars(fullKey, destination[5..]) + 5;
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
}
