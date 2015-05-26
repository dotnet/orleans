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
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// This interface generalizes the standard .NET IObserveable interface to allow asynchronous consumption of items.
    /// Asynchronous here means that the consumer can process items asynchronously and signal item completion to the 
    /// producer by completing the returned Task.
    /// <para>
    /// Note that this interface is invoked (used) by item consumers and implemented by item producers.
    /// This means that the producer endpoint of a stream implements this interface.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type of object produced by the observable.</typeparam>
    public interface IAsyncObservable<T>
    {
        /// <summary>
        /// Subscribe a consumer to this observable.
        /// </summary>
        /// <param name="observer">The asynchronous observer to subscribe.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer);

        /// <summary>
        /// Subscribe a consumer to this observable.
        /// </summary>
        /// <param name="observer">The asynchronous observer to subscribe.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <param name="filterFunc">Filter to be applied for this subscription</param>
        /// <param name="filterData">Data object that will be passed in to the filterFunc.
        /// This will usually contain any paramaters required by the filterFunc to make it's filtering decision.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if the supplied stream filter function is not suitable. 
        /// Usually this is because it is not a static method. </exception>
        Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer, StreamSequenceToken token,
            StreamFilterPredicate filterFunc = null,
            object filterData = null);
    }
}
