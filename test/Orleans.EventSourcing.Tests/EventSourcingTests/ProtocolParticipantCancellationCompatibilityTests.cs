using Orleans.EventSourcing;
using TestExtensions;
using Xunit;

namespace UnitTests.EventSourcingTests;

public class ProtocolParticipantCancellationCompatibilityTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("EventSourcing")]
    public async Task LifecycleCancellationOverloads_ForwardToLegacyImplementation()
    {
        ILogConsistencyProtocolParticipant participant = new LegacyParticipant();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await participant.PreActivateProtocolParticipant(cancellation.Token);
        await participant.PostActivateProtocolParticipant(cancellation.Token);
        await participant.DeactivateProtocolParticipant(cancellation.Token);

        Assert.Equal(3, ((LegacyParticipant)participant).Calls);
    }

    private sealed class LegacyParticipant : ILogConsistencyProtocolParticipant
    {
        public int Calls { get; private set; }

        public Task PreActivateProtocolParticipant()
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task PostActivateProtocolParticipant()
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task DeactivateProtocolParticipant()
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
