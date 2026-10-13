
using System;

namespace Orleans.Providers.MongoDB.Configuration;

/// <summary>
/// Configures MongoDB Reminders Options.
/// </summary>
public sealed class MongoDBRemindersOptions : MongoDBOptions
{
    public MongoDBRemindersOptions()
    {
    }

    /// <summary>
    /// Gets or sets whether the provider creates reminder indexes and removes legacy indexes.
    /// Defaults to <see langword="true"/>. Set to <see langword="false"/> when indexes are managed externally.
    /// </summary>
    public bool CreateIndexes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether all legacy indexes should be removed from the MongoDB collections.
    /// </summary>
    /// <remarks>
    /// Legacy indexes are indexes that are no longer needed or do not contribute to query execution efficiency.
    /// When this property is set to <c>true</c>, the system will attempt to clean up such indexes during initialization
    /// to optimize database performance and storage usage. This setting is useful for controlling when indexes are
    /// reclaimed during a rolling deployment of Orleans silos.
    /// </remarks>
    [Obsolete("This will be removed in future version")]
    public bool PurgeLegacyIndexes { get; set; } = false;
}
