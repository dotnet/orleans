
namespace Orleans.Clustering.MongoDB;

/// <summary>
/// Configures MongoDB Membership.
/// </summary>
public sealed class MongoDBMembershipTableOptions : MongoDBOptions
{
    /// <summary>
    /// Gets or sets the membership format. Defaults to <see cref="MongoDBMembershipStrategy.SingleDocument"/>.
    /// </summary>
    public MongoDBMembershipStrategy Strategy { get; set; }

    public MongoDBMembershipTableOptions()
    {
    }
}
