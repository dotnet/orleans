namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Threading.Tasks;

    internal class TypedToUntypedObserverAdapter<T> : IUntypedGrainObserver
    {
        private readonly IAsyncObserver<T> observer;

        public TypedToUntypedObserverAdapter(IAsyncObserver<T> observer)
        {
            this.observer = observer;
        }

        public Task OnNextAsync(Guid streamId, object value, StreamSequenceToken token) => this.observer.OnNextAsync((T)value, token);

        public Task OnErrorAsync(Guid streamId, Exception exception) => this.observer.OnErrorAsync(exception);

        public Task OnCompletedAsync(Guid streamId) => this.observer.OnCompletedAsync();
    }
}