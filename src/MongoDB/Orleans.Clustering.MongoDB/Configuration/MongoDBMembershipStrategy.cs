namespace Orleans.Clustering.MongoDB;

/// <summary>
/// Selects the collection format and atomicity mechanism used for MongoDB membership.
/// </summary>
public enum MongoDBMembershipStrategy
{
    /// <summary>
    /// Stores all cluster members and the table version in one atomically updated document.
    /// </summary>
    SingleDocument,
    /// <summary>
    /// Stores individual silo documents and updates membership using replica-set transactions.
    /// </summary>
    Multiple,
    /// <summary>
    /// Uses the contributed provider's legacy per-silo format for existing deployments.
    /// </summary>
    MultipleDeprecated
}
