using System;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Messaging;
using Orleans.Configuration;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering.EntityFrameworkCore;

internal class EFGatewayListProvider<TDbContext> : IGatewayListProvider where TDbContext : ClusterDbContext<TDbContext>
{
    private readonly ILogger _logger;
    private readonly string _clusterId;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;

    public TimeSpan MaxStaleness { get; }

    public bool IsUpdatable => true;

    public EFGatewayListProvider(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions,
        IDbContextFactory<TDbContext> dbContextFactory)
    {
        this._logger = loggerFactory.CreateLogger<EFMembershipTable<TDbContext>>();
        this._clusterId = clusterOptions.Value.ClusterId;
        this._dbContextFactory = dbContextFactory;
        this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    public Task InitializeGatewayListProvider()
    {
        if (this._logger.IsEnabled(LogLevel.Debug))
        {
            this._logger.LogDebug("EFCore Gateway list provider initialized!");
        }

        return Task.CompletedTask;
    }

    public async Task<IList<Uri>> GetGateways()
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var records = await ctx.Silos.Where(r =>
                    r.ClusterId == this._clusterId &&
                    r.Status == SiloStatus.Active &&
                    r.ProxyPort.HasValue && r.ProxyPort.Value != 0)
                .ToArrayAsync().ConfigureAwait(false);

            return records.Select(ConvertToGatewayUri).ToArray();
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error reading gateway list");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private static Uri ConvertToGatewayUri(SiloRecord record) => SiloAddress.New(new IPEndPoint(IPAddress.Parse(record.Address), record.ProxyPort!.Value), record.Generation).ToGatewayUri();
}