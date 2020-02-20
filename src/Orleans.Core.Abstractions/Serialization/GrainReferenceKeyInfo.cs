using System;
using System.Net;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// Type is a low level representation of grain reference keys to enable
    /// space-efficient serialization of grain references.
    /// </summary>
    /// <remarks>
    /// This type is not intended for general use. It's a highly specialized type
    /// for serializing and deserializing grain references, and as such is not
    /// generally something you should pass around in your application.
    /// </remarks>
    public readonly struct GrainReferenceKeyInfo
    {
        /// <summary>
        /// Observer id. Only applicable if <see cref="HasObserverId"/> is true.
        /// </summary>
        /// <remarks>In most cases, is Guid.Empty.</remarks>
        public Guid ObserverId { get; }

        /// <summary>
        /// Target silo. Only applicable if <see cref="HasTargetSilo"/> is true.
        /// </summary>
        /// <remarks>In most cases, is <c>(null, 0)</c>.</remarks>
        public (IPEndPoint endpoint, int generation) TargetSilo { get; }

        /// <summary>
        /// Generic argument. Only applicable if <see cref="HasGenericArgument"/> is true.
        /// </summary>
        /// <remarks>In most cases, is <c>null</c>.</remarks>
        public string GenericArgument { get; }

        /// <summary>
        /// Grain key. For more specialized views, see <c>KeyAs*</c> methods and <c>KeyFrom*</c>.
        /// </summary>
        public (ulong, ulong, ulong, string) Key { get; }

        /// <summary>
        /// Whether or not key info has observer id.
        /// </summary>
        public bool HasObserverId => ObserverId != Guid.Empty;

        /// <summary>
        /// Whether or not key info has silo endpoint.
        /// </summary>
        public bool HasTargetSilo => TargetSilo.endpoint != null;

        /// <summary>
        /// Whether or not key info has generic argument.
        /// </summary>
        public bool HasGenericArgument => GenericArgument != null;

        /// <summary>
        /// Whether or not key info has a long key.
        /// </summary>
        public bool IsLongKey => Key.Item1 == 0;

        /// <summary>
        /// Whether or not key info has key extension.
        /// </summary>
        public bool HasKeyExt
        {
            get
            {
                var category = UniqueKey.GetCategory(Key.Item3);
                return category == UniqueKey.Category.KeyExtGrain;
            }
        }

        public (Guid key, ulong typeCode) KeyAsGuid()
        {
            if (HasKeyExt)
            {
                throw new InvalidOperationException("Key has string extension");
            }

            return (ToGuid(Key), Key.Item3);
        }

        public static (ulong, ulong, ulong, string) KeyFromGuid(Guid key, ulong typeCode)
        {
            var (n0, n1) = FromGuid(key);
            return (n0, n1, typeCode, null);
        }

        public (Guid key, string ext, ulong typeCode) KeyAsGuidWithExt()
        {
            return (ToGuid(Key), Key.Item4, Key.Item3);
        }

        public static (ulong, ulong, ulong, string) KeyFromGuidWithExt(Guid key, string ext, ulong typeCode)
        {
            var (n0, n1) = FromGuid(key);
            return (n0, n1, typeCode, ext);
        }

        public (long key, ulong typeCode) KeyAsLong()
        {
            if (HasKeyExt)
            {
                throw new InvalidOperationException("Key has string extension");
            }

            if (!IsLongKey)
            {
                throw new InvalidOperationException("Key is not a long key");
            }

            return (unchecked((long)Key.Item2), Key.Item3);
        }

        public static (ulong, ulong, ulong, string) KeyFromLong(long key, ulong typeCode)
        {
            var n1 = unchecked((ulong)key);
            return (0, n1, typeCode, null);
        }

        public (long key, string ext, ulong typeCode) KeyAsLongWithExt()
        {
            if (!IsLongKey)
            {
                throw new InvalidOperationException("Key is not a long key");
            }

            return (unchecked((long)Key.Item2), Key.Item4, Key.Item3);
        }

        public static (ulong, ulong, ulong, string) KeyFromLongWithExt(long key, string ext, ulong typeCode)
        {
            var n1 = unchecked((ulong)key);
            return (0, n1, typeCode, ext);
        }

        public (string key, ulong typeCode) KeyAsString()
        {
            if (Key.Item1 != 0 || Key.Item2 != 0)
            {
                throw new InvalidOperationException("Key is not a string key");
            }

            if (!HasKeyExt)
            {
                throw new InvalidOperationException("Key has no string extension");
            }

            return (Key.Item4, Key.Item3);
        }

        public static (ulong, ulong, ulong, string) KeyFromString(string key, ulong typeCode)
        {
            return KeyFromLongWithExt(0L, key, typeCode);
        }

        public GrainReferenceKeyInfo((ulong, ulong, ulong, string) key)
        {
            Key = key;
            ObserverId = Guid.Empty;
            TargetSilo = (null, 0);
            GenericArgument = null;
        }

        public GrainReferenceKeyInfo((ulong, ulong, ulong, string) key, Guid observerId)
        {
            Key = key;
            ObserverId = observerId;
            TargetSilo = (null, 0);
            GenericArgument = null;
        }

        public GrainReferenceKeyInfo((ulong, ulong, ulong, string) key, (IPEndPoint endpoint, int generation) targetSilo)
        {
            Key = key;
            ObserverId = Guid.Empty;
            TargetSilo = targetSilo;
            GenericArgument = null;
        }

        public GrainReferenceKeyInfo((ulong, ulong, ulong, string) key, string genericArgument)
        {
            Key = key;
            ObserverId = Guid.Empty;
            TargetSilo = (null, 0);
            GenericArgument = genericArgument;
        }

        private static Guid ToGuid((ulong n0, ulong n1, ulong, string) key) =>
            new Guid(
                (uint)(key.n0 & 0xffffffff),
                (ushort)(key.n0 >> 32),
                (ushort)(key.n0 >> 48),
                (byte)key.n1,
                (byte)(key.n1 >> 8),
                (byte)(key.n1 >> 16),
                (byte)(key.n1 >> 24),
                (byte)(key.n1 >> 32),
                (byte)(key.n1 >> 40),
                (byte)(key.n1 >> 48),
                (byte)(key.n1 >> 56));

        private static (ulong, ulong) FromGuid(Guid guid)
        {
            var guidBytes = guid.ToByteArray();
            var n0 = BitConverter.ToUInt64(guidBytes, 0);
            var n1 = BitConverter.ToUInt64(guidBytes, 8);
            return (n0, n1);
        }
    }
}
