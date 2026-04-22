using System;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace Orleans.Dashboard
{
    internal sealed partial class DashboardHost
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Unable to activate silo grain service during startup. The service will be activated on first use."
        )]
        private static partial void LogWarningActivateSiloGrainServiceStartupFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Unable to activate dashboard grain during startup. The grain will be activated on first use."
        )]
        private static partial void LogWarningActivateDashboardGrainStartupFailed(ILogger logger, Exception exception);
    }
}

namespace Orleans.Dashboard.Implementation
{
    internal sealed partial class GrainProfilerFilter
    {
        [LoggerMessage(
            EventId = 100002,
            Level = LogLevel.Error,
            Message = "error recording results for grain"
        )]
        private static partial void LogErrorRecordingResultsForGrain(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 100003,
            Level = LogLevel.Error,
            Message = "error reading NoProfilingAttribute attribute for grain"
        )]
        private static partial void LogErrorReadingNoProfilingAttribute(ILogger logger, Exception exception);
    }

    internal sealed partial class DashboardTelemetryExporter
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Ignoring unknown metric type {MetricType}"
        )]
        private static partial void LogWarningIgnoringUnknownMetricType(ILogger logger, MetricType metricType);
    }

    internal sealed partial class SiloGrainService
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Not running in Orleans runtime"
        )]
        private static partial void LogWarningNotRunningInOrleansRuntime(ILogger logger);
    }
}

namespace Orleans.Dashboard.Metrics
{
    internal sealed partial class GrainProfiler
    {
        [LoggerMessage(
            EventId = 100001,
            Level = LogLevel.Warning,
            Message = "Exception thrown sending tracing to dashboard grain"
        )]
        private static partial void LogWarningSubmitTracingFailed(ILogger logger, Exception exception);
    }
}
