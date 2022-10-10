using System;
using System.Collections.Generic;

namespace Grains
{
    public class ObserverManager<TObserver>
    {
        private readonly Dictionary<TObserver, DateTime> _observers = new();
        private readonly TimeSpan _expiration;

        public ObserverManager(TimeSpan expiration) => _expiration = expiration;

        public int Count => _observers.Count;
        public void Clear() => _observers.Clear();
        public void Subscribe(TObserver observer) => _observers[observer] = DateTime.UtcNow;
        public void Unsubscribe(TObserver subscriber) => _observers.Remove(subscriber, out _);
        public void Notify(Action<TObserver> notification)
        {
            ClearExpired();
            foreach (var observer in _observers)
            {
                try
                {
                    notification(observer.Key);
                }
                catch (Exception)
                {
                    // TODO: This should probably log.
                }
            }
        }

        private void ClearExpired()
        {
            var now = DateTime.UtcNow;
            var defunct = default(List<TObserver>);
            foreach (var observer in _observers)
            {
                if (observer.Value + _expiration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= new List<TObserver>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct is { Count: > 0 })
            {
                foreach (var observer in defunct)
                {
                    _observers.Remove(observer, out _);
                }
            }
        }
    }
}