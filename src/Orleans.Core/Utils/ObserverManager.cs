#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Collections.Generic;
using Orleans.Runtime;
using System.Linq;

namespace Orleans.Utilities;

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
public partial class ObserverManager<TIdentity, TObserver> : IEnumerable<TObserver> where TIdentity : notnull
{
    /// <summary>
    /// The observers.
    /// </summary>
    private Dictionary<TIdentity, ObserverEntry> _observers = [];

    /// <summary>
    /// The log.
    /// </summary>
    private readonly ILogger _log;

    // The number of concurrent readers.
    private int _numReaders;

    // The most recently captured read snapshot.
    // Each time a reader is added, we capture the current _observers reference here, signaling to writers
    // that _observers must be set to a copy before modifying.
    private Dictionary<TIdentity, ObserverEntry>? _readSnapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObserverManager{TIdentity,TObserver}"/> class.
    /// </summary>
    /// <param name="expiration">
    /// The expiration.
    /// </param>
    /// <param name="log">The log.</param>
    public ObserverManager(TimeSpan expiration, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
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
    public IReadOnlyDictionary<TIdentity, TObserver> Observers => _observers.ToDictionary(_ => _.Key, _ => _.Value.Observer);

    /// <summary>
    /// Removes all observers.
    /// </summary>
    public void Clear()
    {
        var observers = GetWritableObservers();
        observers.Clear();
    }

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
        var observers = GetWritableObservers();

        // Add or update the subscription.
        var now = GetDateTime();
        if (observers.TryGetValue(id, out var entry))
        {
            entry.LastSeen = now;
            entry.Observer = observer;
            LogDebugUpdatingEntry(id, observer, _observers.Count);
        }
        else
        {
            _observers[id] = new ObserverEntry { LastSeen = now, Observer = observer };
            LogDebugAddingEntry(id, observer, _observers.Count);
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
        var observers = GetWritableObservers();

        observers.Remove(id, out _);
        LogDebugRemovedEntry(id, observers.Count);
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
    public async Task Notify(Func<TObserver, Task> notification, Func<TObserver, bool>? predicate = null)
    {
        var now = GetDateTime();
        var defunct = default(List<TIdentity>);

        using (var snapshot = CreateReadSnapshot())
        {
            foreach (var observerEntryPair in snapshot.Observers)
            {
                if (observerEntryPair.Value.LastSeen + ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= [];
                    defunct.Add(observerEntryPair.Key);
                    continue;
                }

                // Skip observers which don't match the provided predicate.
                if (predicate is not null && !predicate(observerEntryPair.Value.Observer))
                {
                    continue;
                }

                try
                {
                    await notification(observerEntryPair.Value.Observer);
                }
                catch (Exception)
                {
                    // Failing observers are considered defunct and will be removed.
                    defunct ??= [];
                    defunct.Add(observerEntryPair.Key);
                }
            }
        }

        RemoveDefunct(defunct);
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
    public void Notify(Action<TObserver> notification, Func<TObserver, bool>? predicate = null)
    {
        var now = GetDateTime();
        var defunct = default(List<TIdentity>);

        using (var snapshot = CreateReadSnapshot())
        {
            foreach (var observerEntryPair in snapshot.Observers)
            {
                if (observerEntryPair.Value.LastSeen + ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= [];
                    defunct.Add(observerEntryPair.Key);
                    continue;
                }

                // Skip observers which don't match the provided predicate.
                if (predicate is not null && !predicate(observerEntryPair.Value.Observer))
                {
                    continue;
                }

                try
                {
                    notification(observerEntryPair.Value.Observer);
                }
                catch (Exception)
                {
                    // Failing observers are considered defunct and will be removed.
                    defunct ??= [];
                    defunct.Add(observerEntryPair.Key);
                }
            }
        }

        RemoveDefunct(defunct);
    }

    /// <summary>
    /// Removed all expired observers.
    /// </summary>
    public void ClearExpired()
    {
        var defunct = default(List<TIdentity>);
        using (var snapshot = CreateReadSnapshot())
        {
            var now = GetDateTime();

            foreach (var observerEntryPair in snapshot.Observers)
            {
                if (observerEntryPair.Value.LastSeen + ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct ??= [];
                    defunct.Add(observerEntryPair.Key);
                }
            }
        }

        RemoveDefunct(defunct);
    }

    private void RemoveDefunct(List<TIdentity>? defunct)
    {
        // Remove defunct observers.
        if (defunct is { Count: > 0 })
        {
            var observers = GetWritableObservers();

            LogDebugRemovingDefunctObservers(defunct.Count);
            foreach (var observerIdToRemove in defunct)
            {
                observers.Remove(observerIdToRemove, out _);
            }
        }
    }

    /// <summary>
    /// Returns an enumerator that iterates through the collection.
    /// </summary>
    /// <returns>
    /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.
    /// </returns>
    public IEnumerator<TObserver> GetEnumerator() => new ObserverEnumerator(CreateReadSnapshot());

    /// <summary>
    /// Returns an enumerator that iterates through a collection.
    /// </summary>
    /// <returns>
    /// An <see cref="T:System.Collections.IEnumerator"/> object that can be used to iterate through the collection.
    /// </returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private ReadSnapshot CreateReadSnapshot()
    {
        ++_numReaders;
        var observers = _readSnapshot = _observers;
        return new(this, observers);
    }

    private Dictionary<TIdentity, ObserverEntry> GetWritableObservers()
    {
        if (_numReaders > 0 && ReferenceEquals(_observers, _readSnapshot))
        {
            _observers = new Dictionary<TIdentity, ObserverEntry>(_observers);
        }

        return _observers;
    }

    /// <summary>
    /// An observer entry.
    /// </summary>
    private sealed class ObserverEntry
    {
        /// <summary>
        /// Gets or sets the observer.
        /// </summary>
        public required TObserver Observer { get; set; }

        /// <summary>
        /// Gets or sets the UTC last seen time.
        /// </summary>
        public DateTime LastSeen { get; set; }
    }

    private readonly struct ReadSnapshot(
        ObserverManager<TIdentity, TObserver> manager,
        IReadOnlyDictionary<TIdentity, ObserverEntry> snapshot) : IDisposable
    {
        public IReadOnlyDictionary<TIdentity, ObserverEntry> Observers { get; } = snapshot;

        public void Dispose()
        {
            if (--manager._numReaders == 0)
            {
                manager._readSnapshot = null;
            }
        }
    }

    private sealed class ObserverEnumerator(ReadSnapshot snapshot) : IEnumerator<TObserver>
    {
        private bool _isDisposed;
        private readonly IEnumerator<KeyValuePair<TIdentity, ObserverEntry>> _enumerator = snapshot.Observers.GetEnumerator();
        public TObserver Current => _enumerator.Current.Value.Observer;
        object? IEnumerator.Current => Current;
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _enumerator.Dispose();
                snapshot.Dispose();
            }
        }

        public bool MoveNext() => _enumerator.MoveNext();
        public void Reset() => _enumerator.Reset();
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Updating entry for {Id}/{Observer}. {Count} total observers."
    )]
    private partial void LogDebugUpdatingEntry(TIdentity id, TObserver observer, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Adding entry for {Id}/{Observer}. {Count} total observers after add."
    )]
    private partial void LogDebugAddingEntry(TIdentity id, TObserver observer, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Removed entry for {Id}. {Count} total observers after remove."
    )]
    private partial void LogDebugRemovedEntry(TIdentity id, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Removing {Count} defunct observers entries."
    )]
    private partial void LogDebugRemovingDefunctObservers(int count);
}
