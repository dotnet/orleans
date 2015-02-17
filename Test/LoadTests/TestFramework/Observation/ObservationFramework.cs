using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Orleans.TestFramework
{
    /// <summary>
    /// Represents the subscription.
    /// </summary>
    public class Subscription : IDisposable
    {
        static Dictionary<object, List<object>> subscriptions = new Dictionary<object, List<object>>();
        /// <summary>
        /// subscriber
        /// </summary>
        object subscriber;
        /// <summary>
        /// publisher 
        /// </summary>
        object publisher;
        /// <summary>
        /// Creates a subscription object.
        /// </summary>
        /// <param name="publisher">publisher</param>
        /// <param name="subscriber">subscriber</param>
        public Subscription(object publisher, object subscriber)
        {
            Assert.IsNotNull(publisher);
            Assert.IsNotNull(subscriber);
            this.subscriber = subscriber;
            this.publisher = publisher;
            lock (subscriptions)
            {
                if (!subscriptions.ContainsKey(publisher))
                {
                    subscriptions.Add(publisher, new List<object>());
                }
                List<object> lst = subscriptions[publisher];
                lock (lst)
                {
                    if (!lst.Contains(subscriber))
                        lst.Add(subscriber);
                }
            }
        }
        /// <summary>
        /// Required method.
        /// Removes the connection between subscriber and publisher.
        /// </summary>
        public void Dispose()
        {
            lock (subscriptions)
            {
                List<object> lst = subscriptions[publisher];
                lock (lst)
                {
                    lst.Remove(subscriber);
                }
            }
        }
        /// <summary>
        /// Gets all the subscribers for the given publisher
        /// </summary>
        /// <param name="publisher">publisher.</param>
        /// <returns></returns>
        public static IEnumerable<object> GetSubscribers(object publisher)
        {
            lock (subscriptions)
            {
                return subscriptions.ContainsKey(publisher) ? subscriptions[publisher] : null;
            }
        }
    }
    /// <summary>
    /// A utility class that can be used to create the observers by passing lambda for each method of IObserver
    /// </summary>
    /// <typeparam name="TSourceFrom"></typeparam>
    public class LambdaWrapperObserver<TSourceFrom> : IObserver<TSourceFrom>
    {
        /// <summary>
        /// OnCompleted implemetation passed in by user
        /// </summary>
        Action onCompletedHandler;
        /// <summary>
        /// OnError implemetation passed in by user
        /// </summary>
        Action<Exception> onErrorHandler;
        /// <summary>
        /// OnNext implemetation passed in by user
        /// </summary>
        Action<TSourceFrom> onNextHandler;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="onNextHandler"></param>
        /// <param name="onCompletedHandler"></param>
        /// <param name="onErrorHandler"></param>
        public LambdaWrapperObserver(Action<TSourceFrom> onNextHandler, Action onCompletedHandler=null, Action<Exception> onErrorHandler = null)
        {
            this.onNextHandler = onNextHandler;
            this.onErrorHandler = onErrorHandler;
            this.onCompletedHandler = onCompletedHandler;
        }
        /// <summary>
        ///  Notifies the observer that the provider has finished sending push-based notifications.
        /// </summary>
        public void OnCompleted()
        {
            if (null != onCompletedHandler) onCompletedHandler();
        }
        /// <summary>
        /// Notifies the observer that the provider has experienced an error condition.
        /// </summary>
        /// <param name="error">An object that provides additional information about the error.</param>
        public void OnError(Exception error)
        {
            if (null != onErrorHandler) onErrorHandler(error);
        }
        /// <summary>
        /// Provides the observer with new data.
        /// </summary>
        /// <param name="value">The current notification information.</param>
        public void OnNext(TSourceFrom value)
        {
            if (null != onNextHandler) onNextHandler(value);
        }
    }

    /// <summary>
    /// A utility class that defines base class for the "Pipe" or "Filter" in the event stream..
    /// Observes the source event stream and acts a observable for target.
    /// Most of the implementation is just the plumbing.
    /// However most important method is the Transform that takes in object of type Source  and converts it into Target
    /// </summary>
    /// <typeparam name="TSourceFrom">Source/Input data type</typeparam>
    /// <typeparam name="TSourceTo">Target/Output data type</typeparam>
    public class BaseFilter<TSourceFrom, TSourceTo> : IObservable<TSourceTo>, IObserver<TSourceFrom>
    {
        /// <summary>
        /// True when the Source event stream is completed.
        /// </summary>
        public bool IsComplete { get; private set; }
        
        /// <summary>
        /// Notifies the provider that an observer is to receive notifications.
        /// </summary>
        /// <param name="onNextHandler">Lambda for handling OnNext.</param>
        /// <param name="onCompletedHandler">Lambda for handling OnCompleted.</param>
        /// <param name="onErrorHandler">Lambda for handling OnError.</param>
        /// <returns>The observer's interface that enables resources to be disposed.</returns>
        public IDisposable Subscribe(Action<TSourceTo> onNextHandler, Action onCompletedHandler = null, Action<Exception> onErrorHandler = null)
        {
            IObserver<TSourceTo> observer = new LambdaWrapperObserver<TSourceTo>(onNextHandler, onCompletedHandler, onErrorHandler);
            return new Subscription(this, observer);
        }
        /// <summary>
        /// Notifies the provider that an observer is to receive notifications.
        /// </summary>
        /// <param name="observer">The object that is to receive notifications.</param>
        /// <returns>The observer's interface that enables resources to be disposed.</returns>
        public IDisposable Subscribe(IObserver<TSourceTo> observer)
        {
            return new Subscription(this, observer);
        }
        /// <summary>
        /// Notifies the observer that the provider has finished sending push-based notifications.
        /// </summary>
        public void OnCompleted()
        {
            var subscribers = Subscription.GetSubscribers(this);
            if (null != subscribers)
            {
                foreach (IObserver<TSourceTo> subscriber in subscribers.Cast<IObserver<TSourceTo>>())
                {
                    try
                    {
                        subscriber.OnCompleted();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            IsComplete = true;
        }
        /// <summary>
        /// Notifies the observer that the provider has experienced an error condition.
        /// </summary>
        /// <param name="error">An object that provides additional information about the error.</param>
        public void OnError(Exception error)
        {
            var subscribers = Subscription.GetSubscribers(this);
            if (null != subscribers)
            {
                foreach (IObserver<TSourceTo> subscriber in subscribers.Cast<IObserver<TSourceTo>>())
                {
                    try
                    {
                        subscriber.OnError(error);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        /// <summary>
        /// Provides the observer with new data.
        /// </summary>
        /// <param name="value">The current notification information.</param>
        public void OnNext(TSourceFrom value)
        {
            var subscribers = Subscription.GetSubscribers(this);
            if (null != subscribers)
            {
                foreach (IObserver<TSourceTo> subscriber in subscribers.Cast<IObserver<TSourceTo>>())
                {
                    try
                    {
                        TSourceTo newValue = Transform(value);
                        subscriber.OnNext(newValue);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        /// <summary>
        /// Transforms the source object to target object
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual TSourceTo Transform(TSourceFrom value)
        {
            return default(TSourceTo);
        }
    }

    /// <summary>
    /// Group observer. Aggregates the events fromState different sources into just one target
    /// </summary>
    /// <typeparam name="Tsource"></typeparam>
    public class GroupObserver<Tsource> : BaseFilter<Tsource, Tsource>
    { 
        List<IObservable<Tsource>> sources = new List<IObservable<Tsource>>();
        /// <summary>
        /// Creates a group observer
        /// </summary>
        /// <param name="sources">sources</param>
        public GroupObserver(params IObservable<Tsource>[] sources ) 
        {
            this.sources.AddRange(sources);
            foreach(IObservable<Tsource> source in this.sources)
            {
                source.Subscribe(this);
            }
        }
        /// <summary>
        /// Adds a new source
        /// </summary>
        /// <param name="source">source</param>
        /// <returns>self so that we can create compsitions</returns>
        public GroupObserver<Tsource> Join(IObservable<Tsource> source)
        {
            sources.Add(source);
            source.Subscribe(this);
            return this;
        }
        /// <summary>
        /// Joins multiple streams as sources
        /// </summary>
        /// <param name="sources">sources</param>
        /// <returns>Newly created Group observer</returns>
        public static GroupObserver<Tsource> Join(params IObservable<Tsource>[] sources)
        {
            return new GroupObserver<Tsource>(sources);
        }
        /// <summary>
        /// Overrides the base method to just pass along the passed in object.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        protected override Tsource Transform(Tsource value)
        {
            return value;
        }
    }
}
