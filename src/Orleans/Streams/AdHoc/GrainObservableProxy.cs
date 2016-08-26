namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Threading.Tasks;

    using Orleans.CodeGeneration;
    using Orleans.Runtime;

    [Serializable]
    internal class GrainObservableProxy<T> : IAsyncObservable<T>
    {
        private readonly InvokeMethodRequest subscriptionRequest;

        private readonly GrainReference grain;

        [NonSerialized]
        private IObservableGrainExtension grainExtension;

        public IObservableGrainExtension GrainExtension
        {
            get
            {
                if (this.grainExtension == null)
                {
                    this.grainExtension = this.grain.AsReference<IObservableGrainExtension>();
                }

                return this.grainExtension;
            }
        }

        public GrainObservableProxy(GrainReference grain, InvokeMethodRequest subscriptionRequest)
        {
            this.grain = grain;
            this.subscriptionRequest = subscriptionRequest;
        }

        internal Task<StreamSubscriptionHandle<T>> ResumeAsync(Guid streamId, IAsyncObserver<T> observer, StreamSequenceToken token = null)
        {
            return this.SubscribeInternal(streamId, observer, token);
        }

#warning Call OnError if the silo goes down.
        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return this.SubscribeInternal(Guid.NewGuid(), observer, token: null);
        }

        private async Task<StreamSubscriptionHandle<T>> SubscribeInternal(Guid streamId, IAsyncObserver<T> observer, StreamSequenceToken token)
        {
            var activation = RuntimeClient.Current.CurrentActivationData;
            object observerReference;
            if (activation == null)
            {
                // The caller is a client, so create an object reference.
                var adapter = new TypedToUntypedObserverAdapter<T>(observer);
                var grainFactory = RuntimeClient.Current.InternalGrainFactory;
                var clientObjectReference = grainFactory.CreateObjectReference<IUntypedGrainObserver>(adapter);
                await this.GrainExtension.Subscribe(streamId, this.subscriptionRequest, clientObjectReference, token);
                observerReference = adapter;
            }
            else
            {
                // The caller is a grain, so get or install the observer extension.
                var caller = activation.GrainInstance;
                var grainExtensionManager =
                    caller?.Runtime?.ServiceProvider?.GetService(typeof(IObserverGrainExtensionManager)) as IObserverGrainExtensionManager;
                if (caller == null || grainExtensionManager == null)
                {
#warning throw?
                    throw new Exception("MUST REPLACE THIS");
                }

                // Wrap the observer and register it with the observer extension.
                var adapter = new TypedToUntypedObserverAdapter<T>(observer);
                var callerGrainExtension = grainExtensionManager.GetOrAddExtension();
                callerGrainExtension.Register(streamId, adapter);
                
                // Create a reference to the extension installed on the current grain.
                var extensionReference = activation.GrainReference.AsReference<IObserverGrainExtensionRemote>();

                // Subscribe the calling grain to the remote observable.
                await this.GrainExtension.Subscribe(streamId, this.subscriptionRequest, extensionReference, token);
                observerReference = adapter;
            }

            return new TransientStreamSubscriptionHandle<T>(streamId, this, observerReference);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken token,
            StreamFilterPredicate filterFunc = null,
            object filterData = null)
        {
            return this.SubscribeInternal(Guid.NewGuid(), observer, token);
        }
    }
}