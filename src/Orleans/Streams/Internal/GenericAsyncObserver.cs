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
        private Func<T, StreamSequenceToken, Task> _NextAsync;
        private Func<Task> _CompletedAsync;
        private Func<Exception, Task> _ErrorAsync;

        public GenericAsyncObserver( Func<T, StreamSequenceToken, Task> onNextAsync, Func<Task> onCompletedAsync, Func<Exception, Task> onErrorAsync )
        {
            _NextAsync = onNextAsync;
            _CompletedAsync = onCompletedAsync;
            _ErrorAsync = onErrorAsync;
        }

        public Task OnNextAsync( T item, StreamSequenceToken token = null )
        {
            return _NextAsync( item, token );
        }

        public Task OnCompletedAsync()
        {
            return _CompletedAsync();
        }

        public Task OnErrorAsync( Exception ex )
        {
            return _ErrorAsync( ex );
        }
    }
}
