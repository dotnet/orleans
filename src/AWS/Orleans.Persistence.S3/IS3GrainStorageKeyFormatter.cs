using Orleans.Runtime;

namespace Orleans.Persistence.S3
{
    public interface IS3GrainStorageKeyFormatter
    {
        string FormatKey(string name, string grainType, GrainReference grainReference);
    }
}