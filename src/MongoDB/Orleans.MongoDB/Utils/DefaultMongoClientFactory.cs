using MongoDB.Driver;

namespace Orleans.Providers.MongoDB.Utils;

/// <summary>
/// Shares the registered MongoDB client across Orleans providers.
/// </summary>
public sealed class DefaultMongoClientFactory : IMongoClientFactory
{
    private readonly IMongoClient mongoClient;

    /// <summary>
    /// Initializes the factory with the application's reusable MongoDB client.
    /// </summary>
    public DefaultMongoClientFactory(IMongoClient mongoClient)
    {
        this.mongoClient = mongoClient;
    }

    /// <inheritdoc />
    public IMongoClient Create(string name)
    {
        return mongoClient;
    }
}
