using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        /// The key extension.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key and key extension.
        /// </returns>
        public static IdSpan CreateIntegerKey(long key, string keyExtension)
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
        /// The key extension.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> representing the provided key and key extension.
        /// </returns>
        public static IdSpan CreateGuidKey(Guid key, string keyExtension)
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

        /// <summary>
        /// Tries to parse the <see cref="GrainId.Key"/> portion of the provided grain id to extract a <see cref="long"/> key and <see cref="string"/> key extension.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="keyExt">
        /// The key extension.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the grain id was successfully parsed, <see langword="false" /> otherwise.
        /// </returns>
        public static bool TryGetIntegerKey(this GrainId grainId, out long key, [NotNullWhen(true)] out string keyExt)
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
        /// Tries to parse the <see cref="GrainId.Key"/> portion of the provided grain id to extract a <see cref="long"/> key.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the grain id was successfully parsed, <see langword="false" /> otherwise.
        /// </returns>
        internal static bool TryGetIntegerKey(this GrainId grainId, out long key)
        {
            var keyString = grainId.Key.AsSpan();
            if (keyString.IndexOf((byte)'+') is int index && index >= 0)
                keyString = keyString.Slice(0, index);

            return Utf8Parser.TryParse(keyString, out key, out var len, 'X') && len == keyString.Length;
        }

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain key.
        /// </summary>
        /// <param name="grainId">The grain id.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A long representing the key for this grain.</returns>
        public static long GetIntegerKey(this GrainId grainId, out string keyExt)
        {
            if (!grainId.TryGetIntegerKey(out var result, out keyExt))
            {
                ThrowInvalidIntegerKeyFormat(grainId);
            }

            return result;
        }

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain key.
        /// </summary>
        /// <param name="grainId">The grain to find the key for.</param>
        /// <returns>A <see cref="long"/> representing the key for this grain.</returns>
        public static long GetIntegerKey(this GrainId grainId)
        {
            if (!grainId.TryGetIntegerKey(out var result))
            {
                ThrowInvalidIntegerKeyFormat(grainId);
            }

            return result;
        }

        /// <summary>
        /// Tries to parse the <see cref="GrainId.Key"/> portion of the provided grain id to extract a <see cref="Guid"/> key and <see cref="string"/> key extension.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="keyExt">
        /// The key extension.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the grain id was successfully parsed, <see langword="false" /> otherwise.
        /// </returns>
        public static bool TryGetGuidKey(this GrainId grainId, out Guid key, out string keyExt)
        {
            keyExt = null;
            var keyString = grainId.Key.AsSpan();
            if (keyString.Length > 32 && keyString[32] == (byte)'+')
            {
                keyExt = keyString.Slice(33).GetUtf8String();
                keyString = keyString.Slice(0, 32);
            }
            else if(keyString.Length != 32)
            {
                key = default;
                return false;
            }

            return Utf8Parser.TryParse(keyString, out key, out var len, 'N') && len == 32;
        }

        /// <summary>
        /// Tries to parse the <see cref="GrainId.Key"/> portion of the provided grain id to extract a <see cref="Guid"/> key.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the grain id was successfully parsed, <see langword="false" /> otherwise.
        /// </returns>
        internal static bool TryGetGuidKey(this GrainId grainId, out Guid key)
        {
            var keyString = grainId.Key.AsSpan();
            if (keyString.Length > 32 && keyString[32] == (byte)'+')
            {
                keyString = keyString.Slice(0, 32);
            }
            else if (keyString.Length != 32)
            {
                key = default;
                return false;
            }

            return Utf8Parser.TryParse(keyString, out key, out var len, 'N') && len == 32;
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A <see cref="Guid"/> representing the primary key for this grain.</returns>
        public static Guid GetGuidKey(this GrainId grainId, out string keyExt)
        {
            if (!grainId.TryGetGuidKey(out var result, out keyExt))
            {
                ThrowInvalidGuidKeyFormat(grainId);
            }

            return result;
        }

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <returns>A <see cref="Guid"/> representing the primary key for this grain.</returns>
        public static Guid GetGuidKey(this GrainId grainId)
        {
            if (!grainId.TryGetGuidKey(out var result))
            {
                ThrowInvalidGuidKeyFormat(grainId);
            }

            return result;
        }

        /// <summary>
        /// Throws an exception indicating that a <see cref="Guid"/>-based grain id was incorrectly formatted.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidGuidKeyFormat(GrainId grainId) => throw new ArgumentException($"Value \"{grainId}\" is not in the correct format for a Guid key.", nameof(grainId));

        /// <summary>
        /// Throws an exception indicating that a <see cref="long"/>-based grain id was incorrectly formatted.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidIntegerKeyFormat(GrainId grainId) => throw new ArgumentException($"Value \"{grainId}\" is not in the correct format for an Integer key.", nameof(grainId));

        /// <summary>
        /// Parses the provided value as a UTF8 string and returns the <see cref="string"/> representation of it.
        /// </summary>
        /// <param name="span">
        /// The span to convert.
        /// </param>
        /// <returns>
        /// The <see cref="string"/> representation of the input.
        /// </returns>
        internal static unsafe string GetUtf8String(this ReadOnlySpan<byte> span)
        {
            fixed (byte* bytes = span) return Encoding.UTF8.GetString(bytes, span.Length);
        }
    }
}
