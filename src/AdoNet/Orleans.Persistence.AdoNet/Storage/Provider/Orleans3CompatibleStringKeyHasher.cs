using System;
using System.Buffers;
using System.Text;

namespace Orleans.Storage
{
    /// <summary>
    /// Orleans v3-compatible hasher implementation for string-only grain key ids.
    /// </summary>
    internal class Orleans3CompatibleStringKeyHasher : IHasher
    {
        private readonly Orleans3CompatibleHasher _innerHasher;
        private readonly string _grainType;

        public Orleans3CompatibleStringKeyHasher(Orleans3CompatibleHasher innerHasher, string grainType)
        {
            _innerHasher = innerHasher;
            _grainType = grainType;
        }

        /// <summary>
        /// <see cref="IHasher.Description"/>
        /// </summary>
        public string Description { get; } = $"Orleans v3 hash function ({nameof(JenkinsHash)}).";

        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(byte[] data)
        {
            // Orleans v3 treats string-only keys as integer keys with extension (AdoGrainKey.IsLongKey == true),
            // so data must be extended for string-only grain keys.
            // But AdoNetGrainStorage implementation also uses such code:
            //    ...
            //    var grainIdHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes());
            //    var grainTypeHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType));
            //    ...
            // PickHasher parameters are the same for both calls so we need to analyze data content to distinguish these cases.
            // It doesn't word if string key is equal to grain type name, but we consider this edge case to be negligibly rare.

            if (IsGrainTypeName(data))
                return _innerHasher.Hash(data);

            var extendedLength = data.Length + 8;
            if (extendedLength <= 256)
            {
                Span<byte> extended = stackalloc byte[extendedLength];
                data.AsSpan().CopyTo(extended);
                return _innerHasher.Hash(extended);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(extendedLength);
            try
            {
                data.AsSpan().CopyTo(buffer);
                Array.Clear(buffer, data.Length, 8);
                return _innerHasher.Hash(buffer.AsSpan(0, extendedLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private bool IsGrainTypeName(byte[] data)
        {
            // at least 1 byte per char
            if (data.Length < _grainType.Length)
                return false;

            var grainTypeByteCount = Encoding.UTF8.GetByteCount(_grainType);
            if (grainTypeByteCount != data.Length)
                return false;

            if (grainTypeByteCount <= 256)
            {
                Span<byte> grainTypeBytes = stackalloc byte[grainTypeByteCount];
                if (!Encoding.UTF8.TryGetBytes(_grainType, grainTypeBytes, out _))
                    throw new InvalidOperationException();

                return grainTypeBytes.SequenceEqual(data);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(grainTypeByteCount);
            try
            {
                var grainTypeBytes = buffer.AsSpan(0, grainTypeByteCount);

                if (!Encoding.UTF8.TryGetBytes(_grainType, grainTypeBytes, out _))
                    throw new InvalidOperationException();

                return grainTypeBytes.SequenceEqual(data);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
