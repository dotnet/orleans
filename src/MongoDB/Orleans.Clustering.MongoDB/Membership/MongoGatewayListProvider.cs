using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Providers.MongoDB.Membership.Store;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Configuration;
using MongoDB.Driver;


namespace Orleans.Providers.MongoDB.Membership;

/// <summary>
/// Discovers active Orleans client gateways from MongoDB cluster membership.
/// </summary>
public sealed class MongoGatewayListProvider : IGatewayListProvider
{
    private readonly IMongoClient mongoClient;
    private readonly ILogger<MongoGatewayListProvider> logger;
    private readonly MongoDBGatewayListProviderOptions options;
    private readonly string clusterId;
    private IMongoMembershipCollection gatewaysCollection = null!;

    /// <inheritdoc />
    public bool IsUpdatable { get; } = true;

    /// <inheritdoc />
    public TimeSpan MaxStaleness { get; }

    /// <summary>
    /// Initializes gateway discovery using the silo membership format and client cluster identity.
    /// </summary>
    public MongoGatewayListProvider(
        IMongoClientFactory mongoClientFactory,
        ILogger<MongoGatewayListProvider> logger,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<MongoDBGatewayListProviderOptions> options)
    {
        this.mongoClient = mongoClientFactory.Create(options.Value, "Membership");
        this.logger = logger;
        this.options = options.Value;
        this.clusterId = clusterOptions.Value.ClusterId;
        this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    /// <inheritdoc />
    public Task InitializeGatewayListProvider()
    {
        CreateCollection();

        return Task.CompletedTask;
    }

    private void CreateCollection()
    {
        gatewaysCollection = Factory.CreateCollection(mongoClient, options, options.Strategy);
    }

    /// <inheritdoc />
    public Task<IList<Uri>> GetGateways()
    {
        return DoAndLog(nameof(GetGateways), () =>
        {
            return gatewaysCollection.GetGateways(clusterId);
        });
    }

    private async Task<T> DoAndLog<T>(string actionName, Func<Task<T>> action)
    {
        logger.LogDebug($"{nameof(MongoGatewayListProvider)}.{actionName} called.");

        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning((int)MongoProviderErrorCode.MembershipTable_Operations, ex, $"{nameof(MongoGatewayListProvider)}.{actionName} failed. Exception={ex.Message}");

            throw;
        }
    }
}
