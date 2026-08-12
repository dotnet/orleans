using System.Net;
using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.Streaming.Reliability;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class StreamingDiagnosticEventRecorderTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task ProviderReadinessIgnoresQueueEventsFromOtherSilos()
    {
        const string providerName = "provider";
        var localSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
        var otherSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13001), 1);
        var queue = QueueId.GetQueueId("queue", 1, 1);
        var otherQueue = QueueId.GetQueueId("other-queue", 2, 1);
        using var recorder = new StreamingDiagnosticEventRecorder(new TestLocalSiloDetails(localSilo));

        recorder.OnEvent(new StreamingEvents.PullingAgentStarted(
            providerName,
            localSilo,
            queue,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1)));
        recorder.OnEvent(new StreamingEvents.QueueReceiverInitialized(providerName, localSilo, queue));
        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [queue],
            runningAgents: 1));
        recorder.OnEvent(new StreamingEvents.BalancerChanged(
            providerName,
            otherSilo,
            [],
            [otherQueue],
            new TestQueueBalancer()));

        await recorder.WaitForProviderReady(providerName, TimeSpan.FromSeconds(1));
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task ProviderReadinessRequiresManagerState()
    {
        const string providerName = "provider";
        var localSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
        var queue = QueueId.GetQueueId("queue", 1, 1);
        using var recorder = new StreamingDiagnosticEventRecorder(new TestLocalSiloDetails(localSilo));

        recorder.OnEvent(new StreamingEvents.PullingAgentStarted(
            providerName,
            localSilo,
            queue,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1)));
        recorder.OnEvent(new StreamingEvents.QueueReceiverInitialized(providerName, localSilo, queue));

        var readiness = recorder.WaitForProviderReady(providerName, TimeSpan.FromSeconds(1));
        Assert.False(readiness.IsCompleted);

        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [queue],
            runningAgents: 1));
        await readiness;
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task ProviderReadinessAcceptsNoAssignedQueues()
    {
        const string providerName = "provider";
        var localSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
        using var recorder = new StreamingDiagnosticEventRecorder(new TestLocalSiloDetails(localSilo));

        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [],
            runningAgents: 0));

        await recorder.WaitForProviderReady(providerName, TimeSpan.FromSeconds(1));
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task ProviderReadinessWaitsForAssignedQueueReceiverInitialization()
    {
        const string providerName = "provider";
        var localSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
        var queue = QueueId.GetQueueId("queue", 1, 1);
        using var recorder = new StreamingDiagnosticEventRecorder(new TestLocalSiloDetails(localSilo));

        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [queue],
            runningAgents: 1));

        var readiness = recorder.WaitForProviderReady(providerName, TimeSpan.FromSeconds(1));
        Assert.False(readiness.IsCompleted);

        recorder.OnEvent(new StreamingEvents.QueueReceiverInitialized(providerName, localSilo, queue));
        await readiness;
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task ProviderReadinessDoesNotReuseInitializationFromPreviousAssignment()
    {
        const string providerName = "provider";
        var localSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
        var queue = QueueId.GetQueueId("queue", 1, 1);
        using var recorder = new StreamingDiagnosticEventRecorder(new TestLocalSiloDetails(localSilo));

        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [queue],
            runningAgents: 1));
        recorder.OnEvent(new StreamingEvents.QueueReceiverInitialized(providerName, localSilo, queue));
        await recorder.WaitForProviderReady(providerName, TimeSpan.FromSeconds(1));

        recorder.OnEvent(new StreamingEvents.PullingAgentStopped(providerName, localSilo, queue));
        recorder.OnEvent(new StreamingEvents.QueueReceiverInitialized(providerName, localSilo, queue));
        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [],
            runningAgents: 0));
        recorder.OnEvent(new StreamingEvents.PullingAgentManagerState(
            providerName,
            localSilo,
            [queue],
            runningAgents: 1));

        var readiness = recorder.WaitForProviderReady(providerName, TimeSpan.FromSeconds(1));
        Assert.False(readiness.IsCompleted);

        recorder.OnEvent(new StreamingEvents.QueueReceiverInitialized(providerName, localSilo, queue));
        await readiness;
    }

    private sealed class TestLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => "Test";
        public string ClusterId => "Test";
        public string DnsHostName => "localhost";
        public SiloAddress SiloAddress { get; } = siloAddress;
        public SiloAddress GatewayAddress => SiloAddress;
    }

    private sealed class TestQueueBalancer : IStreamQueueBalancer
    {
        public IEnumerable<QueueId> GetMyQueues() => [];
        public Task Initialize(IStreamQueueMapper queueMapper) => Task.CompletedTask;
        public Task Shutdown() => Task.CompletedTask;
        public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer) => true;
        public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer) => true;
    }
}
