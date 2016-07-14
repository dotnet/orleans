namespace Orleans.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using Orleans.Streams;
    using Orleans.Streams.AdHoc;

    internal class ObserverGrainExtension : IObserverGrainExtension
    {
        private readonly Dictionary<Guid, IUntypedGrainObserver> observers = new Dictionary<Guid, IUntypedGrainObserver>();

        public Task OnNextAsync(Guid streamId, object value, StreamSequenceToken token) => this.observers[streamId].OnNextAsync(streamId, value, token);

        public Task OnErrorAsync(Guid streamId, Exception exception) => this.GetAndRemove(streamId).OnErrorAsync(streamId, exception);

        public Task OnCompletedAsync(Guid streamId) => this.GetAndRemove(streamId).OnCompletedAsync(streamId);

        public void Register(Guid streamId, IUntypedGrainObserver observer) => this.observers.Add(streamId, observer);

        public void Remove(Guid streamId) => this.observers.Remove(streamId);

        private IUntypedGrainObserver GetAndRemove(Guid streamId)
        {
            IUntypedGrainObserver observer;
            if (!this.observers.TryGetValue(streamId, out observer))
            {
                throw new KeyNotFoundException($"Observable with id {streamId} not found.");
            }

            this.observers.Remove(streamId);
            return observer;
        }
    }
}