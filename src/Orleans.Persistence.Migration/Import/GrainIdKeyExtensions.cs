using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Extensions for <see cref="GrainId"/> keys.
    /// </summary>
    public static class GrainIdKeyExtensions
    {
        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="long"/> key.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key.
        /// </returns>
        public static IdSpan CreateIntegerKey(long key)
        {
            Span<byte> buf = stackalloc byte[sizeof(long) * 2];
            Utf8Formatter.TryFormat(key, buf, out var len, 'X');
            Debug.Assert(len > 0, "Unable to format the provided value as a UTF8 string");
            return new IdSpan(buf.Slice(0, len).ToArray());
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="long"/> key and key extension string.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="keyExtension">
        /// The UTF-8 encoded key extension.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key and key extension.
        /// </returns>
        public static IdSpan CreateIntegerKey(long key, ReadOnlySpan<byte> keyExtension)
        {
            if (keyExtension.IsEmpty)
                return CreateIntegerKey(key);

            Span<byte> tmp = stackalloc byte[sizeof(long) * 2];
            Utf8Formatter.TryFormat(key, tmp, out var len, 'X');
            Debug.Assert(len > 0, "Unable to format the provided value as a UTF8 string");

            var buf = new byte[len + 1 + keyExtension.Length];
            tmp[..len].CopyTo(buf);
            buf[len] = (byte)'+';
            keyExtension.CopyTo(buf.AsSpan(len + 1));
            return new(buf);
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="long"/> key and key extension string.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="keyExtension">
        /// The key extension.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key and key extension.
        /// </returns>
        public static IdSpan CreateIntegerKey(long key, string? keyExtension)
        {
            if (string.IsNullOrWhiteSpace(keyExtension))
            {
                return CreateIntegerKey(key);
            }

            Span<byte> tmp = stackalloc byte[sizeof(long) * 2];
            Utf8Formatter.TryFormat(key, tmp, out var len, 'X');
            Debug.Assert(len > 0, "Unable to format the provided value as a UTF8 string");

            var extLen = Encoding.UTF8.GetByteCount(keyExtension);
            var buf = new byte[len + 1 + extLen];
            tmp.Slice(0, len).CopyTo(buf);
            buf[len] = (byte)'+';
            Encoding.UTF8.GetBytes(keyExtension, 0, keyExtension.Length, buf, len + 1);

            return new IdSpan(buf);
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="Guid"/> key.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key.
        /// </returns>
        public static IdSpan CreateGuidKey(Guid key)
        {
            var buf = new byte[32];
            Utf8Formatter.TryFormat(key, buf, out var len, 'N');
            Debug.Assert(len == 32, "Unable to format the provided value as a UTF8 string");
            return new IdSpan(buf);
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="Guid"/> key and key extension string.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="keyExtension">
        /// The UTF-8 encoded key extension.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key and key extension.
        /// </returns>
        public static IdSpan CreateGuidKey(Guid key, ReadOnlySpan<byte> keyExtension)
        {
            if (keyExtension.IsEmpty)
                return CreateGuidKey(key);

            var buf = new byte[32 + 1 + keyExtension.Length];
            Utf8Formatter.TryFormat(key, buf, out var len, 'N');
            Debug.Assert(len == 32, "Unable to format the provided value as a UTF8 string");
            buf[32] = (byte)'+';
            keyExtension.CopyTo(buf.AsSpan(len + 1));
            return new(buf);
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="Guid"/> key and key extension string.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="keyExtension">
        /// The key extension.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key and key extension.
        /// </returns>
        public static IdSpan CreateGuidKey(Guid key, string? keyExtension)
        {
            if (string.IsNullOrWhiteSpace(keyExtension))
            {
                return CreateGuidKey(key);
            }

            var extLen = Encoding.UTF8.GetByteCount(keyExtension);
            var buf = new byte[32 + 1 + extLen];
            Utf8Formatter.TryFormat(key, buf, out var len, 'N');
            Debug.Assert(len == 32, "Unable to format the provided value as a UTF8 string");
            buf[32] = (byte)'+';
            Encoding.UTF8.GetBytes(keyExtension, 0, keyExtension.Length, buf, 33);

            return new IdSpan(buf);
        }
    }
}