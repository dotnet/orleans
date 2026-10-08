using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableJobs.Diagnostics;

internal static class DurableJobsEvents
{
    internal const string ListenerName = "Orleans.DurableJobs.ShardManager";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<DurableJobEvent> AllEvents { get; } = new Observable();

    internal abstract class DurableJobEvent(SiloAddress siloAddress, JournalId journalId)
    {
        public readonly SiloAddress SiloAddress = siloAddress;
        public readonly JournalId JournalId = journalId;
    }

    internal sealed class ShardOpenJoined(SiloAddress siloAddress, JournalId journalId)
        : DurableJobEvent(siloAddress, journalId);

    internal sealed class ShardOpenRetryAfterCancellation(SiloAddress siloAddress, JournalId journalId)
        : DurableJobEvent(siloAddress, journalId);

    internal static void EmitShardOpenJoined(SiloAddress siloAddress, JournalId journalId)
    {
        if (!Listener.IsEnabled(nameof(ShardOpenJoined)))
        {
            return;
        }

        Emit(siloAddress, journalId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, JournalId journalId)
        {
            Listener.Write(nameof(ShardOpenJoined), new ShardOpenJoined(siloAddress, journalId));
        }
    }

    internal static void EmitShardOpenRetryAfterCancellation(SiloAddress siloAddress, JournalId journalId)
    {
        if (!Listener.IsEnabled(nameof(ShardOpenRetryAfterCancellation)))
        {
            return;
        }

        Emit(siloAddress, journalId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, JournalId journalId)
        {
            Listener.Write(nameof(ShardOpenRetryAfterCancellation), new ShardOpenRetryAfterCancellation(siloAddress, journalId));
        }
    }

    private sealed class Observable : IObservable<DurableJobEvent>
    {
        public IDisposable Subscribe(IObserver<DurableJobEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<DurableJobEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is DurableJobEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
