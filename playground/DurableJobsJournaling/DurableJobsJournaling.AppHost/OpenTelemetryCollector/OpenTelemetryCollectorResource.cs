using Aspire.Hosting.ApplicationModel;

namespace DurableJobsJournaling.AppHost.OpenTelemetryCollector;

public sealed class OpenTelemetryCollectorResource(string name) : ContainerResource(name)
{
    internal const string OtlpGrpcEndpointName = "grpc";
    internal const string OtlpHttpEndpointName = "http";
}
