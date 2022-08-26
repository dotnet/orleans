namespace Orleans.Storage
{
    /// <summary>
    /// A default implementation uses the same hash as Orleans in grains placement.
    /// </summary>
    public sealed class OrleansDefaultHasher: IHasher
    {
        /// <summary>
        /// <see cref="IHasher.Description"/>
        /// </summary>
        public string Description => $"The default Orleans hash function ({nameof(StableHash)}).";

        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(byte[] data) => (int)StableHash.ComputeHash(data);
    }
}
