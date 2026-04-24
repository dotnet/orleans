using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

internal static class StatelessWorkerEvents
{
    internal const string ListenerName = "Orleans.StatelessWorker";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<StatelessWorkerEvent> AllEvents { get; } = new Observable();

    internal abstract class StatelessWorkerEvent(IGrainContext context)
    {
        public readonly IGrainContext Context = context;
        public readonly GrainId GrainId = context.GrainId;
    }

    internal sealed class WorkerCreated(
        IGrainContext context,
        IGrainContext workerContext,
        int workerCount) : StatelessWorkerEvent(context)
    {
        public readonly IGrainContext WorkerContext = workerContext;
        public readonly int WorkerCount = workerCount;
    }

    internal sealed class ContextTerminated(
        IGrainContext context,
        int workerCount) : StatelessWorkerEvent(context)
    {
        public readonly int WorkerCount = workerCount;
    }

    internal sealed class MessageForwarded(
        IGrainContext context,
        IGrainContext replacementContext,
        Message message) : StatelessWorkerEvent(context)
    {
        public readonly IGrainContext ReplacementContext = replacementContext;
        public readonly Message Message = message;
    }

    internal static void EmitWorkerCreated(IGrainContext context, IGrainContext workerContext, int workerCount)
    {
        if (!Listener.IsEnabled(nameof(WorkerCreated)))
        {
            return;
        }

        Emit(context, workerContext, workerCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext context, IGrainContext workerContext, int workerCount)
        {
            Listener.Write(nameof(WorkerCreated), new WorkerCreated(context, workerContext, workerCount));
        }
    }

    internal static void EmitContextTerminated(IGrainContext context, int workerCount)
    {
        if (!Listener.IsEnabled(nameof(ContextTerminated)))
        {
            return;
        }

        Emit(context, workerCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext context, int workerCount)
        {
            Listener.Write(nameof(ContextTerminated), new ContextTerminated(context, workerCount));
        }
    }

    internal static void EmitMessageForwarded(IGrainContext context, IGrainContext replacementContext, Message message)
    {
        if (!Listener.IsEnabled(nameof(MessageForwarded)))
        {
            return;
        }

        Emit(context, replacementContext, message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext context, IGrainContext replacementContext, Message message)
        {
            Listener.Write(nameof(MessageForwarded), new MessageForwarded(context, replacementContext, message));
        }
    }

    private sealed class Observable : IObservable<StatelessWorkerEvent>
    {
        public IDisposable Subscribe(IObserver<StatelessWorkerEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<StatelessWorkerEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();

            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is StatelessWorkerEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
