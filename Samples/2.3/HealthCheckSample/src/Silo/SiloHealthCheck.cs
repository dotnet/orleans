using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans.Runtime;

namespace Silo
{
    public class SiloHealthCheck : IHealthCheck
    {
        private readonly IEnumerable<IHealthCheckParticipant> participants;

        private static long lastCheckTime = DateTime.UtcNow.ToBinary();

        public SiloHealthCheck(IEnumerable<IHealthCheckParticipant> participants)
        {
            this.participants = participants;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var thisLastCheckTime = DateTime.FromBinary(Interlocked.Exchange(ref lastCheckTime, DateTime.UtcNow.ToBinary()));

            foreach (var participant in this.participants)
            {
                if (!participant.CheckHealth(thisLastCheckTime))
                {
                    return Task.FromResult(HealthCheckResult.Degraded());
                }
            }

            return Task.FromResult(HealthCheckResult.Healthy());
        }
    }
}
