using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Configuration;

namespace Orleans.Clustering.Firestore;

internal partial class FirestoreGatewayListProvider : IGatewayListProvider
{
    private const string ClusterGroup = "Cluster";
    private readonly FirestoreOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _clusterId;

    private FirestoreDataManager _storage = default!;

    public TimeSpan MaxStaleness { get; }
    public bool IsUpdatable => true;

    public FirestoreGatewayListProvider(
        ILoggerFactory loggerFactory,
        IOptions<FirestoreOptions> options,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions)
    {
        this._loggerFactory = loggerFactory;
        this._logger = loggerFactory.CreateLogger<FirestoreGatewayListProvider>();
        this._options = options.Value;
        this._clusterId = clusterOptions.Value.ClusterId;
        this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    public Task<IList<Uri>> GetGateways() => GetGatewaysAsync(CancellationToken.None);

    public async Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogSearchingForGateways(this._clusterId);

        try
        {
            var results = await this._storage.QueryEntities<SiloInstanceEntity>(
                collection => collection.WhereEqualTo(nameof(SiloInstanceEntity.Status), (int)SiloStatus.Active),
                cancellationToken);
            var gateways = results
                .Where(silo => silo.ProxyPort > 0)
                .Select(ConvertToGatewayUri)
                .ToList();

            LogFoundGateways(gateways.Count, this._clusterId);
            return gateways;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogErrorSearchingForGateways(exception, this._clusterId);
            throw;
        }
    }

    public Task InitializeGatewayListProvider() => InitializeGatewayListProviderAsync(CancellationToken.None);

    public async Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this._storage = new FirestoreDataManager(
            ClusterGroup,
            Utils.SanitizeId(this._clusterId),
            this._options,
            this._loggerFactory.CreateLogger<FirestoreDataManager>());
        await this._storage.Initialize(cancellationToken);
    }

    private static Uri ConvertToGatewayUri(SiloInstanceEntity gateway) =>
        SiloAddress.New(IPAddress.Parse(gateway.Address), gateway.ProxyPort, gateway.Generation).ToGatewayUri();

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100277,
        Level = LogLevel.Debug,
        Message = "Searching for active gateway silos for deployment {DeploymentId}.")]
    private partial void LogSearchingForGateways(string deploymentId);

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100278,
        Level = LogLevel.Debug,
        Message = "Found {GatewaySiloCount} active Gateway Silos for deployment {DeploymentId}.")]
    private partial void LogFoundGateways(int gatewaySiloCount, string deploymentId);

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100331,
        Level = LogLevel.Error,
        Message = "Error searching for active gateway silos for deployment {DeploymentId}")]
    private partial void LogErrorSearchingForGateways(Exception exception, string deploymentId);
}
