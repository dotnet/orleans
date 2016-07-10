using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams.AdHoc
{
    /// <summary>
    /// Represents a resource which can be disposed of asynchronously.
    /// </summary>
    public interface IAsyncDisposable
    {
        Task Dispose();
    }

    /// <summary>
    /// Represents an untyped observer.
    /// </summary>
    internal interface IUntypedGrainObserver : IAddressable
    {
        Task OnNextAsync(Guid streamId, object value, StreamSequenceToken token);

        Task OnErrorAsync(Guid streamId, Exception exception);

        Task OnCompletedAsync(Guid streamId);
    }

    /// <summary>
    /// Methods for managing grain extensions for the current activation.
    /// </summary>
    internal interface IGrainExtensionManager
    {
        bool TryGetExtensionHandler<TExtension>(out TExtension result) where TExtension : IGrainExtension;

        bool TryAddExtension(IGrainExtension handler, Type extensionType);

        void RemoveExtension(IGrainExtension handler);
    }

    /// <summary>
    /// The <see cref="IObserverGrainExtension"/> manager, which allows grains to subscribe to observables.
    /// </summary>
    internal interface IObserverGrainExtensionManager
    {
        IObserverGrainExtension GetOrAddExtension();
    }

    /// <summary>
    /// The remote interface for grains which are observable.
    /// </summary>
    internal interface IObservableGrainExtension : IGrainExtension
    {
        [AlwaysInterleave]
        Task SubscribeClient(
            Guid streamId,
            InvokeMethodRequest request,
            IUntypedGrainObserver receiver,
            StreamSequenceToken token);

        [AlwaysInterleave]
        Task SubscribeGrain(
            Guid streamId,
            InvokeMethodRequest request,
            GrainReference receiver,
            StreamSequenceToken token);

        [AlwaysInterleave]
        Task Unsubscribe(Guid streamId);
    }

    /// <summary>
    /// The remote interface for <see cref="IObserverGrainExtension"/>.
    /// </summary>
    internal interface IObserverGrainExtensionRemote : IGrainExtension
    {
        [AlwaysInterleave]
        Task OnNext(Guid streamId, object value, StreamSequenceToken token);

        [AlwaysInterleave]
        Task OnError(Guid streamId, Exception exception);

        [AlwaysInterleave]
        Task OnCompleted(Guid streamId);
    }

    /// <summary>
    /// The interface for the extension which allows grains to subscribe to observables.
    /// </summary>
    internal interface IObserverGrainExtension : IObserverGrainExtensionLocal, IObserverGrainExtensionRemote
    {
    }

    /// <summary>
    /// Defines the local interface for <see cref="IObserverGrainExtensionRemote"/>.
    /// </summary>
    internal interface IObserverGrainExtensionLocal
    {
        void Register(Guid streamId, IUntypedGrainObserver observer);
    }

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

        internal Task<StreamSubscriptionHandle<T>> ResumeAsync(
            Guid streamId,
            IAsyncObserver<T> observer,
            StreamSequenceToken token = null)
        {
            return this.SubscribeInternal(streamId, observer, token);
        }

#warning Call OnError if the silo goes down.
        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return this.SubscribeInternal(Guid.NewGuid(), observer, token: null);
        }

        private async Task<StreamSubscriptionHandle<T>> SubscribeInternal(
            Guid streamId,
            IAsyncObserver<T> observer,
            StreamSequenceToken token)
        {
            var activation = RuntimeClient.Current.CurrentActivationData;
            object observerReference;
            if (activation == null)
            {
                // The caller is a client, so create an object reference.
                var adapter = new TypedToUntypedObserverAdapter<T>(observer);
                var grainFactory = RuntimeClient.Current.InternalGrainFactory;
                var clientObjectReference = await grainFactory.CreateObjectReference<IUntypedGrainObserver>(adapter);
                await
                    this.GrainExtension.SubscribeClient(
                        streamId,
                        this.subscriptionRequest,
                        clientObjectReference,
                        token);
                observerReference = adapter;
            }
            else
            {
                // The caller is a grain, so get or install the observer extension.
                var caller = activation.GrainInstance;
                var grainExtensionManager =
                    caller?.Runtime?.ServiceProvider?.GetService(typeof(IObserverGrainExtensionManager)) as
                    IObserverGrainExtensionManager;
                if (caller == null || grainExtensionManager == null)
                {
#warning throw?
                    throw new Exception("MUST REPLACE THIS");
                }

                // Wrap the observer and register it with the observer extension.
                var adapter = new TypedToUntypedObserverAdapter<T>(observer);
                var callerGrainExtension = grainExtensionManager.GetOrAddExtension();
                callerGrainExtension.Register(streamId, adapter);

                // Subscribe the calling grain to the remote observable.
                await
                    this.GrainExtension.SubscribeGrain(
                        streamId,
                        this.subscriptionRequest,
                        activation.GrainReference,
                        token);
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

    internal class TypedToUntypedObserverAdapter<T> : IUntypedGrainObserver
    {
        private readonly IAsyncObserver<T> observer;

        public TypedToUntypedObserverAdapter(IAsyncObserver<T> observer)
        {
            this.observer = observer;
        }

        public Task OnNextAsync(Guid streamId, object value, StreamSequenceToken token)
            => this.observer.OnNextAsync((T)value, token);

        public Task OnErrorAsync(Guid streamId, Exception exception) => this.observer.OnErrorAsync(exception);

        public Task OnCompletedAsync(Guid streamId) => this.observer.OnCompletedAsync();
    }

    internal class GrainObserverExtensionToUntypedObserverAdapter<T> : IAsyncObserver<T>
    {
        private readonly IObserverGrainExtensionRemote observer;

        private readonly Guid streamId;

        public GrainObserverExtensionToUntypedObserverAdapter(IObserverGrainExtensionRemote observer, Guid streamId)
        {
            this.observer = observer;
            this.streamId = streamId;
        }

        public Task OnNextAsync(T value, StreamSequenceToken token) => this.observer.OnNext(this.streamId, value, token);

        public Task OnErrorAsync(Exception exception) => this.observer.OnError(this.streamId, exception);

        public Task OnCompletedAsync() => this.observer.OnCompleted(this.streamId);
    }

    internal class UntypedToTypedObserverAdapter<T> : IAsyncObserver<T>
    {
        private readonly Guid streamId;

        public UntypedToTypedObserverAdapter(Guid streamId, IUntypedGrainObserver receiver)
        {
            this.Receiver = receiver;
            this.streamId = streamId;
        }

        public IUntypedGrainObserver Receiver { get; }

        public Task OnNextAsync(T value, StreamSequenceToken token = null)
            => this.Receiver.OnNextAsync(streamId, value, token);

        public Task OnErrorAsync(Exception exception) => this.Receiver.OnErrorAsync(streamId, exception);

        public Task OnCompletedAsync() => this.Receiver.OnCompletedAsync(streamId);
    }

    internal class TransientStreamIdentity : IStreamIdentity
    {
        public TransientStreamIdentity(Guid id)
        {
            this.Guid = id;
        }

        public Guid Guid { get; }

        public string Namespace => string.Empty;
    }

    [Serializable]
    internal class TransientStreamSubscriptionHandle<T> : StreamSubscriptionHandle<T>, IAsyncDisposable
    {
        private readonly Guid streamId;

        private readonly GrainObservableProxy<T> observable;

        [SuppressMessage("ReSharper", "NotAccessedField.Local",
            Justification = "This field prevents the reference from being garbage collected.")]
        [NonSerialized]
        private readonly object observerReference;

        public TransientStreamSubscriptionHandle(
            Guid streamId,
            GrainObservableProxy<T> observable,
            object observerReference)
        {
            this.streamId = streamId;
            this.observable = observable;
            this.observerReference = observerReference;
        }

        public Task Dispose() => this.UnsubscribeAsync();

        public override IStreamIdentity StreamIdentity => new TransientStreamIdentity(this.streamId);

        public override Guid HandleId => this.streamId;

        public override Task UnsubscribeAsync()
        {
            return this.observable.GrainExtension.Unsubscribe(this.streamId);
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken token = null)
        {
            return this.observable.ResumeAsync(this.streamId, observer, token);
        }

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            return this.HandleId == other.HandleId;
        }
    }

    /// <summary>
    /// Utility class for subscribing to observable streams.
    /// </summary>
    internal static class StreamDelegateHelper
    {
        private delegate Task<object> ClientSubscribeDelegate(
            object observable,
            IUntypedGrainObserver receiver,
            Guid streamId,
            StreamSequenceToken token);

        private delegate Task<object> GrainSubscribeDelegate(
            object observable,
            IObserverGrainExtensionRemote receiver,
            Guid streamId,
            StreamSequenceToken token);

        private delegate Task UnsubscribeDelegate(object handle);

        private static readonly ConcurrentDictionary<Type, ClientSubscribeDelegate> ClientSubscribeDelegates =
            new ConcurrentDictionary<Type, ClientSubscribeDelegate>();

        private static readonly ConcurrentDictionary<Type, GrainSubscribeDelegate> GrainSubscribeDelegates =
            new ConcurrentDictionary<Type, GrainSubscribeDelegate>();

        private static readonly ConcurrentDictionary<Type, UnsubscribeDelegate> UnsubscribeDelegates =
            new ConcurrentDictionary<Type, UnsubscribeDelegate>();

        private static readonly MethodInfo ClientSubscribeMethodInfo;

        private static readonly MethodInfo GrainSubscribeMethodInfo;

        private static readonly MethodInfo UnsubscribeMethodInfo;

        static StreamDelegateHelper()
        {
            ClientSubscribeMethodInfo = typeof(StreamDelegateHelper).GetMethod(
                nameof(ClientSubscribe),
                BindingFlags.Static | BindingFlags.NonPublic);
            GrainSubscribeMethodInfo = typeof(StreamDelegateHelper).GetMethod(
                nameof(GrainSubscribe),
                BindingFlags.Static | BindingFlags.NonPublic);
            UnsubscribeMethodInfo = typeof(StreamDelegateHelper).GetMethod(
                nameof(UnsubscribeInternal),
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static Task<StreamSubscriptionHandle<TElement>> ClientSubscribe<TElement, TObservable>(
            TObservable observable,
            IUntypedGrainObserver receiver,
            Guid streamId,
            StreamSequenceToken token) where TObservable : IAsyncObservable<TElement>
        {
            return observable.SubscribeAsync(new UntypedToTypedObserverAdapter<TElement>(streamId, receiver), token);
        }

        private static Task<StreamSubscriptionHandle<TElement>> GrainSubscribe<TElement, TObservable>(
            TObservable observable,
            IObserverGrainExtensionRemote receiver,
            Guid streamId,
            StreamSequenceToken token) where TObservable : IAsyncObservable<TElement>
        {
            return
                observable.SubscribeAsync(new GrainObserverExtensionToUntypedObserverAdapter<TElement>(receiver, streamId), token);
        }

        private static Task UnsubscribeInternal<TElement, THandle>(THandle observable) where THandle : StreamSubscriptionHandle<TElement>
        {
            return observable.UnsubscribeAsync();
        }

        public static Task<object> Subscribe(
            object observable,
            IUntypedGrainObserver receiver,
            Guid streamId,
            StreamSequenceToken token)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            var type = observable.GetType();
            if (!type.IsConstructedGenericType
                || typeof(IAsyncObservable<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
            {
                throw new ArgumentException($"Type {type} must be of type {typeof(IAsyncObservable<>)}");
            }

            ClientSubscribeDelegate subscribeDelegate;
            if (!ClientSubscribeDelegates.TryGetValue(type, out subscribeDelegate))
            {
                subscribeDelegate = ClientSubscribeDelegates.GetOrAdd(type, CreateClientSubscribeDelegate);
            }
            
            return subscribeDelegate(observable, receiver, streamId, token);
        }

        public static Task<object> Subscribe(
            object observable,
            IObserverGrainExtensionRemote receiver,
            Guid streamId,
            StreamSequenceToken token)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            var type = observable.GetType();
            if (!type.IsConstructedGenericType
                || typeof(IAsyncObservable<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
            {
                throw new ArgumentException($"Type {type} must be of type {typeof(IAsyncObservable<>)}");
            }

            GrainSubscribeDelegate subscribeDelegate;
            if (!GrainSubscribeDelegates.TryGetValue(type, out subscribeDelegate))
            {
                subscribeDelegate = GrainSubscribeDelegates.GetOrAdd(type, CreateGrainSubscribeDelegate);
            }

            return subscribeDelegate(observable, receiver, streamId, token);
        }

        public static Task Unsubscribe(object handle)
        {
            if (handle == null) throw new ArgumentNullException(nameof(handle));
            var type = handle.GetType();
            if (!type.IsConstructedGenericType
                || typeof(StreamSubscriptionHandle<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
            {
                throw new ArgumentException($"Type {type} must be of type {typeof(StreamSubscriptionHandle<>)}");
            }

            UnsubscribeDelegate unsubscribeDelegate;
            if (!UnsubscribeDelegates.TryGetValue(type, out unsubscribeDelegate))
            {
                unsubscribeDelegate = UnsubscribeDelegates.GetOrAdd(type, CreateUnsubscribeDelegate);
            }

            return unsubscribeDelegate(handle);
        }

        private static ClientSubscribeDelegate CreateClientSubscribeDelegate(Type observableType)
        {
            return
                (ClientSubscribeDelegate)
                CreateSubscribeDelegate<ClientSubscribeDelegate>(
                    observableType,
                    typeof(IUntypedGrainObserver),
                    ClientSubscribeMethodInfo);
        }

        private static GrainSubscribeDelegate CreateGrainSubscribeDelegate(Type observableType)
        {
            return
                (GrainSubscribeDelegate)
                CreateSubscribeDelegate<GrainSubscribeDelegate>(
                    observableType,
                    typeof(IObserverGrainExtensionRemote),
                    GrainSubscribeMethodInfo);
        }

        private static Delegate CreateSubscribeDelegate<TDelegate>(
            Type observableType,
            Type observerType,
            MethodInfo subscribeMethodInfo)
        {
            // Create a method to hold the generated IL.
            var method = new DynamicMethod(
                observableType.Name + observerType.Name,
                typeof(Task<object>),
                new[] { typeof(object), observerType, typeof(Guid), typeof(StreamSequenceToken) },
                observableType.GetTypeInfo().Module,
                true);

            // Construct the method which this IL will call.
            var genericMethod =
                subscribeMethodInfo.MakeGenericMethod(
                    observableType.GetTypeInfo().GetGenericArguments()[0],
                    observableType);

            // Emit IL which calls the constructed method.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldarg_2);
            emitter.Emit(OpCodes.Ldarg_3);
            emitter.Emit(OpCodes.Call, genericMethod);
            emitter.Emit(OpCodes.Ret);

            return method.CreateDelegate(typeof(TDelegate));
        }

        private static UnsubscribeDelegate CreateUnsubscribeDelegate(Type handleType)
        {
            // Create a method to hold the generated IL.
            var method = new DynamicMethod(
                handleType.Name + "Unsubscribe",
                typeof(Task),
                new[] { typeof(object)},
                handleType.GetTypeInfo().Module,
                true);

            // Construct the method which this IL will call.
            var genericMethod =
                UnsubscribeMethodInfo.MakeGenericMethod(
                    handleType.GetTypeInfo().GetGenericArguments()[0],
                    handleType);

            // Emit IL which calls the constructed method.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Call, genericMethod);
            emitter.Emit(OpCodes.Ret);

            return (UnsubscribeDelegate)method.CreateDelegate(typeof(UnsubscribeDelegate));
        }
    }
}
