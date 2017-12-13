namespace Orleans.Storage
{
    /// <summary>
    /// An interface for all the hashing operations currently in Orleans Storage operations.
    /// </summary>
    /// <remarks>Implement this to provide a hasher for database key with specific properties.
    /// As for an example: collision resistance on out-of-control ID providers.</remarks>
    public interface IHasher
    {
        /// <summary>
        /// Description of the hashing functionality.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// The hash.
        /// </summary>
        /// <param name="data">The data to hash.</param>
        /// <returns>The given bytes hashed.</returns>
        int Hash(byte[] data);
    }
}
