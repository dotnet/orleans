using MongoDB.Driver;
using Orleans.Runtime;
using System;


#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.MongoDB;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.MongoDB;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.MongoDB;
#else
#error Define a MongoDB provider namespace.
#endif

/// <summary>
/// Options to configure MongoDB for Orleans
/// </summary>
public class MongoDBOptions
{
    /// <summary>
    /// Gets or sets the database name.
    /// Configuration-based registration derives an omitted name from the matching registered <see cref="IMongoDatabase"/>.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key of a registered <see cref="IMongoClient"/>.
    /// An unset or whitespace value selects the unkeyed client.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// The collection prefix.
    /// </summary>
    public string? CollectionPrefix { get; set; }

    /// <summary>
    /// True, to create a shard key when using with cosmos db.
    /// </summary>
    public bool CreateShardKeyForCosmos { get; set; }

    /// <summary>
    /// The collection configurator.
    /// </summary>
    public Action<MongoCollectionSettings>? CollectionConfigurator { get; set; }

    internal virtual void Validate(string? name = null)
    {
        var typeName = GetType().Name;

        if (!string.IsNullOrWhiteSpace(name))
        {
            typeName = $"{typeName} for {name}";
        }

        if (string.IsNullOrWhiteSpace(DatabaseName))
        {
            throw new OrleansConfigurationException($"Invalid {typeName} values for {nameof(DatabaseName)}. {nameof(DatabaseName)} is required.");
        }
    }
}
