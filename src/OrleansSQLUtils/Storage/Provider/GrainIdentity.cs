using Orleans.Runtime;

namespace Orleans.SqlUtils.StorageProvider
{
    public class GrainIdentity
    {
        public string GrainType { get; set; }
        public int ShardKey { get; set; }
        public string GrainKey { get; set; }

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