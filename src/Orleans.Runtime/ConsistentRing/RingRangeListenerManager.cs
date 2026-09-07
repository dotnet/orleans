using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime.ConsistentRing;

internal sealed class RingRangeListenerManager(IRingRange initialRange)
{
    private readonly object _lock = new();
    private readonly List<IRingRangeListener> _listeners = [];
    private readonly Queue<NotificationWorkItem> _pendingNotifications = [];
    private Notification _lastNotification = new(initialRange, initialRange, true);
    private bool _isDispatching;
    private int _dispatchThreadId;

    public bool Subscribe(IRingRangeListener listener)
    {
        NotificationWorkItem notification;
        lock (_lock)
        {
            if (_listeners.Contains(listener))
            {
                return false;
            }

            _listeners.Add(listener);
            notification = new([listener], _lastNotification, onException: null);
            _pendingNotifications.Enqueue(notification);
        }

        Dispatch(notification);
        return true;
    }

    public bool Unsubscribe(IRingRangeListener listener)
    {
        lock (_lock)
        {
            return _listeners.Remove(listener);
        }
    }

    public NotificationWorkItem Publish(
        IRingRange oldRange,
        IRingRange newRange,
        bool increased,
        Action<IRingRangeListener, Exception> onException)
    {
        NotificationWorkItem notification;
        lock (_lock)
        {
            _lastNotification = new(oldRange, newRange, increased);
            notification = new([.. _listeners], _lastNotification, onException);
            _pendingNotifications.Enqueue(notification);
        }

        return notification;
    }

    public void Dispatch(NotificationWorkItem notification)
    {
        bool shouldDispatch;
        bool shouldWait;
        lock (_lock)
        {
            shouldDispatch = !_isDispatching;
            if (shouldDispatch)
            {
                _isDispatching = true;
                _dispatchThreadId = Environment.CurrentManagedThreadId;
            }

            shouldWait = !shouldDispatch && _dispatchThreadId != Environment.CurrentManagedThreadId;
        }

        if (shouldDispatch)
        {
            Drain();
        }

        if (shouldDispatch || shouldWait)
        {
            notification.Wait();
        }
    }

    private void Drain()
    {
        while (true)
        {
            NotificationWorkItem notification;
            lock (_lock)
            {
                if (_pendingNotifications.Count == 0)
                {
                    _isDispatching = false;
                    _dispatchThreadId = 0;
                    return;
                }

                notification = _pendingNotifications.Dequeue();
            }

            notification.Execute();
        }
    }

    internal readonly record struct Notification(IRingRange OldRange, IRingRange NewRange, bool Increased);

    internal sealed class NotificationWorkItem(
        IRingRangeListener[] listeners,
        Notification notification,
        Action<IRingRangeListener, Exception>? onException)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsCompleted => _completion.Task.IsCompleted;

        public void Wait() => _completion.Task.GetAwaiter().GetResult();

        public void Execute()
        {
            try
            {
                foreach (var listener in listeners)
                {
                    try
                    {
                        listener.RangeChangeNotification(
                            notification.OldRange,
                            notification.NewRange,
                            notification.Increased);
                    }
                    catch (Exception exception) when (onException is not null)
                    {
                        onException(listener, exception);
                    }
                }
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                return;
            }

            _completion.TrySetResult();
        }
    }
}
