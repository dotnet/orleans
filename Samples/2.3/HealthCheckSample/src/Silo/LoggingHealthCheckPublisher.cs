using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Silo
{
    public class LoggingHealthCheckPublisher : IHealthCheckPublisher
    {
        private readonly ILogger<LoggingHealthCheckPublisher> logger;

        public LoggingHealthCheckPublisher(ILogger<LoggingHealthCheckPublisher> logger)
        {
            this.logger = logger;
        }

        public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;

            logger.Log(report.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
                "Service is {@ReportStatus} at {@ReportTime} after {@ElapsedTime}ms with CorrelationId {@CorrelationId}",
                report.Status, now, report.TotalDuration.TotalMilliseconds, id);

            foreach (var entry in report.Entries)
            {
                logger.Log(entry.Value.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
                    entry.Value.Exception,
                    "{@HealthCheckName} is {@ReportStatus} after {@ElapsedTime}ms with CorrelationId {@CorrelationId}",
                    entry.Key, entry.Value.Status, entry.Value.Duration.TotalMilliseconds, id);
            }

            return Task.CompletedTask;
        }
    }
}