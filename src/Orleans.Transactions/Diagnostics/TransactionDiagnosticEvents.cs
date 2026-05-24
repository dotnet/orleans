using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Transactions.Diagnostics;

internal static class TransactionDiagnosticEvents
{
    internal const string ListenerName = "Orleans.Transactions";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<TransactionDiagnosticEvent> AllEvents { get; } = new Observable();

    internal abstract class TransactionDiagnosticEvent(ParticipantId resource)
    {
        public readonly ParticipantId Resource = resource;
    }

    internal sealed class StorageWriteCompleted(
        ParticipantId resource,
        string eTag,
        int batchSize,
        int commitCount) : TransactionDiagnosticEvent(resource)
    {
        public readonly string ETag = eTag;
        public readonly int BatchSize = batchSize;
        public readonly int CommitCount = commitCount;
    }

    internal static void EmitStorageWriteCompleted(ParticipantId resource, string eTag, int batchSize, int commitCount)
    {
        if (!Listener.IsEnabled(nameof(StorageWriteCompleted)))
        {
            return;
        }

        Emit(resource, eTag, batchSize, commitCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(ParticipantId resource, string eTag, int batchSize, int commitCount)
        {
            // Observer exceptions intentionally propagate so tests can inject post-write faults.
            Listener.Write(nameof(StorageWriteCompleted), new StorageWriteCompleted(resource, eTag, batchSize, commitCount));
        }
    }

    private sealed class Observable : IObservable<TransactionDiagnosticEvent>
    {
        public IDisposable Subscribe(IObserver<TransactionDiagnosticEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<TransactionDiagnosticEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is TransactionDiagnosticEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
