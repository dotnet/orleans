using Orleans.Storage;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Returns a constant hash value to all input. This is used to induce hash collisions
    /// one scenarios that involve more than one case.
    /// </summary>
    public class ConstantHasher: IHasher
    {
        /// <summary>
        /// The hash value to which every input will be hashed to.
        /// </summary>
        public const int ConstantHash = 1;

        /// <summary>
        /// <see cref="IHasher.Description"/>.
        /// </summary>
        public string Description { get; } = $"Returns {ConstantHash} to all input, thus inducing hash collisions.";

        /// <summary>
        /// <see cref="IHasher.Hash(byte[])"/>.
        /// </summary>
        public int Hash(byte[] data) { return ConstantHash; }
    }
}
