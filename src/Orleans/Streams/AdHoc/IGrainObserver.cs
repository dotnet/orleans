using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Streams.AdHoc
{
    /// <summary>
    /// Represents an asynchronous observer.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public interface IGrainObserver<in T>
    {
        Task OnNext(T value);
        Task OnError(Exception exception);
        Task OnCompleted();
    }

    /// <summary>
    /// Represents an asynchronous observable stream of values.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public interface IGrainObservable<out T>
    {
        Task<IAsyncDisposable> Subscribe(IGrainObserver<T> observer);
    }
    
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
        Task OnNext(object value);
        
        Task OnError(Exception exception);
        
        Task OnCompleted();
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
        Task SubscribeClient(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver receiver);
        Task SubscribeGrain(Guid streamId, InvokeMethodRequest request, GrainReference receiver);
        Task Unsubscribe(Guid streamId);
    }

    /// <summary>
    /// The remote interface for <see cref="IObserverGrainExtension"/>.
    /// </summary>
    internal interface IObserverGrainExtensionRemote : IGrainExtension
    {
        Task OnNext(Guid streamId, object value);

        Task OnError(Guid streamId, Exception exception);

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
    internal class GrainObservableProxy<T> : IGrainObservable<T>
    {
        private readonly InvokeMethodRequest subscriptionRequest;
        private readonly GrainReference grain;

        [NonSerialized] private IObservableGrainExtension grainExtension;

        public GrainObservableProxy(GrainReference grain, InvokeMethodRequest subscriptionRequest)
        {
            this.grain = grain;
            this.subscriptionRequest = subscriptionRequest;
        }

#warning Call OnError if the silo goes down.
        public async Task<IAsyncDisposable> Subscribe(IGrainObserver<T> observer)
        {
            if (grainExtension == null)
            {
                grainExtension = grain.AsReference<IObservableGrainExtension>();
            }

            var streamId = Guid.NewGuid();

            var activation = RuntimeClient.Current.CurrentActivationData;
            object observerReference;
            if (activation == null)
            {
                // The caller is a client, so create an object reference.
                var adapter = new TypedToUntypedObserverAdapter<T>(observer);
                var grainFactory = RuntimeClient.Current.InternalGrainFactory;
                var clientObjectReference = await grainFactory.CreateObjectReference<IUntypedGrainObserver>(adapter);
                await grainExtension.SubscribeClient(streamId, subscriptionRequest, clientObjectReference);
                observerReference = adapter;
            }
            else
            {
                // The caller is a grain, so get or install the observer extension.
                var caller = activation.GrainInstance;
                var grainExtensionManager =
                    caller?.Runtime?.ServiceProvider?.GetService(typeof(IObserverGrainExtensionManager))
                        as IObserverGrainExtensionManager;
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
                await grainExtension.SubscribeGrain(streamId, subscriptionRequest, activation.GrainReference);
                observerReference = adapter;
            }

            // Return a disposable which will forward Dispose calls to the remote observable endpoint.
            return new AsyncDisposableProxy(streamId, grainExtension, observerReference);

        }
    }

    internal class TypedToUntypedObserverAdapter<T> : IUntypedGrainObserver
    {
        private readonly IGrainObserver<T> observer;

        public TypedToUntypedObserverAdapter(IGrainObserver<T> observer)
        {
            this.observer = observer;
        }

        public Task OnNext(object value) => observer.OnNext((T)value);

        public Task OnError(Exception exception) => observer.OnError(exception);

        public Task OnCompleted() => observer.OnCompleted();
    }

    internal class GrainObserverExtensionToUntypedObserverAdapter<T> : IGrainObserver<T>
    {
        private readonly IObserverGrainExtensionRemote observer;
        private readonly Guid streamId;

        public GrainObserverExtensionToUntypedObserverAdapter(IObserverGrainExtensionRemote observer, Guid streamId)
        {
            this.observer = observer;
            this.streamId = streamId;
        }

        public Task OnNext(T value) => observer.OnNext(streamId, value);

        public Task OnError(Exception exception) => observer.OnError(streamId, exception);

        public Task OnCompleted() => observer.OnCompleted(streamId);
    }

    internal class UntypedToTypedObserverAdapter<T> : IGrainObserver<T>
    {
        public UntypedToTypedObserverAdapter(IUntypedGrainObserver receiver)
        {
            this.Receiver = receiver;
        }

        public IUntypedGrainObserver Receiver { get; }

        public Task OnNext(T value) => Receiver.OnNext(value);

        public Task OnError(Exception exception) => Receiver.OnError(exception);

        public Task OnCompleted() => Receiver.OnCompleted();
    }

    [Serializable]
    internal class AsyncDisposableProxy : IAsyncDisposable
    {
        private readonly Guid streamId;
        private readonly IAddressable grain;

        [SuppressMessage(
            "ReSharper",
            "NotAccessedField.Local",
            Justification = "This field prevents the reference from being garbage collected.")]
        [NonSerialized]
        private readonly object observerReference;

        public AsyncDisposableProxy(Guid streamId, IAddressable grain, object observerReference)
        {
            this.streamId = streamId;
            this.grain = grain;
            this.observerReference = observerReference;
        }

        public Task Dispose() => grain.AsReference<IObservableGrainExtension>().Unsubscribe(streamId);
    }

    /// <summary>
    /// Utility class for subscribing to observable streams.
    /// </summary>
    internal static class ObservableSubscriberHelper
    {
        private delegate Task<IAsyncDisposable> TypedClientSubscribeDelegate(object observable, IUntypedGrainObserver receiver);
        private delegate Task<IAsyncDisposable> TypedGrainSubscribeDelegate(object observable, IObserverGrainExtensionRemote receiver, Guid streamId);

        private static readonly ConcurrentDictionary<Type, TypedClientSubscribeDelegate> ClientSubscribeDelegates =
            new ConcurrentDictionary<Type, TypedClientSubscribeDelegate>();
        private static readonly ConcurrentDictionary<Type, TypedGrainSubscribeDelegate> GrainSubscribeDelegates =
            new ConcurrentDictionary<Type, TypedGrainSubscribeDelegate>();

        private static readonly MethodInfo ClientSubscribeMethodInfo;
        private static readonly MethodInfo GrainSubscribeMethodInfo;
        static ObservableSubscriberHelper()
        {
            ClientSubscribeMethodInfo = typeof(ObservableSubscriberHelper).GetMethod(nameof(ClientSubscribe),
                BindingFlags.Static | BindingFlags.NonPublic);
            GrainSubscribeMethodInfo = typeof(ObservableSubscriberHelper).GetMethod(nameof(GrainSubscribe),
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static Task<IAsyncDisposable> ClientSubscribe<TElement, TObservable>(TObservable observable,
            IUntypedGrainObserver receiver) where TObservable : IGrainObservable<TElement>
        {
            return observable.Subscribe(new UntypedToTypedObserverAdapter<TElement>(receiver));
        }

        private static Task<IAsyncDisposable> GrainSubscribe<TElement, TObservable>(
            TObservable observable,
            IObserverGrainExtensionRemote receiver,
            Guid streamId) where TObservable : IGrainObservable<TElement>
        {
            return observable.Subscribe(new GrainObserverExtensionToUntypedObserverAdapter<TElement>(receiver, streamId));
        }

        public static Task<IAsyncDisposable> Subscribe(object observable, IUntypedGrainObserver receiver)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            var type = observable.GetType();
            if (!type.IsConstructedGenericType || typeof(IGrainObservable<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
            {
                throw new ArgumentException($"Type {type} must be of type {typeof(IGrainObservable<>)}");
            }

            TypedClientSubscribeDelegate subscribeDelegate;
            if (!ClientSubscribeDelegates.TryGetValue(type, out subscribeDelegate))
            {
                subscribeDelegate = ClientSubscribeDelegates.GetOrAdd(type, CreateTypedClientSubscribeDelegate);
            }

            return subscribeDelegate(observable, receiver);
        }

        public static Task<IAsyncDisposable> Subscribe(object observable, IObserverGrainExtensionRemote receiver, Guid streamId)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            var type = observable.GetType();
            if (!type.IsConstructedGenericType || typeof(IGrainObservable<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
            {
                throw new ArgumentException($"Type {type} must be of type {typeof(IGrainObservable<>)}");
            }

            TypedGrainSubscribeDelegate subscribeDelegate;
            if (!GrainSubscribeDelegates.TryGetValue(type, out subscribeDelegate))
            {
                subscribeDelegate = GrainSubscribeDelegates.GetOrAdd(type, CreateTypedGrainSubscribeDelegate);
            }

            return subscribeDelegate(observable, receiver, streamId);
        }

        private static TypedClientSubscribeDelegate CreateTypedClientSubscribeDelegate(Type observableType)
        {
            // Create a method to hold the generated IL.
            var method = new DynamicMethod(
                observableType.Name + "ClientSubscriber",
                typeof(Task<IAsyncDisposable>),
                new[] { typeof(object), typeof(IUntypedGrainObserver) },
                observableType.GetTypeInfo().Module,
                true);

            // Construct the method which this IL will call.
            var genericMethod = ClientSubscribeMethodInfo.MakeGenericMethod(
                observableType.GetTypeInfo().GetGenericArguments()[0],
                observableType);

            // Emit IL which calls the constructed method.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Call, genericMethod);
            emitter.Emit(OpCodes.Ret);

            return (TypedClientSubscribeDelegate)method.CreateDelegate(typeof(TypedClientSubscribeDelegate));
        }

        private static TypedGrainSubscribeDelegate CreateTypedGrainSubscribeDelegate(Type observableType)
        {
            // Create a method to hold the generated IL.
            var method = new DynamicMethod(
                observableType.Name + "GrainSubscriber",
                typeof(Task<IAsyncDisposable>),
                new[] { typeof(object), typeof(IObserverGrainExtensionRemote), typeof(Guid)},
                observableType.GetTypeInfo().Module,
                true);

            // Construct the method which this IL will call.
            var genericMethod = GrainSubscribeMethodInfo.MakeGenericMethod(
                observableType.GetTypeInfo().GetGenericArguments()[0],
                observableType);

            // Emit IL which calls the constructed method.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldarg_2);
            emitter.Emit(OpCodes.Call, genericMethod);
            emitter.Emit(OpCodes.Ret);

            return (TypedGrainSubscribeDelegate)method.CreateDelegate(typeof(TypedGrainSubscribeDelegate));
        }
    }

}
