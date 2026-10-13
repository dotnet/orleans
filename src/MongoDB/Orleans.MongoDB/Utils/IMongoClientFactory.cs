using MongoDB.Driver;

namespace Orleans.Providers.MongoDB.Utils;

/// <summary>
/// Supplies reusable MongoDB clients to Orleans providers.
/// </summary>
public interface IMongoClientFactory
{
    /// <summary>
    /// Gets the client selected by the provider's configured client name.
    /// </summary>
    /// <param name="name">The configured client name or provider-specific default name.</param>
    /// <returns>A client whose lifetime is managed by the factory or dependency injection container.</returns>
    IMongoClient Create(string name);
}
