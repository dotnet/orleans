using Orleans.Serialization;

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

        /// <summary>
        /// Creates a grain reference from a storage key info struct.
        /// </summary>
        /// <param name="keyInfo">The key info.</param>
        /// <returns>The newly created grain reference.</returns>
        GrainReference GetGrainFromKeyInfo(GrainReferenceKeyInfo keyInfo);
    }
}