using System.Net;
using Orleans.Messaging;
using Orleans.Clustering.CosmosDB.Models;

namespace Orleans.Clustering.CosmosDB;

internal class AzureCosmosDBGatewayListProvider : IGatewayListProvider
{
    private readonly ILogger _logger;
    private readonly string _clusterId;
    private readonly IServiceProvider _serviceProvider;
    private readonly AzureCosmosDBClusteringOptions _options;
    private readonly QueryRequestOptions _queryRequestOptions;
    private Container _container = default!;

    public TimeSpan MaxStaleness { get; }

    public bool IsUpdatable => true;

    public AzureCosmosDBGatewayListProvider(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<AzureCosmosDBClusteringOptions> options,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions
    )
    {
        this._logger = loggerFactory.CreateLogger<AzureCosmosDBGatewayListProvider>();
        this._serviceProvider = serviceProvider;
        this._clusterId = clusterOptions.Value.ClusterId;
        this._options = options.Value;
        this._queryRequestOptions = new QueryRequestOptions { PartitionKey = new(this._clusterId) };
        this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    public async Task InitializeGatewayListProvider()
    {
        try
        {
            var cosmos = await AzureCosmosDBConnectionFactory.CreateCosmosClient(this._serviceProvider, this._options);
            this._container = cosmos.GetContainer(this._options.Database, this._options.Container);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error initializing Azure CosmosDB Gateway list provider.");
            throw;
        }
    }

    public async Task<IList<Uri>> GetGateways()
    {
        try
        {
            var query = this._container
                .GetItemLinqQueryable<SiloEntity>(requestOptions: this._queryRequestOptions)
                .Where(g => g.EntityType == nameof(SiloEntity) &&
                    g.Status == (int)SiloStatus.Active &&
                    g.ProxyPort.HasValue && g.ProxyPort.Value != 0).ToFeedIterator();

            var entities = new List<SiloEntity>();
            do
            {
                var items = await query.ReadNextAsync();
                entities.AddRange(items);
            } while (query.HasMoreResults);

            var uris = entities.Select(ConvertToGatewayUri).ToArray();
            return uris;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error reading Orleans Gateway list from Azure CosmosDB");
            throw;
        }
    }

    private static Uri ConvertToGatewayUri(SiloEntity gateway) =>
        SiloAddress.New(new IPEndPoint(IPAddress.Parse(gateway.Address), gateway.ProxyPort!.Value), gateway.Generation).ToGatewayUri();
}