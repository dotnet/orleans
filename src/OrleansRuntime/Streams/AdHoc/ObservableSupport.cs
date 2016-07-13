using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Providers;
using Orleans.Streams.AdHoc;

namespace Orleans.Runtime
{
    using Orleans.Streams;

    internal class ObserverGrainExtensionManager : IObserverGrainExtensionManager
    {
        private readonly IGrainExtensionManager extensionManager;

        public ObserverGrainExtensionManager(IGrainExtensionManager extensionManager)
        {
            this.extensionManager = extensionManager;
        }

        public IObserverGrainExtension GetOrAddExtension()
        {
            IObserverGrainExtensionRemote handler;
            if (!this.extensionManager.TryGetExtensionHandler(out handler))
            {
                this.extensionManager.TryAddExtension(handler = new ObserverGrainExtension(), typeof(IObserverGrainExtensionRemote));
            }

            return handler as IObserverGrainExtension;
        }
    }

    internal class ObserverGrainExtension : IObserverGrainExtension
    {
        private readonly Dictionary<Guid, IUntypedGrainObserver> observers = new Dictionary<Guid, IUntypedGrainObserver>();

        public Task OnNext(Guid streamId, object value, StreamSequenceToken token) => this.observers[streamId].OnNextAsync(streamId, value, token);

        public Task OnError(Guid streamId, Exception exception) => this.GetAndRemove(streamId).OnErrorAsync(streamId, exception);

        public Task OnCompleted(Guid streamId) => this.GetAndRemove(streamId).OnCompletedAsync(streamId);

        public void Register(Guid streamId, IUntypedGrainObserver observer) => this.observers.Add(streamId, observer);

        public void Remove(Guid streamId) => this.observers.Remove(streamId);

        private IUntypedGrainObserver GetAndRemove(Guid streamId)
        {
            IUntypedGrainObserver observer;
            if (!this.observers.TryGetValue(streamId, out observer))
            {
                throw new KeyNotFoundException($"Observable with id {streamId}.");
            }

            this.observers.Remove(streamId);
            return observer;
        }
    }

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

#warning automatically unsubscribe if remote endpoint fails.
        public async Task SubscribeClient(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver receiver, StreamSequenceToken token)
        {
            // If an existing subscription exists, then this is a resume call and nothing needs to be done.
            object subscription;
            if (this.observers.TryGetValue(streamId, out subscription)) return;

            var invoker = this.invokable.GetInvoker(request.InterfaceId, this.genericGrainType);
            var result = await invoker.Invoke(this.grain, request);

            try
            {
                subscription = await StreamDelegateHelper.Subscribe(result, receiver, streamId, token);
                this.observers.Add(streamId, subscription);
            }
            catch (Exception exception)
            {
                await receiver.OnErrorAsync(streamId, exception);
            }
        }

        public async Task SubscribeGrain(Guid streamId, InvokeMethodRequest request, GrainReference remoteGrain, StreamSequenceToken token)
        {
            // If an existing subscription exists, then this is a resume call and nothing needs to be done.
            object subscription;
            if (this.observers.TryGetValue(streamId, out subscription)) return;

            var invoker = this.invokable.GetInvoker(request.InterfaceId, this.genericGrainType);
            var result = await invoker.Invoke(this.grain, request);
            var receiver = remoteGrain.AsReference<IObserverGrainExtensionRemote>();

            try
            {
                subscription = await StreamDelegateHelper.Subscribe(result, receiver, streamId, token);
                this.observers.Add(streamId, subscription);
            }
            catch (Exception exception)
            {
                await receiver.OnError(streamId, exception);
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