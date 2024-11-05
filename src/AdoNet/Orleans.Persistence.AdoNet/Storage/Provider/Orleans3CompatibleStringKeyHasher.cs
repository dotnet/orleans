using System;
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

            // reducing allocations if data is not a grain type
            if (data.Length >= _grainType.Length && Encoding.UTF8.GetByteCount(_grainType) == data.Length)
            {
                var grainTypeBytes = Encoding.UTF8.GetBytes(_grainType);
                if (grainTypeBytes.AsSpan().SequenceEqual(data))
                    return _innerHasher.Hash(data);
            }

            var extendedData = new byte[data.Length + 8];
            data.CopyTo(extendedData, 0);
            return _innerHasher.Hash(extendedData);
        }
    }
}
