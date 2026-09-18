using System.Reflection;
using NSubstitute;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DeferredJournaledStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dictionary_CaptureAndAcknowledgementPreserveLaterMutations(bool snapshot)
    {
        var codec = new DictionaryCodec();
        var state = CreateDictionary(codec);
        var items = (IDurableDictionary<string, int>)state;
        items.Add("first", 1);
        Assert.Empty(codec.Commands);
        Capture(state, snapshot);
        Assert.Equal(new[] { snapshot ? "snapshot:first=1" : "set:first=1" }, codec.Commands);
        items["later"] = 2;
        state.OnWriteCompleted();
        Assert.Equal(1, Version(state, "CapturedVersion"));
        Assert.Equal(1, Version(state, "AcknowledgedVersion"));
        Assert.Equal(2, Version(state, "MutationVersion"));
        Assert.Equal(2, items.Count);
        state.AppendEntries(default);
        Assert.Equal("set:later=2", codec.Commands[1]);
        Assert.Equal(2, codec.Commands.Count);
        state.OnWriteCompleted();
        Assert.Equal(2, Version(state, "AcknowledgedVersion"));
    }

    [Fact]
    public void Dictionary_OrderedMutationsUseExistingCommandCodec()
    {
        var codec = new DictionaryCodec();
        var state = CreateDictionary(codec);
        var items = (IDurableDictionary<string, int>)state;
        items.Add("key", 1);
        items["key"] = 2;
        Assert.False(items.Remove("absent"));
        Assert.True(items.Remove("key"));
        items.Clear();
        items.Add("last", 3);
        Assert.Empty(codec.Commands);
        state.AppendEntries(default);
        Assert.Equal(new[] { "set:key=1", "set:key=2", "remove:key", "clear", "set:last=3" }, codec.Commands);
        Assert.Equal(new KeyValuePair<string, int>("last", 3), Assert.Single(items));
        Assert.Equal(5, Version(state, "CapturedVersion"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dictionary_EncodingFailureKeepsOriginalErrorAndUnacknowledgedState(bool snapshot)
    {
        var failure = new IOException("codec failure");
        var codec = new DictionaryCodec { Failure = failure };
        var state = CreateDictionary(codec);
        var items = (IDurableDictionary<string, int>)state;
        items.Add("pending", 7);
        Assert.Same(failure, Assert.Throws<IOException>(() => Capture(state, snapshot)));
        Assert.Equal(7, items["pending"]);
        Assert.Equal(1, Version(state, "MutationVersion"));
        Assert.Equal(0, Version(state, "CapturedVersion"));
        Assert.Equal(0, Version(state, "AcknowledgedVersion"));
    }

    [Fact]
    public void Dictionary_ResetClearsRowsAndBothCommandCohorts()
    {
        var codec = new DictionaryCodec();
        var state = CreateDictionary(codec);
        var items = (IDurableDictionary<string, int>)state;
        items.Add("captured", 1);
        state.AppendEntries(default);
        items.Add("later", 2);
        state.Reset(default);
        state.AppendEntries(default);
        Assert.Empty(items);
        Assert.Single(codec.Commands);
        Assert.Equal(0, Version(state, "MutationVersion"));
        Assert.Equal(0, Version(state, "CapturedVersion"));
        Assert.Equal(0, Version(state, "AcknowledgedVersion"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Value_AcknowledgementPreservesNewerPendingValue(bool snapshot)
    {
        var codec = new ValueCodec();
        var manager = Substitute.For<IJournaledStateManager>();
        manager.GetRequiredCommandCodec<IDurableValueCommandCodec<int>>().Returns(codec);
        var state = ReceiverTestServices.CreateDeferredValue<int>(manager);
        var value = (IDurableValue<int>)state;
        value.Value = 1;
        Assert.Empty(codec.Values);
        Capture(state, snapshot);
        value.Value = 2;
        state.OnWriteCompleted();
        Assert.Equal(1, Version(state, "AcknowledgedVersion"));
        Assert.Equal(2, value.Value);
        state.AppendEntries(default);
        state.OnWriteCompleted();
        Assert.Equal(new[] { 1, 2 }, codec.Values);
        Assert.Equal(2, Version(state, "AcknowledgedVersion"));
        state.Reset(default);
        Assert.Equal(0, value.Value);
        Assert.Equal(0, Version(state, "MutationVersion"));
    }

    private static IJournaledState CreateDictionary(DictionaryCodec codec)
    {
        var manager = Substitute.For<IJournaledStateManager>();
        manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>().Returns(codec);
        return ReceiverTestServices.CreateDeferredDictionary<string, int>(manager);
    }

    private static void Capture(IJournaledState state, bool snapshot)
    {
        if (snapshot) state.AppendSnapshot(default);
        else state.AppendEntries(default);
    }

    private static long Version(IJournaledState state, string name) =>
        (long)state.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;

    private sealed class DictionaryCodec : IDurableDictionaryCommandCodec<string, int>
    {
        public List<string> Commands { get; } = [];
        public Exception? Failure { get; init; }
        public void WriteSet(string key, int value, JournalStreamWriter writer) => Record($"set:{key}={value}");
        public void WriteRemove(string key, JournalStreamWriter writer) => Record($"remove:{key}");
        public void WriteClear(JournalStreamWriter writer) => Record("clear");
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, int>> items, JournalStreamWriter writer) =>
            Record("snapshot:" + string.Join(",", items.Select(pair => $"{pair.Key}={pair.Value}")));
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, int> consumer) => throw new NotSupportedException();
        private void Record(string command)
        {
            if (Failure is { } exception) throw exception;
            Commands.Add(command);
        }
    }

    private sealed class ValueCodec : IDurableValueCommandCodec<int>
    {
        public List<int> Values { get; } = [];
        public void WriteSet(int value, JournalStreamWriter writer) => Values.Add(value);
        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<int> consumer) => throw new NotSupportedException();
    }
}
