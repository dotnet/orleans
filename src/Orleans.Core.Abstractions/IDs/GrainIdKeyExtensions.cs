using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

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
        public static IdSpan CreateIntegerKey(long key)
        {
            Span<byte> buf = stackalloc byte[sizeof(long) * 2];
            Utf8Formatter.TryFormat(key, buf, out var len, 'X');
            Debug.Assert(len > 0);
            return new IdSpan(buf.Slice(0, len).ToArray());
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="long"/> key.
        /// </summary>
        public static IdSpan CreateIntegerKey(long key, string keyExtension)
        {
            if (string.IsNullOrWhiteSpace(keyExtension)) return CreateIntegerKey(key);

            Span<byte> tmp = stackalloc byte[sizeof(long) * 2];
            Utf8Formatter.TryFormat(key, tmp, out var len, 'X');
            Debug.Assert(len > 0);

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
        public static IdSpan CreateGuidKey(Guid key)
        {
            var buf = new byte[32];
            Utf8Formatter.TryFormat(key, buf, out var len, 'N');
            Debug.Assert(len == 32);
            return new IdSpan(buf);
        }

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="Guid"/> key.
        /// </summary>
        public static IdSpan CreateGuidKey(Guid key, string keyExtension)
        {
            if (string.IsNullOrWhiteSpace(keyExtension)) return CreateGuidKey(key);

            var extLen = Encoding.UTF8.GetByteCount(keyExtension);
            var buf = new byte[32 + 1 + extLen];
            Utf8Formatter.TryFormat(key, buf, out var len, 'N');
            Debug.Assert(len == 32);
            buf[32] = (byte)'+';
            Encoding.UTF8.GetBytes(keyExtension, 0, keyExtension.Length, buf, 33);

            return new IdSpan(buf);
        }

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain primary key.
        /// </summary>
        public static bool TryGetIntegerKey(this GrainId grainId, out long key, out string keyExt)
        {
            keyExt = null;
            var keyString = grainId.Key.AsSpan();
            if (keyString.IndexOf((byte)'+') is int index && index >= 0)
            {
                keyExt = keyString.Slice(index + 1).GetUtf8String();
                keyString = keyString.Slice(0, index);
            }

            return Utf8Parser.TryParse(keyString, out key, out var len, 'X') && len == keyString.Length;
        }

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain primary key.
        /// </summary>
        internal static bool TryGetIntegerKey(this GrainId grainId, out long key)
        {
            var keyString = grainId.Key.AsSpan();
            if (keyString.IndexOf((byte)'+') is int index && index >= 0)
                keyString = keyString.Slice(0, index);

            return Utf8Parser.TryParse(keyString, out key, out var len, 'X') && len == keyString.Length;
        }

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A long representing the primary key for this grain.</returns>
        public static long GetIntegerKey(this GrainId grainId, out string keyExt)
        {
            if (!grainId.TryGetIntegerKey(out var result, out keyExt)) ThrowInvalidIntegerKeyFormat(grainId);
            return result;
        }

        /// <summary>
        /// Returns the long representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <returns>A long representing the primary key for this grain.</returns>
        public static long GetIntegerKey(this GrainId grainId)
        {
            if (!grainId.TryGetIntegerKey(out var result)) ThrowInvalidIntegerKeyFormat(grainId);
            return result;
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        public static bool TryGetGuidKey(this GrainId grainId, out Guid key, out string keyExt)
        {
            keyExt = null;
            var keyString = grainId.Key.AsSpan();
            if (keyString.Length > 32 && keyString[32] == (byte)'+')
            {
                keyExt = keyString.Slice(33).GetUtf8String();
                keyString = keyString.Slice(0, 32);
            }

            return keyString.Length == 32 && Utf8Parser.TryParse(keyString, out key, out var len, 'N') && len == 32;
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        internal static bool TryGetGuidKey(this GrainId grainId, out Guid key)
        {
            var keyString = grainId.Key.AsSpan();
            if (keyString.Length > 32 && keyString[32] == (byte)'+')
                keyString = keyString.Slice(0, 32);

            return keyString.Length == 32 && Utf8Parser.TryParse(keyString, out key, out var len, 'N') && len == 32;
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A <see cref="Guid"/> representing the primary key for this grain.</returns>
        public static Guid GetGuidKey(this GrainId grainId, out string keyExt)
        {
            if (!grainId.TryGetGuidKey(out var result, out keyExt)) ThrowInvalidGuidKeyFormat(grainId);
            return result;
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <returns>A <see cref="Guid"/> representing the primary key for this grain.</returns>
        public static Guid GetGuidKey(this GrainId grainId)
        {
            if (!grainId.TryGetGuidKey(out var result)) ThrowInvalidGuidKeyFormat(grainId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidGuidKeyFormat(GrainId grainId) => throw new ArgumentException($"Value \"{grainId}\" is not in the correct format for a Guid key.", nameof(grainId));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidIntegerKeyFormat(GrainId grainId) => throw new ArgumentException($"Value \"{grainId}\" is not in the correct format for an Integer key.", nameof(grainId));

        internal static unsafe string GetUtf8String(this ReadOnlySpan<byte> span)
        {
            fixed (byte* bytes = span) return Encoding.UTF8.GetString(bytes, span.Length);
        }
    }
}
