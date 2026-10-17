using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Clients which read, append to, and delete journals concurrently, as duplicate activations of the same grain would,
/// recording every operation in a <see cref="LinearizabilityHistory"/>.
/// </summary>
internal sealed class JournalWorkload(LinearizabilityHistory history)
{
    private int _nextClient;

    public LinearizabilityHistory History => history;

    /// <summary>Runs a client against one journal until <paramref name="stop"/> is cancelled.</summary>
    public async Task RunClientAsync(GarnetJournalRouter router, JournalId journal, int seed, CancellationToken stop)
    {
        var client = Interlocked.Increment(ref _nextClient);
        var random = new Random(seed);
        var storage = new GarnetJournalStorage(router, journal, "json", compactionThresholdBytes: 0);
        var sequence = 0;
        var observed = await ReadAsync(storage, client, journal);
        while (!stop.IsCancellationRequested)
        {
            var choice = random.Next(100);
            if (choice < 10 || observed is null)
            {
                observed = await ReadAsync(storage, client, journal);
                continue;
            }

            HistoryOp op;
            var invoked = history.Now();
            if (choice < 15 && observed.Values is not null)
            {
                op = new HistoryOp(client, HistoryKind.Delete, invoked, observed, 0, null, HistoryOutcome.Success);
                op = op with { Outcome = await RunAsync(() => storage.DeleteAsync(CancellationToken.None)), DeletionETag = storage.LastDeletionETag };
                observed = op.Outcome == HistoryOutcome.Success ? JournalState.AbsentAfter(op.DeletionETag!) : null;
            }
            else
            {
                var value = client * 1_000_000 + sequence++;
                var payload = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(payload, value);
                op = new HistoryOp(client, HistoryKind.Append, invoked, observed, value, null, HistoryOutcome.Success);
                op = op with { Outcome = await RunAsync(() => storage.AppendAsync(new ReadOnlySequence<byte>(payload), CancellationToken.None)) };
                observed = op.Outcome == HistoryOutcome.Success ? observed.Append(value) : null;
            }

            history.Record(journal.Value, op);
        }
    }

    /// <summary>Reads a journal and records the read.</summary>
    /// <returns>The state read, or <see langword="null"/> if the read's outcome is unknown.</returns>
    public async Task<JournalState?> ReadAsync(GarnetJournalStorage storage, int client, JournalId journal)
    {
        var invoked = history.Now();
        try
        {
            var consumer = new CapturingConsumer();
            await storage.ReadAsync(consumer, CancellationToken.None);
            var state = Decode(consumer);
            if (state.Values is null)
            {
                state = JournalState.AbsentAfter(storage.AbsenceETag);
            }
            history.Record(journal.Value, new HistoryOp(client, HistoryKind.Read, invoked, null, 0, state, HistoryOutcome.Success));
            return state;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            history.Record(journal.Value, new HistoryOp(client, HistoryKind.Read, invoked, null, 0, null, HistoryOutcome.Unknown));
            return null;
        }
    }

    /// <summary>Reads a journal's values without recording the read.</summary>
    public static async Task<JournalState> ReadUnrecordedAsync(GarnetJournalRouter router, JournalId journal)
    {
        var consumer = new CapturingConsumer();
        await new GarnetJournalStorage(router, journal, "json", compactionThresholdBytes: 0).ReadAsync(consumer, CancellationToken.None);
        return Decode(consumer);
    }

    private static JournalState Decode(CapturingConsumer consumer)
    {
        if (consumer.Metadata?.ETag is null)
        {
            return JournalState.Absent;
        }

        var bytes = consumer.Bytes.ToArray();
        var values = ImmutableList.CreateBuilder<int>();
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));
        }

        return new JournalState(values.ToImmutable());
    }

    private static async Task<HistoryOutcome> RunAsync(Func<ValueTask> operation)
    {
        try
        {
            await operation();
            return HistoryOutcome.Success;
        }
        catch (GarnetJournalAmbiguousConflictException)
        {
            // An earlier attempt may have committed before other writers' mutations evicted its ETag.
            return HistoryOutcome.Unknown;
        }
        catch (InconsistentStateException)
        {
            return HistoryOutcome.Conflict;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return HistoryOutcome.Unknown;
        }
    }

    /// <summary>
    /// Gets a value indicating whether an operation failed only because a partition was unavailable or its reply was
    /// lost, so its outcome is unknown. Any other failure, such as a server error, fails the test.
    /// </summary>
    private static bool IsUnavailable(Exception exception) => exception is TimeoutException || GarnetJournalRouter.IsTransient(exception);
}
