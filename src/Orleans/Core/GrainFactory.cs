using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    internal class GrainFactory : IInternalGrainFactory
    {
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

        /// <summary>
        /// The cache of typed system target references.
        /// </summary>
        private readonly Dictionary<Tuple<GrainId, Type>, Dictionary<SiloAddress, ISystemTarget>> typedSystemTargetReferenceCache =
                    new Dictionary<Tuple<GrainId, Type>, Dictionary<SiloAddress, ISystemTarget>>();

        /// <summary>
        /// The cache of type metadata.
        /// </summary>
        private readonly TypeMetadataCache typeCache;

        /// <summary>
        /// The runtime client.
        /// </summary>
        private readonly IRuntimeClient runtimeClient;

        // Make this internal so that client code is forced to access the IGrainFactory using the 
        // GrainClient (to make sure they don't forget to initialize the client).
        public GrainFactory(IRuntimeClient runtimeClient, TypeMetadataCache typeCache)
        {
            this.runtimeClient = runtimeClient;
            this.typeCache = typeCache;
        }

        /// <summary>
        /// Casts an <see cref="IAddressable"/> to a concrete <see cref="GrainReference"/> implementaion.
        /// </summary>
        /// <param name="existingReference">The existing <see cref="IAddressable"/> reference.</param>
        /// <returns>The concrete <see cref="GrainReference"/> implementation.</returns>
        internal delegate object GrainReferenceCaster(IAddressable existingReference);

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            Type interfaceType = typeof(TGrainInterface);
            var implementation = this.GetGrainClassData(interfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey, null);
            return this.Cast<TGrainInterface>(this.MakeGrainReferenceFromType(interfaceType, grainId));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey
        {
            Type interfaceType = typeof(TGrainInterface);
            var implementation = this.GetGrainClassData(interfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey, null);
            return this.Cast<TGrainInterface>(this.MakeGrainReferenceFromType(interfaceType, grainId));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            Type interfaceType = typeof(TGrainInterface);
            var implementation = this.GetGrainClassData(interfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey);
            return this.Cast<TGrainInterface>(this.MakeGrainReferenceFromType(interfaceType, grainId));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extention of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            Type interfaceType = typeof(TGrainInterface);
            var implementation = this.GetGrainClassData(interfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey, keyExtension);
            return this.Cast<TGrainInterface>(this.MakeGrainReferenceFromType(interfaceType, grainId));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extention of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            Type interfaceType = typeof(TGrainInterface);
            var implementation = this.GetGrainClassData(interfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey, keyExtension);
            return this.Cast<TGrainInterface>(this.MakeGrainReferenceFromType(interfaceType, grainId));
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
            return Task.FromResult(this.CreateObjectReferenceImpl<TGrainObserverInterface>(obj));
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
            this.runtimeClient.DeleteObjectReference(obj);
            return TaskDone.Done;
        }

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
                where TGrainObserverInterface : IAddressable
        {
            return this.CreateObjectReferenceImpl<TGrainObserverInterface>(obj);
        }

        private TGrainObserverInterface CreateObjectReferenceImpl<TGrainObserverInterface>(IAddressable obj) where TGrainObserverInterface : IAddressable
        {
            var interfaceType = typeof(TGrainObserverInterface);
            var interfaceTypeInfo = interfaceType.GetTypeInfo();
            if (!interfaceTypeInfo.IsInterface)
            {
                throw new ArgumentException(
                    $"The provided type parameter must be an interface. '{interfaceTypeInfo.FullName}' is not an interface.");
            }

            if (!interfaceTypeInfo.IsInstanceOfType(obj))
            {
                throw new ArgumentException($"The provided object must implement '{interfaceTypeInfo.FullName}'.", nameof(obj));
            }

            IGrainMethodInvoker invoker;
            if (!this.invokers.TryGetValue(interfaceType, out invoker))
            {
                invoker = this.MakeInvoker(interfaceType);
                this.invokers.TryAdd(interfaceType, invoker);
            }

            return this.Cast<TGrainObserverInterface>(this.runtimeClient.CreateObjectReference(obj, invoker));
        }

        private IAddressable MakeGrainReferenceFromType(Type interfaceType, GrainId grainId)
        {
            var typeInfo = interfaceType.GetTypeInfo();
            return GrainReference.FromGrainId(
                grainId,
                typeInfo.IsGenericType ? TypeUtils.GenericTypeArgsString(typeInfo.UnderlyingSystemType.FullName) : null);
        }

        private GrainClassData GetGrainClassData(Type interfaceType, string grainClassNamePrefix)
        {
            if (!GrainInterfaceUtils.IsGrainType(interfaceType))
            {
                throw new ArgumentException("Cannot fabricate grain-reference for non-grain type: " + interfaceType.FullName);
            }

            var grainTypeResolver = this.runtimeClient.GrainTypeResolver;
            GrainClassData implementation;
            if (!grainTypeResolver.TryGetGrainClassData(interfaceType, out implementation, grainClassNamePrefix))
            {
                var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
                var assembliesString = string.IsNullOrEmpty(loadedAssemblies)
                    ? string.Empty
                    : " Loaded grain assemblies: " + loadedAssemblies;
                var grainClassPrefixString = string.IsNullOrEmpty(grainClassNamePrefix)
                    ? string.Empty
                    : ", grainClassNamePrefix: " + grainClassNamePrefix;
                throw new ArgumentException(
                    $"Cannot find an implementation class for grain interface: {interfaceType}{grainClassPrefixString}. " +
                    "Make sure the grain assembly was correctly deployed and loaded in the silo." + assembliesString);
            }

            return implementation;
        }

        private IGrainMethodInvoker MakeInvoker(Type interfaceType)
        {
            var invokerType = this.typeCache.GetGrainMethodInvokerType(interfaceType);
            return (IGrainMethodInvoker)Activator.CreateInstance(invokerType);
        }

        #region Interface Casting

        /// <summary>
        /// Casts the provided <paramref name="grain"/> to the specified interface
        /// </summary>
        /// <typeparam name="TGrainInterface">The target grain interface type.</typeparam>
        /// <param name="grain">The grain reference being cast.</param>
        /// <returns>
        /// A reference to <paramref name="grain"/> which implements <typeparamref name="TGrainInterface"/>.
        /// </returns>
        public TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceType = typeof(TGrainInterface);
            return (TGrainInterface)this.Cast(grain, interfaceType);
        }

        /// <summary>
        /// Casts the provided <paramref name="grain"/> to the provided <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="interfaceType">The resulting interface type.</param>
        /// <returns>A reference to <paramref name="grain"/> which implements <paramref name="interfaceType"/>.</returns>
        public object Cast(IAddressable grain, Type interfaceType)
        {
            GrainReferenceCaster caster;
            if (!this.casters.TryGetValue(interfaceType, out caster))
            {
                // Create and cache a caster for the interface type.
                caster = this.casters.GetOrAdd(interfaceType, this.MakeCaster);
            }

            return caster(grain);
        }

        /// <summary>
        /// Creates and returns a new grain reference caster.
        /// </summary>
        /// <param name="interfaceType">The interface which the result will cast to.</param>
        /// <returns>A new grain reference caster.</returns>
        private GrainReferenceCaster MakeCaster(Type interfaceType)
        {
            var grainReferenceType = this.typeCache.GetGrainReferenceType(interfaceType);
            return GrainCasterFactory.CreateGrainReferenceCaster(interfaceType, grainReferenceType);
        }

        #endregion

        #region SystemTargets

        /// <summary>
        /// Gets a reference to the specified system target.
        /// </summary>
        /// <typeparam name="TGrainInterface">The system target interface.</typeparam>
        /// <param name="grainId">The id of the target.</param>
        /// <param name="destination">The destination silo.</param>
        /// <returns>A reference to the specified system target.</returns>
        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            Dictionary<SiloAddress, ISystemTarget> cache;
            Tuple<GrainId, Type> key = Tuple.Create(grainId, typeof(TGrainInterface));

            lock (this.typedSystemTargetReferenceCache)
            {
                if (this.typedSystemTargetReferenceCache.ContainsKey(key)) cache = this.typedSystemTargetReferenceCache[key];
                else
                {
                    cache = new Dictionary<SiloAddress, ISystemTarget>();
                    this.typedSystemTargetReferenceCache[key] = cache;
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
                    reference = this.Cast<TGrainInterface>(GrainReference.FromGrainId(grainId, null, destination));
                    cache[destination] = reference; // Store for next time
                }
            }

            return (TGrainInterface)reference;
        }

        #endregion
    }
}
