namespace Orleans.Storage
{
    /// <summary>
    /// A default implementation uses the same hash as Orleans in grains placement.
    /// </summary>
    public class OrleansDefaultHasher: IHasher
    {
        /// <summary>
        /// <see cref="IHasher.Description"/>
        /// </summary>
        public string Description { get; } = $"The default Orleans hash function ({nameof(JenkinsHash)}).";


        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(byte[] data)
        {
            return unchecked((int)JenkinsHash.ComputeHash(data));
        }
    }
}
