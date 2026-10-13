using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Runtime;

namespace Orleans.Persistence.MongoDB;

/// <summary>
/// Option to configure MongoDB Storage.
/// </summary>
public class MongoDBGrainStorageOptions : MongoDBOptions
{
    public MongoDBGrainStorageOptions()
    {
        CollectionPrefix = "Grains";
    }

    /// <summary>
    /// Gets or sets grain state serializer for the storage provider.
    /// </summary>
    public IGrainStateSerializer GrainStateSerializer { get; set; } = null!;

    /// <summary>
    /// The key generation strategy used by the storage provider. It defaults to calling ToKeyString
    /// in the grain reference.
    /// </summary>
    public GrainStorageKeyGenerator KeyGenerator { get; set; } = x => x.ToString();

    internal override void Validate(string? name = null)
    {
        base.Validate(name);

        if (KeyGenerator == null)
        {
            throw new OrleansConfigurationException($"{nameof(KeyGenerator)} is required and cannot be null.");
        }
    }
}

/// <summary>
/// Delegate representing a strategy for generating the key of the persisted state in MongoDB.
/// </summary>
public delegate string GrainStorageKeyGenerator(GrainId grainId);
