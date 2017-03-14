namespace Orleans.Runtime
{
    public interface IGrainReferenceConverter
    {
        /// <summary>
        /// Creates a grain reference from a storage key string.
        /// </summary>
        /// <param name="key">The key string.</param>
        /// <returns>The newly created grain reference.</returns>
        GrainReference GetGrainFromKeyString(string key);
    }
}