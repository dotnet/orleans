using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Providers;
using Orleans.Streams.AdHoc;

namespace Orleans.Runtime
{
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
            if (!extensionManager.TryGetExtensionHandler(out handler))
            {
                extensionManager.TryAddExtension(
                    handler = new ObserverGrainExtension(),
                    typeof(IObserverGrainExtensionRemote));
            }

            return handler as IObserverGrainExtension;
        }
    }

    internal class ObserverGrainExtension : IObserverGrainExtension
    {
        private readonly Dictionary<Guid, IUntypedGrainObserver> observers =
            new Dictionary<Guid, IUntypedGrainObserver>();

        public Task OnNext(Guid streamId, object value) => observers[streamId].OnNext(value);

        public Task OnError(Guid streamId, Exception exception) => GetAndRemove(streamId).OnError(exception);
        public Task OnCompleted(Guid streamId) => GetAndRemove(streamId).OnCompleted();
        
        public void Register(Guid streamId, IUntypedGrainObserver observer) => observers.Add(streamId, observer);

        public void Remove(Guid streamId) => observers.Remove(streamId);

        private IUntypedGrainObserver GetAndRemove(Guid streamId)
        {
            IUntypedGrainObserver observer;
            if (!observers.TryGetValue(streamId, out observer))
            {
                throw new KeyNotFoundException($"Observable with id {streamId}.");
            }

            observers.Remove(streamId);
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
        private readonly Dictionary<Guid, IAsyncDisposable> observers = new Dictionary<Guid, IAsyncDisposable>();

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
        public async Task SubscribeClient(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver receiver)
        {
            var invoker = invokable.GetInvoker(request.InterfaceId, genericGrainType);
            var result = await invoker.Invoke(grain, request);
            
            try
            {
                var disposable = await ObservableSubscriberHelper.Subscribe(result, receiver);
                observers.Add(streamId, disposable);
            }
            catch (Exception exception)
            {
                await receiver.OnError(exception);
            }
        }

        public async Task SubscribeGrain(Guid streamId, InvokeMethodRequest request, GrainReference remoteGrain)
        {
            var invoker = invokable.GetInvoker(request.InterfaceId, genericGrainType);
            var result = await invoker.Invoke(grain, request);
            var receiver = remoteGrain.AsReference<IObserverGrainExtensionRemote>();

            try
            {
                var disposable = await ObservableSubscriberHelper.Subscribe(result, receiver, streamId);
                observers.Add(streamId, disposable);
            }
            catch (Exception exception)
            {
                await receiver.OnError(streamId, exception);
            }
        }

        public Task Unsubscribe(Guid streamId)
        {
            IAsyncDisposable disposable;
            if (observers.TryGetValue(streamId, out disposable))
            {
                observers.Remove(streamId);
            }

            return disposable?.Dispose() ?? Task.FromResult(0);
        }
    }
}
