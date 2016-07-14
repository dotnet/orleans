namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Threading.Tasks;

    internal class UntypedToTypedObserverAdapter<T> : IAsyncObserver<T>
    {
        private readonly Guid streamId;

        public UntypedToTypedObserverAdapter(Guid streamId, IUntypedGrainObserver receiver)
        {
            this.Receiver = receiver;
            this.streamId = streamId;
        }

        public IUntypedGrainObserver Receiver { get; }

        public Task OnNextAsync(T value, StreamSequenceToken token = null) => this.Receiver.OnNextAsync(this.streamId, value, token);

        public Task OnErrorAsync(Exception exception) => this.Receiver.OnErrorAsync(this.streamId, exception);

        public Task OnCompletedAsync() => this.Receiver.OnCompletedAsync(this.streamId);
    }
}