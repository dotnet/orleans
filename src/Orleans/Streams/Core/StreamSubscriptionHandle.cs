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
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Handle representing this subsription.
    /// Consumer may serialize and store the handle in order to unsubsribe later, for example
    /// in another activation on this grain.
    /// </summary>
    [Serializable]
    public abstract class StreamSubscriptionHandle<T> : IEquatable<StreamSubscriptionHandle<T>>
    {
        public abstract IStreamIdentity StreamIdentity { get; }

        /// <summary>
        /// Unsubscribe a stream consumer from this observable.
        /// </summary>
        /// <param name="handle">The stream handle to unsubscribe.</param>
        /// <returns>A promise to unsubscription action.
        /// </returns>
        public abstract Task UnsubscribeAsync();

        /// <summary>
        /// Resumed consumption from a subscription to a stream.
        /// </summary>
        /// <param name="handle">The stream handle to consume from.</param>
        /// <returns>A promise with an updates subscription handle.
        /// </returns>
        public abstract Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null);

        #region IEquatable<StreamSubscriptionHandle<T>> Members

        public abstract bool Equals(StreamSubscriptionHandle<T> other);

        #endregion
    }
}
