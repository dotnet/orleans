using Orleans.Runtime;

namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// Represents identity of a grain 
    /// </summary>
    internal class GrainIdentity
    {
        internal string GrainType { get; set; }
        internal int ShardKey { get; set; }
        
        /// <summary>
        /// String grain key from its grain reference
        /// </summary>
        public string GrainKey { get; set; }

        /// <summary>
        /// Createa a GrainIdentity from a grain reference and type name
        /// </summary>
        /// <param name="grainType"></param>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        public static GrainIdentity FromGrainReference(string grainType, GrainReference grainReference)
        {
            Guard.NotNullOrEmpty(grainType, "grainType");
            Guard.NotNull(grainReference, "grainReference");

            return new GrainIdentity()
            {
                GrainType = grainType,
                ShardKey = (int)grainReference.GetUniformHashCode(),
                GrainKey = grainReference.ToKeyString()
            };
        }
    }
}