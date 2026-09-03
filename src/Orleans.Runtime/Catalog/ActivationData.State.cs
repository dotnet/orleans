using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization.Session;

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    private struct RequestScheduler
    {
        private readonly List<(Message Message, CoarseStopwatch QueuedTime)> _waiting;
        private readonly Dictionary<Message, CoarseStopwatch> _running;
        private Message? _blockingRequest;
        private int _runningNonAlwaysInterleaveCount;
        private int _runningNonAlwaysInterleaveWritableCount;
        private CoarseStopwatch _busyDuration;

        public RequestScheduler()
        {
            _waiting = new();
            _running = new();
        }

        public int WaitingCount => _waiting.Count;
        public int RunningCount => _running.Count;
        public bool IsRunning => _running.Count > 0;
        public Message? BlockingRequest => _blockingRequest;
        public CoarseStopwatch BusyDuration => _busyDuration;
        public List<(Message Message, CoarseStopwatch QueuedTime)> Waiting => _waiting;
        public Dictionary<Message, CoarseStopwatch> Running => _running;

        public void Enqueue(Message message) => _waiting.Add((message, CoarseStopwatch.StartNew()));

        public bool TryGetWaiting(int index, [NotNullWhen(true)] out Message? message)
        {
            if ((uint)index < (uint)_waiting.Count)
            {
                message = _waiting[index].Message;
                return true;
            }

            message = null;
            return false;
        }

        public void RemoveWaitingAt(int index) => _waiting.RemoveAt(index);

        public void RecordRunning(Message message)
        {
            var stopwatch = CoarseStopwatch.StartNew();
            _running.Add(message, stopwatch);
            if (message.IsAlwaysInterleave)
            {
                return;
            }

            ++_runningNonAlwaysInterleaveCount;
            if (!message.IsReadOnly)
            {
                ++_runningNonAlwaysInterleaveWritableCount;
            }

            if (_blockingRequest is null)
            {
                _blockingRequest = message;
                _busyDuration = stopwatch;
            }
        }

        public void Complete(Message message)
        {
            if (_running.Remove(message) && !message.IsAlwaysInterleave)
            {
                --_runningNonAlwaysInterleaveCount;
                if (!message.IsReadOnly)
                {
                    --_runningNonAlwaysInterleaveWritableCount;
                }

                Debug.Assert(_runningNonAlwaysInterleaveCount >= 0);
                Debug.Assert(_runningNonAlwaysInterleaveWritableCount >= 0);
            }

            if (_blockingRequest is null || message.Equals(_blockingRequest))
            {
                _blockingRequest = null;
                _busyDuration = default;
            }
        }

        public bool MayInvoke(
            Message incoming,
            bool isReentrantSection,
            GrainCanInterleave? canInterleave,
            object? grainInstance)
        {
            if (!IsRunning || incoming.IsAlwaysInterleave
                || _runningNonAlwaysInterleaveCount == 0
                || (incoming.IsReadOnly && _runningNonAlwaysInterleaveWritableCount == 0)
                || isReentrantSection)
            {
                return true;
            }

            bool? incomingMayInterleave = null;
            foreach (var (runningMessage, stopwatch) in _running)
            {
                if (runningMessage.IsAlwaysInterleave || (runningMessage.IsReadOnly && incoming.IsReadOnly))
                {
                    continue;
                }

                if (canInterleave is not null)
                {
                    incomingMayInterleave ??= canInterleave.MayInterleave(grainInstance, incoming);
                    if (incomingMayInterleave.Value)
                    {
                        return true;
                    }

                    if (canInterleave.MayInterleave(grainInstance, runningMessage))
                    {
                        continue;
                    }
                }

                _blockingRequest = runningMessage;
                _busyDuration = stopwatch;
                return false;
            }

            return true;
        }

        public List<Message> DrainWaiting()
        {
            var result = new List<Message>(_waiting.Count);
            foreach (var (message, _) in _waiting)
            {
                if (!message.IsLocalOnly)
                {
                    result.Add(message);
                }
            }

            _waiting.Clear();
            return result;
        }

        public bool TryFindRequest(
            GrainId senderGrainId,
            CorrelationId messageId,
            [NotNullWhen(true)] out Message? message,
            out bool wasWaiting)
        {
            foreach (var candidate in _running.Keys)
            {
                if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                {
                    message = candidate;
                    wasWaiting = false;
                    return true;
                }
            }

            for (var i = 0; i < _waiting.Count; i++)
            {
                var candidate = _waiting[i].Message;
                if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                {
                    message = candidate;
                    _waiting.RemoveAt(i);
                    wasWaiting = true;
                    return true;
                }
            }

            message = null;
            wasWaiting = false;
            return false;
        }

        public bool TryGetRunningDuration(Message message, out CoarseStopwatch duration) =>
            _running.TryGetValue(message, out duration);
    }

    private struct MessagePumpState
    {
        private readonly SingleWaiterAutoResetEvent _signal;

        public MessagePumpState()
        {
            _signal = new() { RunContinuationsAsynchronously = true };
        }

        // Assigned and retained only for diagnostics, such as associating a loop task in a memory dump.
#pragma warning disable IDE0052
        public Task? MessageLoopTask;
#pragma warning restore IDE0052

        public void Signal() => _signal.Signal();
        public ValueTask WaitAsync() => _signal.WaitAsync();
    }

    private struct LifecycleOperationQueue
    {
        private Queue<object>? _pending;

        public bool HasPending => _pending is { Count: > 0 };

        public void Enqueue(object operation) => (_pending ??= new()).Enqueue(operation);

        public bool TryPeek(out object? operation)
        {
            if (_pending is { } pending)
            {
                return pending.TryPeek(out operation);
            }

            operation = null;
            return false;
        }

        public void CompleteCurrent()
        {
            _pending!.Dequeue();
            if (_pending.Count == 0)
            {
                _pending = null;
            }
        }

        public void CancelPending(Action<Exception, Command> onError)
        {
            if (_pending is not { Count: > 0 } pending)
            {
                return;
            }

            var array = ArrayPool<object>.Shared.Rent(pending.Count);
            pending.CopyTo(array, 0);
            try
            {
                foreach (var operation in new Span<object>(array, 0, pending.Count))
                {
                    if (operation is not Command command)
                    {
                        continue;
                    }

                    try
                    {
                        command.Cancel();
                    }
                    catch (Exception exception) when (exception is not ObjectDisposedException)
                    {
                        onError(exception, command);
                    }
                }
            }
            finally
            {
                ArrayPool<object>.Shared.Return(array, clearArray: true);
            }
        }
    }

    /// <summary>
    /// Additional properties which are not needed for the majority of an activation's lifecycle.
    /// </summary>
    private sealed class ActivationDataExtras
    {
        private const int IsStuckProcessingMessageFlag = 1 << 0;
        private const int IsStuckDeactivatingFlag = 1 << 1;
        private const int IsDisposingFlag = 1 << 2;
        private byte _flags;

        private Dictionary<Type, object>? _components;

        public HashSet<IGrainTimer>? Timers;

        /// <summary>
        /// During rehydration, this may contain the address for the previous (recently dehydrated) activation of this grain.
        /// </summary>
        public GrainAddress? PreviousRegistration;

        /// <summary>
        /// If State == Invalid, this may contain a forwarding address for incoming messages
        /// </summary>
        public SiloAddress? ForwardingAddress;

        /// <summary>
        /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
        /// </summary>
        public TaskCompletionSource<bool>? DeactivationTask;
        public TaskCompletionSource? ActivationReady;

        public DateTime? DeactivationStartTime;

        public DeactivationReason DeactivationReason;

        /// <summary>
        /// When migrating to another location, this contains the information to preserve across activations.
        /// </summary>
        public DehydrationContextHolder? DehydrationContext;
        public GrainCanInterleave? InterleavingPredicate;
        public ReentrantRequestTracker? ReentrantRequests;

        public bool IsStuckProcessingMessage { get => GetFlag(IsStuckProcessingMessageFlag); set => SetFlag(IsStuckProcessingMessageFlag, value); }
        public bool IsStuckDeactivating { get => GetFlag(IsStuckDeactivatingFlag); set => SetFlag(IsStuckDeactivatingFlag, value); }
        public bool IsDisposing { get => GetFlag(IsDisposingFlag); set => SetFlag(IsDisposingFlag, value); }

        private void SetFlag(int flag, bool value)
        {
            if (value)
            {
                _flags |= (byte)flag;
            }
            else
            {
                _flags &= (byte)~flag;
            }
        }

        private bool GetFlag(int flag) => (_flags & flag) != 0;
        public bool TryGetComponent(Type componentType, out object? component)
        {
            if (_components is { } components && components.TryGetValue(componentType, out var result))
            {
                component = result;
                return true;
            }

            component = null;
            return false;
        }

        public void SetComponent(Type componentType, object component) =>
            (_components ??= new())[componentType] = component;

        public void RemoveComponent(Type componentType)
        {
            _components?.Remove(componentType);
        }
    }

    private class DehydrationContextHolder(SerializerSessionPool sessionPool, Dictionary<string, object>? requestContext)
    {
        public readonly MigrationContext MigrationContext = new(sessionPool);
        public readonly Dictionary<string, object>? RequestContext = requestContext;

        /// <summary>
        /// The activity context from the grain call that initiated the migration.
        /// This is used to parent the dehydrate span to the migration request trace.
        /// </summary>
        public ActivityContext? MigrationActivityContext { get; set; } = Activity.Current?.Context;
    }
}
