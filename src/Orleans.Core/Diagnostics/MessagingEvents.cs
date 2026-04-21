using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Core.Diagnostics;

internal static class MessagingEvents
{
    internal const string ListenerName = "Orleans.Messaging";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<MessageEvent> AllEvents { get; } = new Observable();

    internal abstract class MessageEvent(Message message)
    {
        public readonly Message Message = message;
    }

    internal sealed class Created(Message message) : MessageEvent(message)
    {
    }

    internal sealed class Sent(Message message) : MessageEvent(message)
    {
    }

    internal sealed class ReceivedByIncomingAgent(Message message) : MessageEvent(message)
    {
    }

    internal sealed class ReceivedByDispatcher(Message message) : MessageEvent(message)
    {
    }

    internal sealed class Expired(Message message, MessagingInstruments.Phase phase) : MessageEvent(message)
    {
        public readonly MessagingInstruments.Phase Phase = phase;
    }

    internal sealed class Blocked(Message message) : MessageEvent(message)
    {
    }

    internal sealed class SendingDropped(
        Message message,
        SiloAddress localSiloAddress,
        string reason) : MessageEvent(message)
    {
        public readonly SiloAddress LocalSiloAddress = localSiloAddress;
        public readonly string Reason = reason;
    }

    internal sealed class EnqueuedInbound(Message message) : MessageEvent(message)
    {
    }

    internal sealed class DequeuedInbound(Message message) : MessageEvent(message)
    {
    }

    internal sealed class Scheduled(Message message) : MessageEvent(message)
    {
    }

    internal sealed class EnqueuedOnActivation(
        Message message,
        IGrainContext grainContext) : MessageEvent(message)
    {
        public readonly IGrainContext GrainContext = grainContext;
    }

    internal sealed class Invoked(Message message) : MessageEvent(message)
    {
    }

    internal sealed class RejectedDeadSilo(
        Message message,
        SiloAddress localSiloAddress) : MessageEvent(message)
    {
        public readonly SiloAddress LocalSiloAddress = localSiloAddress;
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitBlocked(Message message)
    {
        if (!Listener.IsEnabled(nameof(Blocked)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(Blocked), new Blocked(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitCreated(Message message)
    {
        if (!Listener.IsEnabled(nameof(Created)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(Created), new Created(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitDequeuedInbound(Message message)
    {
        if (!Listener.IsEnabled(nameof(DequeuedInbound)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(DequeuedInbound), new DequeuedInbound(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitEnqueuedInbound(Message message)
    {
        if (!Listener.IsEnabled(nameof(EnqueuedInbound)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(EnqueuedInbound), new EnqueuedInbound(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitEnqueuedOnActivation(Message message, IGrainContext grainContext)
    {
        if (!Listener.IsEnabled(nameof(EnqueuedOnActivation)))
        {
            return;
        }

        Emit(message, grainContext);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message, IGrainContext grainContext)
        {
            Listener.Write(nameof(EnqueuedOnActivation), new EnqueuedOnActivation(message, grainContext));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitExpired(Message message, MessagingInstruments.Phase phase)
    {
        if (!Listener.IsEnabled(nameof(Expired)))
        {
            return;
        }

        Emit(message, phase);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message, MessagingInstruments.Phase phase)
        {
            Listener.Write(nameof(Expired), new Expired(message, phase));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitInvoked(Message message)
    {
        if (!Listener.IsEnabled(nameof(Invoked)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(Invoked), new Invoked(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitReceivedByDispatcher(Message message)
    {
        if (!Listener.IsEnabled(nameof(ReceivedByDispatcher)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(ReceivedByDispatcher), new ReceivedByDispatcher(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitReceivedByIncomingAgent(Message message)
    {
        if (!Listener.IsEnabled(nameof(ReceivedByIncomingAgent)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(ReceivedByIncomingAgent), new ReceivedByIncomingAgent(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitRejectedDeadSilo(SiloAddress localSiloAddress, Message message)
    {
        if (!Listener.IsEnabled(nameof(RejectedDeadSilo)))
        {
            return;
        }

        Emit(localSiloAddress, message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress localSiloAddress, Message message)
        {
            Listener.Write(nameof(RejectedDeadSilo), new RejectedDeadSilo(message, localSiloAddress));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitScheduled(Message message)
    {
        if (!Listener.IsEnabled(nameof(Scheduled)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(Scheduled), new Scheduled(message));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitSendingDropped(SiloAddress localSiloAddress, Message message, string reason)
    {
        if (!Listener.IsEnabled(nameof(SendingDropped)))
        {
            return;
        }

        Emit(localSiloAddress, message, reason);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress localSiloAddress, Message message, string reason)
        {
            Listener.Write(nameof(SendingDropped), new SendingDropped(message, localSiloAddress, reason));
        }
    }

    [Conditional("MESSAGING_TRACE")]
    internal static void EmitSent(Message message)
    {
        if (!Listener.IsEnabled(nameof(Sent)))
        {
            return;
        }

        Emit(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message)
        {
            Listener.Write(nameof(Sent), new Sent(message));
        }
    }

    private sealed class Observable : IObservable<MessageEvent>
    {
        public IDisposable Subscribe(IObserver<MessageEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<MessageEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is MessageEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
