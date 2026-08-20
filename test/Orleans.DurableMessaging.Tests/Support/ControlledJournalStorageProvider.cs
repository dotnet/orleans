using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class ControlledJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog
{
    private readonly VolatileJournalStorageProvider _inner = new(
        Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "orleans-binary" }));
    private readonly ConcurrentDictionary<JournalId, WritePlan> _readPlans = new();
    private readonly ConcurrentDictionary<JournalId, WritePlan> _writePlans = new();
    private readonly ConcurrentDictionary<JournalId, int> _successfulWrites = new();

    public IJournalStorage CreateStorage(JournalId journalId) =>
        new ControlledJournalStorage(this, journalId, _inner.CreateStorage(journalId));

    public IAsyncEnumerable<JournalId> ListAsync(
        JournalId prefix = default,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(prefix, cancellationToken);

    public WriteBarrier BlockWrite(JournalId journalId, int matchingWrite = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingWrite);
        var plan = new WritePlan(matchingWrite, fail: false);
        if (!_writePlans.TryAdd(journalId, plan))
        {
            throw new InvalidOperationException($"A write plan is already armed for journal '{journalId}'.");
        }

        return new WriteBarrier(plan);
    }

    public WriteBarrier BlockRead(JournalId journalId, int matchingRead = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingRead);
        var plan = new WritePlan(matchingRead, fail: false);
        if (!_readPlans.TryAdd(journalId, plan))
        {
            throw new InvalidOperationException($"A read plan is already armed for journal '{journalId}'.");
        }

        return new WriteBarrier(plan);
    }

    public void FailWrite(JournalId journalId, int matchingWrite = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingWrite);
        if (!_writePlans.TryAdd(journalId, new WritePlan(matchingWrite, fail: true)))
        {
            throw new InvalidOperationException($"A write plan is already armed for journal '{journalId}'.");
        }
    }

    public int GetSuccessfulWriteCount(JournalId journalId) =>
        _successfulWrites.TryGetValue(journalId, out var count) ? count : 0;

    private async ValueTask BeforeWriteAsync(JournalId journalId, CancellationToken cancellationToken)
    {
        if (!_writePlans.TryGetValue(journalId, out var plan)
            || Interlocked.Increment(ref plan.Seen) != plan.Target)
        {
            return;
        }

        _writePlans.TryRemove(new KeyValuePair<JournalId, WritePlan>(journalId, plan));
        plan.Entered.TrySetResult();
        if (plan.Fail)
        {
            throw new IOException($"Injected journal write failure for '{journalId}'.");
        }

        await plan.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask BeforeReadAsync(JournalId journalId, CancellationToken cancellationToken)
    {
        if (!_readPlans.TryGetValue(journalId, out var plan)
            || Interlocked.Increment(ref plan.Seen) != plan.Target)
        {
            return;
        }

        _readPlans.TryRemove(new KeyValuePair<JournalId, WritePlan>(journalId, plan));
        plan.Entered.TrySetResult();
        await plan.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnWriteSucceeded(JournalId journalId) =>
        _successfulWrites.AddOrUpdate(journalId, 1, static (_, count) => count + 1);

    internal sealed class WritePlan(int target, bool fail)
    {
        public int Target { get; } = target;
        public bool Fail { get; } = fail;
        public int Seen;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class WriteBarrier
    {
        private readonly WritePlan _plan;

        internal WriteBarrier(WritePlan plan) => _plan = plan;

        public Task WaitUntilEnteredAsync() => _plan.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() => _plan.Release.TrySetResult();
        public void Fail() => _plan.Release.TrySetException(new IOException("Injected blocked journal write failure."));
    }

    private sealed class ControlledJournalStorage(
        ControlledJournalStorageProvider owner,
        JournalId journalId,
        IJournalStorage inner) : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            await owner.BeforeReadAsync(journalId, cancellationToken).ConfigureAwait(false);
            await inner.ReadAsync(consumer, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<bool> CreateIfNotExistsAsync(
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) =>
            inner.CreateIfNotExistsAsync(metadata, cancellationToken);

        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.GetMetadataAsync(cancellationToken);

        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null,
            IEnumerable<string>? remove = null,
            string? expectedETag = null,
            CancellationToken cancellationToken = default) =>
            inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            await owner.BeforeWriteAsync(journalId, cancellationToken).ConfigureAwait(false);
            await inner.ReplaceAsync(value, cancellationToken).ConfigureAwait(false);
            owner.OnWriteSucceeded(journalId);
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            await owner.BeforeWriteAsync(journalId, cancellationToken).ConfigureAwait(false);
            await inner.AppendAsync(value, cancellationToken).ConfigureAwait(false);
            owner.OnWriteSucceeded(journalId);
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) =>
            inner.DeleteAsync(cancellationToken);
    }
}
