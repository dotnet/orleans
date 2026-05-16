using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Journaling;

namespace Benchmarks.Journaling;

internal static class JournalReplayContextFactory
{
    public static JournalReplayContext Create(string journalFormatKey, JournalStreamId streamId, IJournaledState state)
    {
        journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IJournalFormat>(journalFormatKey, new TestJournalFormat(journalFormatKey));
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, uint>>(journalFormatKey, new UnsupportedDictionaryCommandCodec<uint>());
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, DateTime>>(journalFormatKey, new UnsupportedDictionaryCommandCodec<DateTime>());

        var serviceProvider = services.BuildServiceProvider();
        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }),
            TimeProvider.System,
            serviceProvider);

        var manager = new JournaledStateManager(shared, new NullJournalStorage());
        manager.BindStateForReplay(streamId, state);
        return new(manager);
    }

    private sealed class TestJournalFormat(string journalFormatKey) : IJournalFormat
    {
        public string FormatKey { get; } = journalFormatKey;

        public string MimeType => null;

        public JournalBufferWriter CreateWriter() => new OrleansBinaryJournalBufferWriter();

        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException();
    }

    private sealed class UnsupportedDictionaryCommandCodec<TValue> : IDurableDictionaryCommandCodec<string, TValue>
    {
        public void WriteSet(string key, TValue value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteRemove(string key, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, TValue>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, TValue> consumer) => throw new NotSupportedException();
    }

    private sealed class NullJournalStorage : IJournalStorage
    {
        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => default;

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }
}
