using Orleans.Runtime;

namespace Orleans.Persistence.S3
{
    public class DefaultS3GrainStorageKeyFormatter : IS3GrainStorageKeyFormatter
    {
        public string FormatKey(string name, string grainType, GrainReference grainReference) => $"{name}/{grainReference.ToShortKeyString()}/{grainType}";
    }
}