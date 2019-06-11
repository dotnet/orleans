using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans;
using Orleans.Runtime;

namespace Silo
{
    public class ClusterHealthCheck : IHealthCheck
    {
        private readonly IClusterClient client;

        public ClusterHealthCheck(IClusterClient client)
        {
            this.client = client;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var manager = client.GetGrain<IManagementGrain>(0);
            try
            {
                var hosts = await manager.GetHosts();
                var count = hosts.Values.Where(x => x.IsUnavailable()).Count();
                return count > 0 ? HealthCheckResult.Degraded($"{count} silo(s) unavailable") : HealthCheckResult.Healthy();
            }
            catch (Exception error)
            {
                return HealthCheckResult.Unhealthy("Failed to get cluster status", error);
            }
        }
    }
}
