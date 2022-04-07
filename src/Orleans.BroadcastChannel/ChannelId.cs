using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Identifies a Channel within a provider
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    public readonly struct ChannelId : IEquatable<ChannelId>, IComparable<ChannelId>, ISerializable
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

        private ChannelId(byte[] fullKey, ushort keyIndex, int hash)
        {
            this.fullKey = fullKey;
            this.keyIndex = keyIndex;
            this.hash = hash;
        }

        internal ChannelId(byte[] fullKey, ushort keyIndex)
            : this(fullKey, keyIndex, (int)JenkinsHash.ComputeHash(fullKey))
        {
        }

        private ChannelId(SerializationInfo info, StreamingContext context)
        {
            fullKey = (byte[])info.GetValue("fk", typeof(byte[]));
            this.keyIndex = info.GetUInt16("ki");
            this.hash = info.GetInt32("fh");
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static ChannelId Create(byte[] ns, byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            if (ns != null)
            {
                var fullKeysBytes = new byte[ns.Length + key.Length];
                ns.CopyTo(fullKeysBytes.AsSpan());
                key.CopyTo(fullKeysBytes.AsSpan(ns.Length));
                return new ChannelId(fullKeysBytes, (ushort)ns.Length);
            }
            else
            {
                return new ChannelId((byte[])key.Clone(), 0);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static ChannelId Create(string ns, Guid key)
        {
            if (ns is null)
            {
                var buf = new byte[32];
                Utf8Formatter.TryFormat(key, buf, out var len, 'N');
                Debug.Assert(len == 32);
                return new ChannelId(buf, 0);
            }
            else
            {
                var nsLen = Encoding.UTF8.GetByteCount(ns);
                var buf = new byte[nsLen + 32];
                Encoding.UTF8.GetBytes(ns, 0, ns.Length, buf, 0);
                Utf8Formatter.TryFormat(key, buf.AsSpan(nsLen), out var len, 'N');
                Debug.Assert(len == 32);
                return new ChannelId(buf, (ushort)nsLen);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelId"/> struct.
        /// </summary>
        /// <param name="ns">The namespace.</param>
        /// <param name="key">The key.</param>
        public static ChannelId Create(string ns, string key)
        {
            if (ns is null)
                return new ChannelId(Encoding.UTF8.GetBytes(key), 0);

            var nsLen = Encoding.UTF8.GetByteCount(ns);
            var keyLen = Encoding.UTF8.GetByteCount(key);
            var buf = new byte[nsLen + keyLen];
            Encoding.UTF8.GetBytes(ns, 0, ns.Length, buf, 0);
            Encoding.UTF8.GetBytes(key, 0, key.Length, buf, nsLen);
            return new ChannelId(buf, (ushort)nsLen);
        }

        /// <inheritdoc/>
        public int CompareTo(ChannelId other) => fullKey.AsSpan().SequenceCompareTo(other.fullKey);

        /// <inheritdoc/>
        public bool Equals(ChannelId other) => fullKey.AsSpan().SequenceEqual(other.fullKey);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ChannelId other ? this.Equals(other) : false;

        /// <summary>
        /// Compares two <see cref="ChannelId"/> instances for equality.
        /// </summary>
        /// <param name="s1">The first stream identity.</param>
        /// <param name="s2">The second stream identity.</param>
        /// <returns>The result of the operator.</returns>
        public static bool operator ==(ChannelId s1, ChannelId s2) => s1.Equals(s2);

        /// <summary>
        /// Compares two <see cref="ChannelId"/> instances for equality.
        /// </summary>
        /// <param name="s1">The first stream identity.</param>
        /// <param name="s2">The second stream identity.</param>
        /// <returns>The result of the operator.</returns>
        public static bool operator !=(ChannelId s1, ChannelId s2) => !s2.Equals(s1);

        /// <inheritdoc/>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("fk", fullKey);
            info.AddValue("ki", this.keyIndex);
            info.AddValue("fh", this.hash);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var key = this.GetKeyAsString();
            return keyIndex == 0 ? "null/" + key : this.GetNamespace() + "/" + key;
        }

        /// <summary>
        /// Parses a <see cref="ChannelId"/> instance from a <see cref="string"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The parsed stream identity.</returns>
        public static ChannelId Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ThrowInvalidInternalStreamId(value);
            }

            var i = value.IndexOf('/');
            if (i < 0)
            {
                ThrowInvalidInternalStreamId(value);
            }

            return Create(value.Substring(0, i), value.Substring(i + 1));
        }

        private static void ThrowInvalidInternalStreamId(string value) => throw new ArgumentException($"Unable to parse \"{value}\" as a stream id");

        /// <inheritdoc/>
        public override int GetHashCode() => this.hash;

        /// <summary>
        /// Returns the <see cref="Key"/> component of this instance as a string.
        /// </summary>
        /// <returns>The key component of this instance.</returns>
        public string GetKeyAsString() => Encoding.UTF8.GetString(fullKey, keyIndex, fullKey.Length - keyIndex);

        /// <summary>
        /// Returns the <see cref="Namespace"/> component of this instance as a string.
        /// </summary>
        /// <returns>The namespace component of this instance.</returns>
        public string GetNamespace() => keyIndex == 0 ? null : Encoding.UTF8.GetString(fullKey, 0, keyIndex);
    }

    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    internal readonly struct InternalChannelId : IEquatable<InternalChannelId>, IComparable<InternalChannelId>, ISerializable
    {
        [Id(0)]
        public ChannelId ChannelId { get; }

        [Id(1)]
        public string ProviderName { get; }

        public InternalChannelId(string providerName, ChannelId streamId)
        {
            ProviderName = providerName;
            ChannelId = streamId;
        }

        private InternalChannelId(SerializationInfo info, StreamingContext context)
        {
            ProviderName = info.GetString("pvn");
            ChannelId = (ChannelId)info.GetValue("sid", typeof(ChannelId));
        }

        public static implicit operator ChannelId(InternalChannelId internalStreamId) => internalStreamId.ChannelId;

        public bool Equals(InternalChannelId other) => ChannelId.Equals(other) && ProviderName.Equals(other.ProviderName);

        public override bool Equals(object obj) => obj is InternalChannelId other ? this.Equals(other) : false;

        public static bool operator ==(InternalChannelId s1, InternalChannelId s2) => s1.Equals(s2);

        public static bool operator !=(InternalChannelId s1, InternalChannelId s2) => !s2.Equals(s1);

        public int CompareTo(InternalChannelId other) => ChannelId.CompareTo(other.ChannelId);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("pvn", ProviderName);
            info.AddValue("sid", ChannelId, typeof(ChannelId));
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ProviderName.GetHashCode() * 43 ^ ChannelId.GetHashCode();
            }
        }

        public override string ToString()
        {
            return $"{ProviderName}/{ChannelId.ToString()}";
        }

        public static InternalChannelId Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ThrowInvalidInternalStreamId(value);
            }

            var i = value.IndexOf('/');
            if (i < 0)
            {
                ThrowInvalidInternalStreamId(value);
            }

            return new InternalChannelId(value.Substring(0, i), ChannelId.Parse(value.Substring(i + 1)));
        }

        private static void ThrowInvalidInternalStreamId(string value) => throw new ArgumentException($"Unable to parse \"{value}\" as a stream id");


        internal string GetNamespace() => ChannelId.GetNamespace();
    }
}