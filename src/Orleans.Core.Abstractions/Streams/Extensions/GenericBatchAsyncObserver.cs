using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Class used by the IAsyncBatchObservable extension methods to allow observation via delegate.
    /// </summary>
    /// <typeparam name="T">The type of object produced by the observable.</typeparam>
    internal class GenericAsyncBatchObserver<T> : IAsyncBatchObserver<T>
    {
        private Func<IList<OrderedItem<T>>, Task> onNextAsync;
        private Func<Exception, Task> onErrorAsync;
        private Func<Task> onCompletedAsync;

        public GenericAsyncBatchObserver(Func<IList<OrderedItem<T>>, Task> onNextAsync, Func<Exception, Task> onErrorAsync, Func<Task> onCompletedAsync)
        {
            this.onNextAsync = onNextAsync ?? throw new ArgumentNullException("onNextAsync");
            this.onErrorAsync = onErrorAsync ?? throw new ArgumentNullException("onErrorAsync");
            this.onCompletedAsync = onCompletedAsync ?? throw new ArgumentNullException("onCompletedAsync");
        }

        public Task OnNextAsync(IList<OrderedItem<T>> items)
        {
            return this.onNextAsync(items);
        }

        public Task OnCompletedAsync()
        {
            return this.onCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            return this.onErrorAsync(ex);
        }
    }
}
