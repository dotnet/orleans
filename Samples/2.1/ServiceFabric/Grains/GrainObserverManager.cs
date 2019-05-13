using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Grains
{
    /// <summary>
    /// Maintains a collection of grain observers.
    /// </summary>
    /// <typeparam name="T">
    /// The grain observer type.
    /// </typeparam>
    public class GrainObserverManager<T> : IEnumerable<T> where T : IAddressable
    {
        /// <summary>
        /// The observers.
        /// </summary>
        private readonly Dictionary<T, DateTime> observers = new Dictionary<T, DateTime>();

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainObserverManager{T}"/> class.
        /// </summary>
        public GrainObserverManager()
        {
            this.GetDateTime = () => DateTime.UtcNow;
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
        public int Count => this.observers.Count;

        /// <summary>
        /// Removes all observers.
        /// </summary>
        public void Clear()
        {
            this.observers.Clear();
        }

        /// <summary>
        /// Ensures that the provided <paramref name="observer"/> is subscribed, renewing its subscription.
        /// </summary>
        /// <param name="observer">The observer.</param>
        public void Subscribe(T observer)
        {
            // Add or update the subscription.
            this.observers[observer] = this.GetDateTime();
        }

        /// <summary>
        /// Ensures that the provided <paramref name="observer"/> is unsubscribed.
        /// </summary>
        /// <param name="observer">The observer.</param>
        public void Unsubscribe(T observer)
        {
            this.observers.Remove(observer);
        }

        /// <summary>
        /// Notifies all observers.
        /// </summary>
        /// <param name="notification">
        /// The notification delegate to call on each observer.
        /// </param>
        /// <param name="predicate">The predicate used to select observers to notify.</param>
        /// <returns>
        /// A <see cref="Task"/> representing the work performed.
        /// </returns>
        public async Task Notify(Func<T, Task> notification, Func<T, bool> predicate = null)
        {
            var now = this.GetDateTime();
            var defunct = default(List<T>);
            foreach (var observer in this.observers)
            {
                if (observer.Value + this.ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct = defunct ?? new List<T>();
                    defunct.Add(observer.Key);
                    continue;
                }

                // Skip observers which don't match the provided predicate.
                if (predicate != null && !predicate(observer.Key))
                {
                    continue;
                }

                try
                {
                    await notification(observer.Key);
                }
                catch (Exception)
                {
                    // Failing observers are considered defunct and will be removed..
                    defunct = defunct ?? new List<T>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct != default(List<T>))
            {
                foreach (var observer in defunct)
                {
                    this.observers.Remove(observer);
                }
            }
        }

        /// <summary>
        /// Notifies all observers which match the provided <paramref name="predicate"/>.
        /// </summary>
        /// <param name="notification">
        /// The notification delegate to call on each observer.
        /// </param>
        /// <param name="predicate">The predicate used to select observers to notify.</param>
        public void Notify(Action<T> notification, Func<T, bool> predicate = null)
        {
            var now = this.GetDateTime();
            var defunct = default(List<T>);
            foreach (var observer in this.observers)
            {
                if (observer.Value + this.ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct = defunct ?? new List<T>();
                    defunct.Add(observer.Key);
                    continue;
                }

                // Skip observers which don't match the provided predicate.
                if (predicate != null && !predicate(observer.Key))
                {
                    continue;
                }

                try
                {
                    notification(observer.Key);
                }
                catch (Exception)
                {
                    // Failing observers are considered defunct and will be removed..
                    defunct = defunct ?? new List<T>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct != default(List<T>))
            {
                foreach (var observer in defunct)
                {
                    this.observers.Remove(observer);
                }
            }
        }

        /// <summary>
        /// Removed all expired observers.
        /// </summary>
        public void ClearExpired()
        {
            var now = this.GetDateTime();
            var defunct = default(List<T>);
            foreach (var observer in this.observers)
            {
                if (observer.Value + this.ExpirationDuration < now)
                {
                    // Expired observers will be removed.
                    defunct = defunct ?? new List<T>();
                    defunct.Add(observer.Key);
                }
            }

            // Remove defunct observers.
            if (defunct != default(List<T>))
            {
                foreach (var observer in defunct)
                {
                    this.observers.Remove(observer);
                }
            }
        }

        /// <summary>
        /// Returns the enumerator for all observers.
        /// </summary>
        /// <returns>The enumerator for all observers.</returns>
        public IEnumerator<T> GetEnumerator()
        {
            return this.observers.Keys.GetEnumerator();
        }

        /// <summary>
        /// Returns the enumerator for all observers.
        /// </summary>
        /// <returns>The enumerator for all observers.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.observers.Keys.GetEnumerator();
        }
    }
}