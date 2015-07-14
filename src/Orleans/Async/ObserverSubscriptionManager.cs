/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// The ObserverSubscriptionManager class is a helper class for grains that support observers.
    /// It provides methods for tracking subscribing observers and for sending notifications.
    /// </summary>
    /// <typeparam name="T">The observer interface type to be managed.</typeparam>
    [Serializable]
    public class ObserverSubscriptionManager<T>
        where T : IGrainObserver
    {
        /// <summary>
        /// Number of subscribers currently registered
        /// </summary>
        public int Count 
        {
            get { return observers.Count; }
        }

        /// <summary>
        /// The set of currently-subscribed observers.
        /// This is implemented as a HashSet of IGrainObserver so that if the same observer subscribes multiple times,
        /// it will still only get invoked once per notification.
        /// </summary>
        private readonly HashSet<T> observers;

        /// <summary>
        /// Constructs an empty subscription manager.
        /// </summary>
        public ObserverSubscriptionManager()
        {
            observers = new HashSet<T>();
        }

        /// <summary>
        /// Records a new subscribing observer.
        /// </summary>
        /// <param name="observer">The new subscriber.</param>
        /// <returns>A promise that resolves when the subscriber is added.
        /// <para>This promise will be broken if the observer is already a subscriber.
        /// In this case, the existing subscription is unaffected.</para></returns>
        public void Subscribe(T observer)
        {
            if (!observers.Add(observer))
                throw new OrleansException(String.Format("Cannot subscribe already subscribed observer {0}.", observer));
        }


        /// <summary>
        /// Removes a (former) subscriber.
        /// </summary>
        /// <param name="observer">The unsubscribing observer.</param>
        /// <returns>A promise that resolves when the subscriber is removed.
        /// This promise will be broken if the observer is not a subscriber.</returns>
        public void Unsubscribe(T observer)
        {
            if (!observers.Remove(observer))
                throw new OrleansException(String.Format("Observer {0} is not subscribed.", observer));
        }
        
        /// <summary>
        /// Removes all subscriptions.
        /// </summary>
        public void Clear()
        {
            observers.Clear();
        }

        /// <summary>
        /// Sends a notification to all subscribers.
        /// </summary>
        /// <param name="notification">An action that sends the notification by invoking the proper method on the provided subscriber.
        /// This action is called once for each current subscriber.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void Notify(Action<T> notification)
        {
            List<T> failed = null;
            foreach (var observer in observers)
            {
                try
                {
                    notification(observer);
                }
                catch (Exception)
                {
                    if (failed == null)
                    {
                        failed = new List<T>();
                    }
                    failed.Add(observer);
                }
            }
            if (failed != null)
            {
                foreach (var key in failed)
                {
                    observers.Remove(key);
                }
                throw new OrleansException(String.Format("Failed to notify the following observers: {0}", Utils.EnumerableToString(failed)));
            }
        }
    }
}
