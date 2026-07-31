using Orleans.EventSourcing.LogStorage;
using Orleans.EventSourcing.StateStorage;
using Xunit;

namespace Tester.EventSourcingTests;

public class StateEnvelopeTests
{
    [Fact]
    public void LogStateEnvelopeMaintainsNonNullState()
    {
        var envelope = new LogStateWithMetaDataAndETag<TestLogEntry>();

        Assert.Same(envelope.StateAndMetaData, envelope.State);
        Assert.Throws<ArgumentNullException>(() => envelope.StateAndMetaData = null!);
        Assert.Throws<ArgumentNullException>(() => envelope.State = null!);
        Assert.Throws<ArgumentNullException>(() => ((IGrainState<LogStateWithMetaData<TestLogEntry>>)envelope).State = null);
    }

    [Fact]
    public void GrainStateEnvelopeMaintainsNonNullState()
    {
        var envelope = new GrainStateWithMetaDataAndETag<TestView>();

        Assert.Same(envelope.StateAndMetaData, envelope.State);
        Assert.Throws<ArgumentNullException>(() => envelope.StateAndMetaData = null!);
        Assert.Throws<ArgumentNullException>(() => envelope.State = null!);
        Assert.Throws<ArgumentNullException>(() => ((IGrainState<GrainStateWithMetaData<TestView>>)envelope).State = null);
    }

    private sealed class TestLogEntry;

    private sealed class TestView
    {
        public TestView()
        {
        }
    }
}
