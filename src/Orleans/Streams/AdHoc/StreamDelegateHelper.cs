namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Threading.Tasks;

    /// <summary>
    /// Utility class for working with observable streams.
    /// </summary>
    internal static class StreamDelegateHelper
    {
        private delegate Task<object> SubscribeDelegate(
            object observable,
            IUntypedGrainObserver receiver,
            Guid streamId,
            StreamSequenceToken token,
            out IUntypedObserverWrapper wrapper);

        private delegate Task UnsubscribeDelegate(object handle);

        private static readonly ConcurrentDictionary<Type, SubscribeDelegate> SubscribeDelegates = new ConcurrentDictionary<Type, SubscribeDelegate>();
        
        private static readonly ConcurrentDictionary<Type, UnsubscribeDelegate> UnsubscribeDelegates = new ConcurrentDictionary<Type, UnsubscribeDelegate>();

        private static readonly MethodInfo SubscribeMethodInfo;
        
        private static readonly MethodInfo UnsubscribeMethodInfo;

        static StreamDelegateHelper()
        {
            SubscribeMethodInfo = typeof(StreamDelegateHelper).GetMethod(nameof(SubscribeInternal), BindingFlags.Static | BindingFlags.NonPublic);
            UnsubscribeMethodInfo = typeof(StreamDelegateHelper).GetMethod(nameof(UnsubscribeInternal), BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static Task<StreamSubscriptionHandle<TElement>> SubscribeInternal<TElement, TObservable>(
            TObservable observable,
            IUntypedGrainObserver receiver,
            Guid streamId,
            StreamSequenceToken token,
            out IUntypedObserverWrapper wrapper) where TObservable : IAsyncObservable<TElement>
        {
            var typedObserver = new UntypedToTypedObserverAdapter<TElement>(streamId, receiver);
            wrapper = typedObserver;
            return observable.SubscribeAsync(typedObserver, token);
        }
        
        private static Task UnsubscribeInternal<TElement, THandle>(THandle observable) where THandle : StreamSubscriptionHandle<TElement>
        {
            return observable.UnsubscribeAsync();
        }

        public static Task<object> Subscribe(object observable, IUntypedGrainObserver receiver, Guid streamId, StreamSequenceToken token, out IUntypedObserverWrapper wrapper)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            var type = observable.GetType();
            if (!type.IsConstructedGenericType || typeof(IAsyncObservable<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
            {
                throw new ArgumentException($"Type {type} must be of type {typeof(IAsyncObservable<>)}");
            }

            SubscribeDelegate subscribeDelegate;
            if (!SubscribeDelegates.TryGetValue(type, out subscribeDelegate))
            {
                subscribeDelegate = SubscribeDelegates.GetOrAdd(type, CreateSubscribeDelegate);
            }

            return subscribeDelegate(observable, receiver, streamId, token, out wrapper);
        }
        
        public static Task Unsubscribe(object handle)
        {
            if (handle == null) throw new ArgumentNullException(nameof(handle));
            var type = handle.GetType();
            if (!type.IsConstructedGenericType || typeof(StreamSubscriptionHandle<>).IsAssignableFrom(type.GetGenericTypeDefinition()))
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

        private static SubscribeDelegate CreateSubscribeDelegate(Type observableType)
        {
            // Create a method to hold the generated IL.
            var method = new DynamicMethod(observableType.Name + typeof(IUntypedGrainObserver).Name,
                                           typeof(Task<object>),
                                           new[]
                                           {
                                               typeof(object),
                                               typeof(IUntypedGrainObserver),
                                               typeof(Guid),
                                               typeof(StreamSequenceToken),
                                               typeof(IUntypedObserverWrapper).MakeByRefType()
                                           },
                                           observableType.GetTypeInfo().Module,
                                           true);

            // Construct the method which this IL will call.
            // Note that this method will not function correctly for types which implement IAsyncOservable<> for more than one type.
            var elementType =
                observableType.GetTypeInfo()
                              .ImplementedInterfaces.First(_ => _.IsConstructedGenericType && _.GetGenericTypeDefinition() == typeof(IAsyncObservable<>))
                              .GetGenericArguments()[0];
            var genericMethod = SubscribeMethodInfo.MakeGenericMethod(elementType, observableType);

            // Emit IL which calls the constructed method.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldarg_2);
            emitter.Emit(OpCodes.Ldarg_3);
            emitter.Emit(OpCodes.Ldarg_S, (short)0x04);
            emitter.Emit(OpCodes.Call, genericMethod);
            emitter.Emit(OpCodes.Ret);

            return (SubscribeDelegate)method.CreateDelegate(typeof(SubscribeDelegate));
        }

        private static UnsubscribeDelegate CreateUnsubscribeDelegate(Type handleType)
        {
            // Create a method to hold the generated IL.
            var method = new DynamicMethod(handleType.Name + "Unsubscribe", typeof(Task), new[] { typeof(object) }, handleType.GetTypeInfo().Module, true);

            // Construct the method which this IL will call.
            var elementType = GetStreamSubscriptionHandleBaseType(handleType.GetTypeInfo())?.GetGenericArguments()[0];
            var genericMethod = UnsubscribeMethodInfo.MakeGenericMethod(elementType, handleType);

            // Emit IL which calls the constructed method.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Call, genericMethod);
            emitter.Emit(OpCodes.Ret);

            return (UnsubscribeDelegate)method.CreateDelegate(typeof(UnsubscribeDelegate));
        }

        private static Type GetStreamSubscriptionHandleBaseType(TypeInfo type)
        {
            while (true)
            {
                if (type == null) return null;
                if (type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(StreamSubscriptionHandle<>)) return type;
                type = type.BaseType?.GetTypeInfo();
            }
        }
    }
}