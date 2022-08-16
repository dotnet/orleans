using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Collections.Generic;
using Orleans.Runtime;
using System.Linq;

namespace Orleans.Utilities
{
    /// <summary>
    /// Maintains a collection of observers.
    /// </summary>
    /// <typeparam name="TObserver">
    /// The observer type.
    /// </typeparam>
    public class ObserverManager<TObserver> : ObserverManager<IAddressable, TObserver>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager{TObserver}"/> class. 
        /// </summary>
        /// <param name="expiration">
        /// The expiration.
        /// </param>
        /// <param name="log">The log.</param>
        public ObserverManager(TimeSpan expiration, ILogger log) : base(expiration, log)
        {
        }
    }

    /// <summary>
    /// Maintains a collection of observers.
    /// </summary>
    /// <typeparam name="TIdentity">
    /// The address type, used to identify observers.
    /// </typeparam>
    /// <typeparam name="TObserver">
    /// The observer type.
    /// </typeparam>
    public class ObserverManager<TIdentity, TObserver> : IEnumerable<TObserver>
    {
        /// <summary>
        /// The observers.
        /// </summary>
        private readonly Dictionary<TIdentity, ObserverEntry> _observers = new();

        /// <summary>
        /// The log.
        /// </summary>
        private readonly ILogger _log;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager{TIdentity,TObserver}"/> class. 
        /// </summary>
        /// <param name="expiration">
        /// The expiration.
        /// </param>
        /// <param name="log">The log.</param>
        public ObserverManager(TimeSpan expiration, ILogger log)
        {
            ExpirationDuration = expiration;
            _log = log;
            GetDateTime = () => DateTime.UtcNow;
        }

        /// <summary>
        /// Gets or sets the delegate used to get the date and time, for expiry.
        /// </summary>
        public Func<DateTime> GetDateTime { get; set; }

        /// <summary>
        /// Gets or sets the expiration time span, after which observers are lazily removed.
        /// </summary>
        public TimeSpan ExpirationDuration { get; set; }

        /// <summary>
        /// Gets the number of observers.
        /// </summary>
        public int Count => _observers.Count;

        /// <summary>
        /// Gets a copy of the observers.
        /// </summary>
        public IDictionary<TIdentity, TObserver> Observers
        {
            get
            {
                return _observers.ToDictionary(_ => _.Key, _ => _.Value.Observer);
            }
        }

        /// <summary>
        /// Removes all observers.
        /// </summary>
        public void Clear() => _observers.Clear();

        /// <summary>
        /// Ensures that the provided <paramref name="observer"/> is subscribed, renewing its subscription.
        /// </summary>
        /// <param name="id">
        /// The observer's identity.
        /// </param>
        /// <param name="observer">
        /// The observer.
        /// </param>
        /// <exception cref="Exception">A delegate callback throws an exception.</exception>
        public void Subscribe(TIdentity id, TObserver observer)
        {
            // Add or update the subscription.
            var now = GetDateTime();
            ObserverEntry entry;
            if (_observers.TryGetValue(id, out entry))
            {
                entry.LastSeen = now;
                entry.Observer = observer;
                if (_log.IsEnabled(LogLevel.Debug))
                {
                    _log.LogDebug("Updating entry for {Id}/{Observer}. {Count} total observers.", id, observer, _observers.Count);
                }
            }
            else
            {
                _observers[id] = new ObserverEntry { LastSeen = now, Observer = observer };
                if (_log.IsEnabled(LogLevel.Debug))
                {
                    _log.LogDebug("Adding entry for {Id}/{Observer}. {Count} total observers after add.", id, observer, _observers.Count);
                }
            }
        }

        /// <summary>
        /// Ensures that the provided <paramref name="id"/> is unsubscribed.
        /// </summary>
        /// <param name="id">
        /// The observer.
        /// </param>
        public void Unsubscribe(TIdentity id)
        {
            _log.LogDebug("Removed entry for {Id}. {Count} total observers after remove.", id, _observers.Count);
            _observers.Remove(id, out _);
        }

        /// <summary>
        /// Notifies all observers.
        /// </summary>
        /// <param name="notification">
        /// The notification delegate to call on each observer.
        /// </param>
        /// <param name="predicate">
        /// The predicate used to select observers to notify.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the work performed.
        /// </returns>
        public async Task Notify(Func<TObserver, Task> notification, Func<TObserver, bool> predicate = null)
        {
            var now = GetDateTime();
            var defunct = default(List<TIdentity>);
            foreach (var observer in _observers)
            {
                if (observer.Value.LastSeen + ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= new List<TIdentity>();
                    defunct.Add(observer.Key);
                    continue;
                }

                // Skip observers which don't match the provided predicate.
                if (predicate != null && !predicate(observer.Value.Observer))
                {
                    continue;
                }

                try
                {
                    await notification(observer.Value.Observer);
                }
                catch (Exception)
                {
                    // Failing observers are considered defunct and will be removed..
                    defunct ??= new List<TIdentity>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct != default(List<TIdentity>))
            {
                foreach (var observer in defunct)
                {
                    _observers.Remove(observer, out _);
                    if (_log.IsEnabled(LogLevel.Debug))
                    {
                        _log.LogDebug("Removing defunct entry for {0}. {1} total observers after remove.", observer, _observers.Count);
                    }
                }
            }
        }

        /// <summary>
        /// Notifies all observers which match the provided <paramref name="predicate"/>.
        /// </summary>
        /// <param name="notification">
        /// The notification delegate to call on each observer.
        /// </param>
        /// <param name="predicate">
        /// The predicate used to select observers to notify.
        /// </param>
        public void Notify(Action<TObserver> notification, Func<TObserver, bool> predicate = null)
        {
            var now = GetDateTime();
            var defunct = default(List<TIdentity>);
            foreach (var observer in _observers)
            {
                if (observer.Value.LastSeen + ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= new List<TIdentity>();
                    defunct.Add(observer.Key);
                    continue;
                }

                // Skip observers which don't match the provided predicate.
                if (predicate != null && !predicate(observer.Value.Observer))
                {
                    continue;
                }

                try
                {
                    notification(observer.Value.Observer);
                }
                catch (Exception)
                {
                    // Failing observers are considered defunct and will be removed..
                    defunct ??= new List<TIdentity>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct != default(List<TIdentity>))
            {
                foreach (var observer in defunct)
                {
                    _observers.Remove(observer, out _);
                    if (_log.IsEnabled(LogLevel.Debug))
                    {
                        _log.LogDebug("Removing defunct entry for {Observer}. {Count} total observers after remove.", observer, _observers.Count);
                    }
                }
            }
        }

        /// <summary>
        /// Removed all expired observers.
        /// </summary>
        public void ClearExpired()
        {
            var now = GetDateTime();
            var defunct = default(List<TIdentity>);
            foreach (var observer in _observers)
            {
                if (observer.Value.LastSeen + ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= new List<TIdentity>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct is { Count: > 0 })
            {
                _log.LogInformation("Removing {Count} defunct observers entries.", defunct.Count);
                foreach (var observer in defunct)
                {
                    _observers.Remove(observer, out _);
                }
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<TObserver> GetEnumerator() => _observers.Select(observer => observer.Value.Observer).GetEnumerator();

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Collections.IEnumerator"/> object that can be used to iterate through the collection.
        /// </returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// An observer entry.
        /// </summary>
        private class ObserverEntry
        {
            /// <summary>
            /// Gets or sets the observer.
            /// </summary>
            public TObserver Observer { get; set; }

            /// <summary>
            /// Gets or sets the UTC last seen time.
            /// </summary>
            public DateTime LastSeen { get; set; }
        }
    }

}
