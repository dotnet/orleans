/*
Project Orleans Cloud Service SDK ver. 1.0
 
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
    /// Class used by the IAsyncObservable extension methods to allow observation via delegate.
    /// </summary>
    /// <typeparam name="T">The type of object produced by the observable.</typeparam>
    internal class GenericAsyncObserver<T> : IAsyncObserver<T>
    {
        private Func<T, StreamSequenceToken, Task> onNextAsync;
        private Func<Exception, Task> onErrorAsync;
        private Func<Task> onCompletedAsync;

        public GenericAsyncObserver(Func<T, StreamSequenceToken, Task> onNextAsync, Func<Exception, Task> onErrorAsync, Func<Task> onCompletedAsync)
        {
            if (onNextAsync == null) throw new ArgumentNullException("onNextAsync");
            if (onErrorAsync == null) throw new ArgumentNullException("onErrorAsync");
            if (onCompletedAsync == null) throw new ArgumentNullException("onCompletedAsync");

            this.onNextAsync = onNextAsync;
            this.onErrorAsync = onErrorAsync;
            this.onCompletedAsync = onCompletedAsync;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            return onNextAsync(item, token);
        }

        public Task OnCompletedAsync()
        {
            return onCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            return onErrorAsync(ex);
        }
    }
}
