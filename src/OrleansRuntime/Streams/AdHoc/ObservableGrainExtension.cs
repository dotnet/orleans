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
        private readonly Dictionary<Guid, object> observers = new Dictionary<Guid, object>();

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
        
        public async Task Subscribe(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver remoteClient, StreamSequenceToken token)
        {
            // If an existing subscription exists, then this is a resume call and nothing needs to be done.
            object subscription;
            if (this.observers.TryGetValue(streamId, out subscription)) return;

            var invoker = this.invokable.GetInvoker(request.InterfaceId, this.genericGrainType);
            var result = await invoker.Invoke(this.grain, request);

            try
            {
                subscription = await StreamDelegateHelper.Subscribe(result, remoteClient, streamId, token);
                this.observers.Add(streamId, subscription);
            }
            catch (Exception exception)
            {
                await remoteClient.OnErrorAsync(streamId, exception);
            }
        }

        public Task Unsubscribe(Guid streamId)
        {
            // If no subscription exists, return success.
            object subscription;
            if (!this.observers.TryGetValue(streamId, out subscription)) return Task.FromResult(0);

            this.observers.Remove(streamId);
            return StreamDelegateHelper.Unsubscribe(subscription);
        }
    }
}