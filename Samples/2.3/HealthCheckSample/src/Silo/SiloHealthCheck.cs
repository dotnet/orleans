using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans.Runtime;

namespace Silo
{
    public class SiloHealthCheck : IHealthCheck
    {
        private readonly IMembershipOracle oracle;

        private static long lastCheckTime = DateTime.UtcNow.ToBinary();

        public SiloHealthCheck(IMembershipOracle oracle)
        {
            this.oracle = oracle;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var thisLastCheckTime = DateTime.FromBinary(Interlocked.Exchange(ref lastCheckTime, DateTime.UtcNow.ToBinary()));

            var ok = oracle.CheckHealth(thisLastCheckTime);
            return ok ? Task.FromResult(HealthCheckResult.Healthy()) : Task.FromResult(HealthCheckResult.Degraded());
        }
    }
}
