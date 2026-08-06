using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Runtime.Diagnostics;

internal static class DispatcherEvents
{
    internal const string ListenerName = "Orleans.Dispatcher";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<DispatcherEvent> AllEvents { get; } = new Observable();

    internal abstract class DispatcherEvent
    {
    }

    internal sealed class ReceivedInvalidActivation(
        Message message,
        ActivationState activationState) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly ActivationState ActivationState = activationState;
    }

    internal sealed class DetectedDeadlock(
        Message message,
        ActivationData activation) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly ActivationData Activation = activation;
    }

    internal sealed class DiscardedRejection(
        Message message,
        Message.RejectionTypes rejectionType,
        string? reason,
        Exception? exception) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly Message.RejectionTypes RejectionType = rejectionType;
        public readonly string? Reason = reason;
        public readonly Exception? Exception = exception;
    }

    internal sealed class Rejected(
        Message message,
        Message.RejectionTypes rejectionType,
        string? reason,
        Exception? exception) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly Message.RejectionTypes RejectionType = rejectionType;
        public readonly string? Reason = reason;
        public readonly Exception? Exception = exception;
    }

    internal sealed class Forwarding(
        Message message,
        GrainAddress? oldAddress,
        SiloAddress? forwardingAddress,
        string? failedOperation,
        Exception? exception) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly GrainAddress? OldAddress = oldAddress;
        public readonly SiloAddress? ForwardingAddress = forwardingAddress;
        public readonly string? FailedOperation = failedOperation;
        public readonly Exception? Exception = exception;
    }

    internal sealed class ForwardingFailed(
        Message message,
        GrainAddress? oldAddress,
        SiloAddress? forwardingAddress,
        string? failedOperation,
        Exception? exception) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly GrainAddress? OldAddress = oldAddress;
        public readonly SiloAddress? ForwardingAddress = forwardingAddress;
        public readonly string? FailedOperation = failedOperation;
        public readonly Exception? Exception = exception;
    }

    internal sealed class ForwardingMultiple(
        int messageCount,
        GrainAddress? oldAddress,
        SiloAddress? forwardingAddress,
        string? failedOperation,
        Exception? exception) : DispatcherEvent
    {
        public readonly int MessageCount = messageCount;
        public readonly GrainAddress? OldAddress = oldAddress;
        public readonly SiloAddress? ForwardingAddress = forwardingAddress;
        public readonly string? FailedOperation = failedOperation;
        public readonly Exception? Exception = exception;
    }

    internal sealed class SelectTargetFailed(
        Message message,
        Exception exception) : DispatcherEvent
    {
        public readonly Message Message = message;
        public readonly Exception Exception = exception;
    }

    internal static void EmitDetectedDeadlock(Message message, ActivationData activation)
    {
        if (!Listener.IsEnabled(nameof(DetectedDeadlock)))
        {
            return;
        }

        Emit(message, activation);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message, ActivationData activation)
        {
            Listener.Write(nameof(DetectedDeadlock), new DetectedDeadlock(message, activation));
        }
    }

    internal static void EmitDiscardedRejection(
        Message message,
        Message.RejectionTypes rejectionType,
        string? reason,
        Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(DiscardedRejection)))
        {
            return;
        }

        Emit(message, rejectionType, reason, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            Message message,
            Message.RejectionTypes rejectionType,
            string? reason,
            Exception? exception)
        {
            Listener.Write(nameof(DiscardedRejection), new DiscardedRejection(message, rejectionType, reason, exception));
        }
    }

    internal static void EmitForwarding(
        Message message,
        GrainAddress? oldAddress,
        SiloAddress? forwardingAddress,
        string? failedOperation,
        Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(Forwarding)))
        {
            return;
        }

        Emit(message, oldAddress, forwardingAddress, failedOperation, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            Message message,
            GrainAddress? oldAddress,
            SiloAddress? forwardingAddress,
            string? failedOperation,
            Exception? exception)
        {
            Listener.Write(nameof(Forwarding), new Forwarding(message, oldAddress, forwardingAddress, failedOperation, exception));
        }
    }

    internal static void EmitForwardingFailed(
        Message message,
        GrainAddress? oldAddress,
        SiloAddress? forwardingAddress,
        string? failedOperation,
        Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(ForwardingFailed)))
        {
            return;
        }

        Emit(message, oldAddress, forwardingAddress, failedOperation, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            Message message,
            GrainAddress? oldAddress,
            SiloAddress? forwardingAddress,
            string? failedOperation,
            Exception? exception)
        {
            Listener.Write(nameof(ForwardingFailed), new ForwardingFailed(message, oldAddress, forwardingAddress, failedOperation, exception));
        }
    }

    internal static void EmitForwardingMultiple(
        int messageCount,
        GrainAddress? oldAddress,
        SiloAddress? forwardingAddress,
        string? failedOperation,
        Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(ForwardingMultiple)))
        {
            return;
        }

        Emit(messageCount, oldAddress, forwardingAddress, failedOperation, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            int messageCount,
            GrainAddress? oldAddress,
            SiloAddress? forwardingAddress,
            string? failedOperation,
            Exception? exception)
        {
            Listener.Write(nameof(ForwardingMultiple), new ForwardingMultiple(
                messageCount,
                oldAddress,
                forwardingAddress,
                failedOperation,
                exception));
        }
    }

    internal static void EmitReceivedInvalidActivation(Message message, ActivationState activationState)
    {
        if (!Listener.IsEnabled(nameof(ReceivedInvalidActivation)))
        {
            return;
        }

        Emit(message, activationState);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message, ActivationState activationState)
        {
            Listener.Write(nameof(ReceivedInvalidActivation), new ReceivedInvalidActivation(message, activationState));
        }
    }

    internal static void EmitRejected(
        Message message,
        Message.RejectionTypes rejectionType,
        string? reason,
        Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(Rejected)))
        {
            return;
        }

        Emit(message, rejectionType, reason, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            Message message,
            Message.RejectionTypes rejectionType,
            string? reason,
            Exception? exception)
        {
            Listener.Write(nameof(Rejected), new Rejected(message, rejectionType, reason, exception));
        }
    }

    internal static void EmitSelectTargetFailed(Message message, Exception exception)
    {
        if (!Listener.IsEnabled(nameof(SelectTargetFailed)))
        {
            return;
        }

        Emit(message, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Message message, Exception exception)
        {
            Listener.Write(nameof(SelectTargetFailed), new SelectTargetFailed(message, exception));
        }
    }

    private sealed class Observable : IObservable<DispatcherEvent>
    {
        public IDisposable Subscribe(IObserver<DispatcherEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<DispatcherEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is DispatcherEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
