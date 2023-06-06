using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Configuration;

namespace Orleans.Clustering.GoogleFirestore;

internal class GoogleFirestoreGatewayListProvider : IGatewayListProvider
{
    private readonly FirestoreOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _clusterId;

    private OrleansSiloInstanceManager _siloInstanceManager = default!;

    public TimeSpan MaxStaleness { get; }
    public bool IsUpdatable => true;

    public GoogleFirestoreGatewayListProvider(
        ILoggerFactory loggerFactory,
        IOptions<FirestoreOptions> options,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions)
    {
        this._loggerFactory = loggerFactory;
        this._options = options.Value;
        this._clusterId = clusterOptions.Value.ClusterId;
        this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    public Task<IList<Uri>> GetGateways() => this._siloInstanceManager.FindAllGatewayProxyEndpoints();

    public async Task InitializeGatewayListProvider()
    {
        this._siloInstanceManager = await OrleansSiloInstanceManager.GetManager(
            this._clusterId,
            this._loggerFactory,
            this._options);
    }
}