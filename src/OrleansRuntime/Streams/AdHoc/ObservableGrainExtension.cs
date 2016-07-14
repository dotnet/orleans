namespace Orleans.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using Orleans.CodeGeneration;
    using Orleans.Runtime.Providers;
    using Orleans.Streams;
    using Orleans.Streams.AdHoc;

    /// <summary>
    /// Grain extension which allows grains to return observables.
    /// </summary>
    internal class ObservableGrainExtension : IObservableGrainExtension
    {
        private readonly IInvokable invokable;

        private readonly string genericGrainType;

        private readonly IAddressable grain;

        /// <summary>
        /// The mapping between stream id and disposable for each observer.
        /// </summary>
        private readonly Dictionary<Guid, Subscription> observers = new Dictionary<Guid, Subscription>();

        public ObservableGrainExtension(IInvokable invokable, string genericGrainType, IAddressable grain)
        {
            this.invokable = invokable;
            this.genericGrainType = genericGrainType;
            this.grain = grain;
        }

        internal static ObservableGrainExtension GetOrAddExtension(IInvokable invokable, string genericGrainType, IAddressable grain)
        {
            var runtime = SiloProviderRuntime.Instance;
            IObservableGrainExtension handler;
            if (!runtime.TryGetExtensionHandler(out handler))
            {
                runtime.TryAddExtension(handler = new ObservableGrainExtension(invokable, genericGrainType, grain));
            }

            return handler as ObservableGrainExtension;
        }
        
        public async Task Subscribe(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver observer, StreamSequenceToken token)
        {
            // If an existing subscription exists, then this is a resume call, so update the observer p
            Subscription subscription;
            if (this.observers.TryGetValue(streamId, out subscription))
            {
                // Update the existing observer's endpoint to point to the new observer.
                subscription.Observer.Observer = observer;
                return;
            }

            var invoker = this.invokable.GetInvoker(request.InterfaceId, this.genericGrainType);
            var observable = await invoker.Invoke(this.grain, request);
            
            IUntypedObserverWrapper wrapper;
            var subscriptionHandle = await StreamDelegateHelper.Subscribe(observable, observer, streamId, token, out wrapper);
            subscription = new Subscription(subscriptionHandle, wrapper);
            this.observers.Add(streamId, subscription);
        }

        public Task Unsubscribe(Guid streamId)
        {
            // If no subscription exists, return success.
            Subscription subscription;
            if (!this.observers.TryGetValue(streamId, out subscription)) return Task.FromResult(0);

            this.observers.Remove(streamId);
            return subscription.Unsubscribe();
        }

        private class Subscription
        {
            private readonly object handle;

            public Subscription(object handle, IUntypedObserverWrapper observer)
            {
                this.handle = handle;
                this.Observer = observer;
            }

            public IUntypedObserverWrapper Observer { get; }

            public Task Unsubscribe()
            {
                return StreamDelegateHelper.Unsubscribe(this.handle);
            }
        }
    }
}