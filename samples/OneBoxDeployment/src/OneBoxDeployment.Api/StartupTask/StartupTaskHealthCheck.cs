using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.Api.StartupTask
{
    /// <summary>
    /// A startup health check.
    /// </summary>
    /// <remarks>Based on code by Andrew Lock at https://andrewlock.net/running-async-tasks-on-app-startup-in-asp-net-core-part-4-using-health-checks/.
    /// See some problems and improvements
    /// <ul>
    ///     <li>https://tools.ietf.org/html/draft-inadarei-api-health-check-02</li>
    ///     <li>https://github.com/aspnet/AspNetCore/issues/5936</li>
    /// </ul>
    /// </remarks>
    public class StartupTasksHealthCheck: IHealthCheck
    {
        private readonly StartupTaskContext _context;
        public StartupTasksHealthCheck(StartupTaskContext context)
        {
            _context = context;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
        {
            if(_context.IsComplete)
            {
                return Task.FromResult(HealthCheckResult.Healthy("All startup tasks complete"));
            }

            return Task.FromResult(HealthCheckResult.Unhealthy("Startup tasks not complete"));
        }
    }
}
