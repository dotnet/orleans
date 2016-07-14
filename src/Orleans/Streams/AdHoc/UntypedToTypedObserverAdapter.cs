namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Adapts an <see cref="IUntypedGrainObserver"/> into an <see cref="IAsyncObserver{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal class UntypedToTypedObserverAdapter<T> : IAsyncObserver<T>, IUntypedObserverWrapper
    {
        private readonly Guid streamId;

        public UntypedToTypedObserverAdapter(Guid streamId, IUntypedGrainObserver observer)
        {
            this.Observer = observer;
            this.streamId = streamId;
        }

        public IUntypedGrainObserver Observer { get; set; }

        public Task OnNextAsync(T value, StreamSequenceToken token = null) => this.Observer.OnNextAsync(this.streamId, value, token);

        public Task OnErrorAsync(Exception exception) => this.Observer.OnErrorAsync(this.streamId, exception);

        public Task OnCompletedAsync() => this.Observer.OnCompletedAsync(this.streamId);
    }

    /// <summary>
    /// Interface for classes which hold an <see cref="IUntypedGrainObserver"/> object.
    /// </summary>
    /// <remarks>
    /// This allows for reusing the container and changing the underlying observer on-the-fly.
    /// </remarks>
    internal interface IUntypedObserverWrapper
    {
        /// <summary>
        /// Gets or sets the underlying observer.
        /// </summary>
        IUntypedGrainObserver Observer { get; set; }
    }
}