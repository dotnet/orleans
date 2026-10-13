
namespace Orleans.Providers.MongoDB.Configuration;

/// <summary>
/// Configures MongoDB Gateway List Provider.
/// </summary>
public sealed class MongoDBGatewayListProviderOptions : MongoDBOptions
{
    /// <summary>
    /// Gets or sets the membership format used by the silos whose gateways are discovered.
    /// </summary>
    public MongoDBMembershipStrategy Strategy { get; set; }

    public MongoDBGatewayListProviderOptions()
    {
    }
}
