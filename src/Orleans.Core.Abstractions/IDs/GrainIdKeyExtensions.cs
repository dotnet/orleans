using System;
using System.Globalization;
using System.Runtime.CompilerServices;

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
        public static IdSpan CreateIntegerKey(long key) => IdSpan.Create(key.ToString("X"));

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="long"/> key.
        /// </summary>
        public static IdSpan CreateIntegerKey(long key, string keyExtension) => string.IsNullOrWhiteSpace(keyExtension) ? CreateIntegerKey(key) : IdSpan.Create($"{key:X}+{keyExtension}");

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="Guid"/> key.
        /// </summary>
        public static IdSpan CreateGuidKey(Guid key) => IdSpan.Create(key.ToString("N"));

        /// <summary>
        /// Creates an <see cref="IdSpan"/> representing a <see cref="Guid"/> key.
        /// </summary>
        public static IdSpan CreateGuidKey(Guid key, string keyExtension) => string.IsNullOrWhiteSpace(keyExtension) ? CreateGuidKey(key) : IdSpan.Create($"{key:N}+{keyExtension}");

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain primary key.
        /// </summary>
        public static bool TryGetIntegerKey(this GrainId grainId, out long key, out string keyExt)
        {
            var keyString = grainId.Key.ToStringUtf8();
            if (keyString.IndexOf('+') is int index && index >= 0)
            {
                keyExt = keyString.Substring(index + 1);
                return long.TryParse(keyString.Substring(0, index), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key);
            }

            keyExt = default;
            return long.TryParse(keyString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key);
        }

        /// <summary>
        /// Returns the <see cref="long"/> representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <param name="keyExt">The output parameter to return the extended key part of the grain primary key, if extended primary key was provided for that grain.</param>
        /// <returns>A long representing the primary key for this grain.</returns>
        public static long GetIntegerKey(this GrainId grainId, out string keyExt)
        {
            if (!grainId.TryGetIntegerKey(out var result, out keyExt))
            {
                ThrowInvalidIntegerKeyFormat(grainId);
            }

            return result;
        }

        /// <summary>
        /// Returns the long representation of a grain primary key.
        /// </summary>
        /// <param name="grainId">The grain to find the primary key for.</param>
        /// <returns>A long representing the primary key for this grain.</returns>
        public static long GetIntegerKey(this GrainId grainId) => GetIntegerKey(grainId, out _);

        /// <summary>
        /// Returns the <see cref="Guid"/> representation of a grain primary key.
        /// </summary>
        public static bool TryGetGuidKey(this GrainId grainId, out Guid key, out string keyExt)
        {
            var keyString = grainId.Key.ToStringUtf8();
            if (keyString.IndexOf('+') is int index && index >= 0)
            {
                keyExt = keyString.Substring(index + 1);
                return Guid.TryParse(keyString.Substring(0, index), out key);
            }

            keyExt = default;
            return Guid.TryParse(keyString, out key);
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
        public static Guid GetGuidKey(this GrainId grainId) => GetGuidKey(grainId, out _);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidGuidKeyFormat(GrainId grainId) => throw new ArgumentException($"Value \"{grainId}\" is not in the correct format for a Guid key.", nameof(grainId));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidIntegerKeyFormat(GrainId grainId) => throw new ArgumentException($"Value \"{grainId}\" is not in the correct format for an Integer key.", nameof(grainId));
    }
}
