using Orleans.EventSourcing.LogStorage;
using Orleans.EventSourcing.StateStorage;
using System.Reflection;
using Xunit;

namespace Tester.EventSourcingTests;

public class StateEnvelopeTests
{
    [Fact]
    public void LogStateEnvelopeMaintainsNonNullState()
    {
        var envelope = new LogStateWithMetaDataAndETag<TestLogEntry>();

        Assert.Same(envelope.StateAndMetaData, envelope.State);
        envelope.StateAndMetaData = null!;
        Assert.NotNull(envelope.StateAndMetaData);
        envelope.State = null!;
        Assert.NotNull(envelope.State);
        ((IGrainState<LogStateWithMetaData<TestLogEntry>>)envelope).State = null;
        Assert.NotNull(envelope.State);
        AssertLegacyBackingFieldIsSerialized(envelope);
    }

    [Fact]
    public void GrainStateEnvelopeMaintainsNonNullState()
    {
        var envelope = new GrainStateWithMetaDataAndETag<TestView>();

        Assert.Same(envelope.StateAndMetaData, envelope.State);
        envelope.StateAndMetaData = null!;
        Assert.NotNull(envelope.StateAndMetaData);
        envelope.State = null!;
        Assert.NotNull(envelope.State);
        ((IGrainState<GrainStateWithMetaData<TestView>>)envelope).State = null;
        Assert.NotNull(envelope.State);
        AssertLegacyBackingFieldIsSerialized(envelope);
    }

    private static void AssertLegacyBackingFieldIsSerialized<T>(T envelope)
    {
        var backingField = typeof(T).GetField("<StateAndMetaData>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(backingField);
        Assert.Null(backingField.GetCustomAttribute<NonSerializedAttribute>());

        backingField.SetValue(envelope, null);
        Assert.NotNull(typeof(T).GetProperty("StateAndMetaData")!.GetValue(envelope));
    }

    private sealed class TestLogEntry;

    private sealed class TestView
    {
        public TestView()
        {
        }
    }
}
