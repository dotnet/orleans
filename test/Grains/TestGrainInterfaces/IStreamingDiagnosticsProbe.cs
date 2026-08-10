using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces;

public static class StreamingDiagnosticsProbeConstants
{
    public static readonly GrainType SystemTargetType = SystemTargetGrainId.CreateGrainType("streaming-diagnostics-probe");
}

public interface IStreamingDiagnosticsProbe : ISystemTarget
{
    Task<SiloAddress> GetLocation();
    Task WaitForProviderReady(string providerName, int expectedQueueCount, TimeSpan timeout);
    Task WaitForProducerRegistered(string providerName, StreamId streamId, TimeSpan timeout);
    Task WaitForPullingAgentStreamRegistered(string providerName, StreamId streamId, TimeSpan timeout);
    Task WaitForSubscriptionRegistered(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout);
    Task WaitForSubscriptionAttached(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout);
    Task<int> GetItemDeliveredCount(string providerName, StreamId streamId, Guid subscriptionId);
    Task WaitForItemDelivered(string providerName, StreamId streamId, Guid subscriptionId, int expectedCount, TimeSpan timeout);
    Task WaitForConsumerCursorDrained(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout);
    Task<string> GetRecentStreamingDiagnostics();
}
