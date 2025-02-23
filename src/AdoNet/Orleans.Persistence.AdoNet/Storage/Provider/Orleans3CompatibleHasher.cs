using System;

namespace Orleans.Storage
{
    /// <summary>
    /// Orleans v3-compatible hasher implementation for non-string-only grain key ids.
    /// </summary>
    internal class Orleans3CompatibleHasher : IHasher
    {
        /// <summary>
        /// <see cref="IHasher.Description"/>
        /// </summary>
        public string Description { get; } = $"Orleans v3 hash function ({nameof(JenkinsHash)}).";

        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(byte[] data) => Hash(data.AsSpan());

        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(ReadOnlySpan<byte> data)
        {
            // implementation restored from Orleans v3.7.2: https://github.com/dotnet/orleans/blob/b24e446abfd883f0e4ed614f5267eaa3331548dc/src/AdoNet/Orleans.Persistence.AdoNet/Storage/Provider/OrleansDefaultHasher.cs
            return unchecked((int)JenkinsHash.ComputeHash(data));
        }
    }
}