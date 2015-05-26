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
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// This interface represents an object that serves as a distributed rendevous between producers and consumers.
    /// It is similar to a Reactive Framework <code>Subject</code> and implements
    /// <code>IObserver</code> nor <code>IObservable</code> interfaces.
    /// </summary>
    /// <typeparam name="T">The type of object that flows through the stream.</typeparam>
    public interface IAsyncStream<T> : IStreamIdentity, IEquatable<IAsyncStream<T>>, IComparable<IAsyncStream<T>>, IAsyncObservable<T>, IAsyncBatchObserver<T>
    {
        /// <summary>
        /// Determines whether this is a rewindable stream - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream, false otherwise.</returns>
        bool IsRewindable { get; }

        /// <summary> Stream Provider Name. </summary>
        string ProviderName { get; }

        /// <summary>
        /// Retrieves a list of all active subscriptions created by the caller for this stream.
        /// </summary>
        /// <returns></returns>
        Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptionHandles();
    }
}
