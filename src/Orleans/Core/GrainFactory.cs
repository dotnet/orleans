using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;

namespace Orleans
{
    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    public class GrainFactory : IGrainFactory
    {
        /// <summary>
        /// The cached <see cref="MethodInfo"/> for <see cref="GrainReference.CastInternal"/>.
        /// </summary>
        private static readonly MethodInfo GrainReferenceCastInternalMethodInfo =
            TypeUtils.Method(() => GrainReference.CastInternal(default(Type), null, default(IAddressable), 0));

        /// <summary>
        /// The mapping between grain types and the corresponding type for the <see cref="IGrainMethodInvoker"/> implementation.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Type> GrainToInvokerMapping
            = new ConcurrentDictionary<Type, Type>();

        /// <summary>
        /// The mapping between grain types and the corresponding type for the <see cref="GrainReference"/> implementation.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Type> GrainToReferenceMapping
            = new ConcurrentDictionary<Type, Type>();

        /// <summary>
        /// The mapping between concrete grain interface types and delegate
        /// </summary>
        private readonly ConcurrentDictionary<Type, GrainReferenceCaster> casters
            = new ConcurrentDictionary<Type, GrainReferenceCaster>();

        /// <summary>
        /// The collection of <see cref="IGrainMethodInvoker"/>s for their corresponding grain interface type.
        /// </summary>
        private readonly ConcurrentDictionary<Type, IGrainMethodInvoker> invokers =
            new ConcurrentDictionary<Type, IGrainMethodInvoker>();

        // Make this internal so that client code is forced to access the IGrainFactory using the 
        // GrainClient (to make sure they don't forget to initialize the client).
        internal GrainFactory()
        {
        }

        /// <summary>
        /// Casts an <see cref="IAddressable"/> to a concrete <see cref="GrainReference"/> implementaion.
        /// </summary>
        /// <param name="existingReference">The existing <see cref="IAddressable"/> reference.</param>
        /// <returns>The concrete <see cref="GrainReference"/> implementation.</returns>
        private delegate object GrainReferenceCaster(IAddressable existingReference);

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey
        {
            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey
        {
            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extention of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface), keyExtension),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extention of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface), keyExtension),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Creates a reference to the provided <paramref name="obj"/>.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The object to create a reference to.</param>
        /// <returns>The reference to <paramref name="obj"/>.</returns>
        public Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
        {
            return Task.FromResult(CreateObjectReferenceImpl<TGrainObserverInterface>(obj));
        }

        internal TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
                where TGrainObserverInterface : IAddressable
        {
            return CreateObjectReferenceImpl<TGrainObserverInterface>(obj);
        }

        private TGrainObserverInterface CreateObjectReferenceImpl<TGrainObserverInterface>(IAddressable obj)
        {
            var interfaceType = typeof(TGrainObserverInterface);
            var interfaceTypeInfo = interfaceType.GetTypeInfo();
            if (!interfaceTypeInfo.IsInterface)
            {
                throw new ArgumentException(
                    string.Format(
                        "The provided type parameter must be an interface. '{0}' is not an interface.",
                        interfaceTypeInfo.FullName));
            }

            if (!interfaceTypeInfo.IsInstanceOfType(obj))
            {
                throw new ArgumentException(
                    string.Format("The provided object must implement '{0}'.", interfaceTypeInfo.FullName),
                    "obj");
            }
            
            IGrainMethodInvoker invoker;
            if (!this.invokers.TryGetValue(interfaceType, out invoker))
            {
                invoker = MakeInvoker(interfaceType);
                if (invoker != null)
                {
                    this.invokers.TryAdd(interfaceType, invoker);
                }
            }

            if (invoker == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Cannot find generated IMethodInvoker implementation for interface '{0}'",
                        interfaceType));
            }

            return Cast<TGrainObserverInterface>(RuntimeClient.Current.CreateObjectReference(obj, invoker));
        }

        /// <summary>
        /// Deletes the provided object reference.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The reference being deleted.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task DeleteObjectReference<TGrainObserverInterface>(
            IGrainObserver obj) where TGrainObserverInterface : IGrainObserver
        {
            RuntimeClient.Current.DeleteObjectReference(obj);
            return TaskDone.Done;
        }

        private static IGrainMethodInvoker MakeInvoker(Type interfaceType)
        {
            var typeInfo = interfaceType.GetTypeInfo(); 
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(typeInfo.Assembly);
            var genericInterfaceType = interfaceType.IsConstructedGenericType
                                           ? typeInfo.GetGenericTypeDefinition()
                                           : interfaceType;

            // Try to find the correct IGrainMethodInvoker type for this interface.
            Type invokerType;
            if (!GrainToInvokerMapping.TryGetValue(genericInterfaceType, out invokerType))
            {
                return null;
            }

            if (interfaceType.IsConstructedGenericType)
            {
                invokerType = invokerType.MakeGenericType(typeInfo.GenericTypeArguments);
            }

            return (IGrainMethodInvoker)Activator.CreateInstance(invokerType);
        }

        #region Interface Casting
        internal TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceType = typeof(TGrainInterface);
            return (TGrainInterface)this.Cast(grain, interfaceType);
        }

        internal object Cast(IAddressable grain, Type interfaceType)
        {
            GrainReferenceCaster caster;
            if (!this.casters.TryGetValue(interfaceType, out caster))
            {
                // Create and cache a caster for the interface type.
                caster = this.casters.GetOrAdd(interfaceType, MakeCaster);
            }

            return caster(grain);
        }

        private static GrainReferenceCaster MakeCaster(Type interfaceType)
        {
            var typeInfo = interfaceType.GetTypeInfo();
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(typeInfo.Assembly);
            var genericInterfaceType = interfaceType.IsConstructedGenericType
                                           ? typeInfo.GetGenericTypeDefinition()
                                           : interfaceType;

            // Try to find the correct GrainReference type for this interface.
            Type grainReferenceType;
            if (!GrainToReferenceMapping.TryGetValue(genericInterfaceType, out grainReferenceType))
            {
                throw new InvalidOperationException(
                    string.Format("Cannot find generated GrainReference class for interface '{0}'", interfaceType));
            }

            if (interfaceType.IsConstructedGenericType)
            {
                grainReferenceType = grainReferenceType.MakeGenericType(typeInfo.GenericTypeArguments);
            }

            // Get the grain reference constructor.
            var constructor =
                grainReferenceType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                    .Where(
                        _ =>
                        {
                            var parameters = _.GetParameters();
                            return parameters.Length == 1 && parameters[0].ParameterType == typeof(GrainReference);
                        }).FirstOrDefault();

            if (constructor == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Cannot find suitable constructor on generated reference type for interface '{0}'",
                        interfaceType));
            }

            // Construct an expression to construct a new instance of this grain reference when given another grain
            // reference.
            var createLambdaParameter = Expression.Parameter(typeof(GrainReference), "gr");
            var createLambda =
                Expression.Lambda<Func<GrainReference, IAddressable>>(
                    Expression.New(constructor, createLambdaParameter),
                    createLambdaParameter);
            var grainRefParameter = Expression.Parameter(typeof(IAddressable), "grainRef");
            var body =
                Expression.Call(
                    GrainReferenceCastInternalMethodInfo,
                    Expression.Constant(interfaceType),
                    createLambda,
                    grainRefParameter,
                    Expression.Constant(GrainInterfaceUtils.GetGrainInterfaceId(interfaceType)));

            // Compile and return the reference casting lambda.
            var lambda = Expression.Lambda<GrainReferenceCaster>(body, grainRefParameter);
            return lambda.Compile();
        }
        #endregion

        #region SystemTargets

        private readonly Dictionary<Tuple<GrainId,Type>, Dictionary<SiloAddress, ISystemTarget>> typedSystemTargetReferenceCache =
                    new Dictionary<Tuple<GrainId, Type>, Dictionary<SiloAddress, ISystemTarget>>();
        
        internal TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            Dictionary<SiloAddress, ISystemTarget> cache;
            Tuple<GrainId, Type> key = Tuple.Create(grainId, typeof(TGrainInterface));

            lock (typedSystemTargetReferenceCache)
            {
                if (typedSystemTargetReferenceCache.ContainsKey(key))
                    cache = typedSystemTargetReferenceCache[key];
                else
                {
                    cache = new Dictionary<SiloAddress, ISystemTarget>();
                    typedSystemTargetReferenceCache[key] = cache;
                }
            }

            ISystemTarget reference;
            lock (cache)
            {
                if (cache.ContainsKey(destination))
                {
                    reference = cache[destination];
                }
                else
                {
                    reference = Cast<TGrainInterface>(GrainReference.FromGrainId(grainId, null, destination));
                    cache[destination] = reference; // Store for next time
                }
            }
            return (TGrainInterface) reference;
        }

        #endregion

        #region Utility functions

        internal static void FindSupportClasses(Type type)
        {
            var typeInfo = type.GetTypeInfo();
            var invokerAttr = typeInfo.GetCustomAttribute<MethodInvokerAttribute>(false);
            if (invokerAttr != null)
            {
                GrainToInvokerMapping.TryAdd(invokerAttr.GrainType, type);
            }
            
            var grainReferenceAttr = typeInfo.GetCustomAttribute<GrainReferenceAttribute>(false);
            if (grainReferenceAttr != null)
            {
                GrainToReferenceMapping.TryAdd(grainReferenceAttr.GrainType, type);
            }
        }

        #endregion
    }
}
