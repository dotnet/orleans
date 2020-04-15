using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans
{
    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    internal class GrainFactory : IInternalGrainFactory, IGrainReferenceConverter
    {
        private GrainReferenceRuntime grainReferenceRuntime;

        /// <summary>
        /// The collection of <see cref="IGrainMethodInvoker"/>s for their corresponding grain interface type.
        /// </summary>
        private readonly ConcurrentDictionary<Type, IGrainMethodInvoker> invokers = new ConcurrentDictionary<Type, IGrainMethodInvoker>();

        /// <summary>
        /// The cache of typed system target references.
        /// </summary>
        private readonly Dictionary<Tuple<GrainId, Type>, ISystemTarget> typedSystemTargetReferenceCache = new Dictionary<Tuple<GrainId, Type>, ISystemTarget>();

        /// <summary>
        /// The cache of type metadata.
        /// </summary>
        private readonly TypeMetadataCache typeCache;

        private readonly IRuntimeClient runtimeClient;

        public GrainFactory(
            IRuntimeClient runtimeClient,
            TypeMetadataCache typeCache)
        {
            this.runtimeClient = runtimeClient;
            this.typeCache = typeCache;
        }

        private GrainReferenceRuntime GrainReferenceRuntime => this.grainReferenceRuntime ??= (GrainReferenceRuntime)this.runtimeClient.GrainReferenceRuntime;

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(typeof(TGrainInterface), primaryKey, keyExtension: null, grainClassNamePrefix: grainClassNamePrefix));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(typeof(TGrainInterface), primaryKey, keyExtension: null, grainClassNamePrefix: grainClassNamePrefix));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(typeof(TGrainInterface), primaryKey, grainClassNamePrefix: grainClassNamePrefix));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(typeof(TGrainInterface), primaryKey, keyExtension: keyExtension, grainClassNamePrefix: grainClassNamePrefix));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(typeof(TGrainInterface), primaryKey, keyExtension: keyExtension, grainClassNamePrefix: grainClassNamePrefix));
        }

        /// <inheritdoc />
        public void BindGrainReference(IAddressable grain)
        {
            if (grain == null) throw new ArgumentNullException(nameof(grain));
            var reference = grain as GrainReference;
            if (reference == null) throw new ArgumentException("Provided grain must be a GrainReference.", nameof(grain));
            reference.Bind(this.GrainReferenceRuntime);
        }

        /// <inheritdoc />
        public GrainReference GetGrainFromKeyString(string key) => GrainReference.FromKeyString(key, this.GrainReferenceRuntime);

        /// <inheritdoc />
        public GrainReference GetGrainFromKeyInfo(GrainReferenceKeyInfo keyInfo) => GrainReference.FromKeyInfo(keyInfo, this.GrainReferenceRuntime);

        /// <inheritdoc />
        public Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
        {
            return Task.FromResult(this.CreateObjectReference<TGrainObserverInterface>((IAddressable)obj));
        }

        /// <inheritdoc />
        public Task DeleteObjectReference<TGrainObserverInterface>(
            IGrainObserver obj) where TGrainObserverInterface : IGrainObserver
        {
            this.runtimeClient.DeleteObjectReference(obj);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
                where TGrainObserverInterface : IAddressable
        {
            return (TGrainObserverInterface)this.CreateObjectReference(typeof(TGrainObserverInterface), obj);
        }

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
        public object Cast(IAddressable grain, Type interfaceType) => this.GrainReferenceRuntime.Convert(grain, interfaceType);

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            var grainId = SystemTargetGrainId.Create(grainType, destination);
            return this.GetSystemTarget<TGrainInterface>(grainId.GrainId);
        }

        /// <summary>
        /// Gets a reference to the specified system target.
        /// </summary>
        /// <typeparam name="TGrainInterface">The system target interface.</typeparam>
        /// <param name="grainId">The id of the target.</param>
        /// <returns>A reference to the specified system target.</returns>
        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId)
            where TGrainInterface : ISystemTarget
        {
            ISystemTarget reference;
            Tuple<GrainId, Type> key = Tuple.Create(grainId, typeof(TGrainInterface));

            lock (this.typedSystemTargetReferenceCache)
            {
                if (this.typedSystemTargetReferenceCache.TryGetValue(key, out reference))
                {
                    return (TGrainInterface)reference;
                }

                reference = this.Cast<TGrainInterface>(GrainReference.FromGrainId(grainId, this.GrainReferenceRuntime, null));
                this.typedSystemTargetReferenceCache[key] = reference;
                return (TGrainInterface)reference;
            }
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable
        {
            return (TGrainInterface)this.CreateGrainReference(typeof(TGrainInterface), grainId);
        }

        /// <inheritdoc />
        public GrainReference GetGrain(GrainId grainId, string genericArguments) => GrainReference.FromGrainId(grainId, this.GrainReferenceRuntime, genericArguments);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, Guid grainPrimaryKey)
            where TGrainInterface : IGrain
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension: null, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, long grainPrimaryKey)
            where TGrainInterface : IGrain
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension: null, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, string grainPrimaryKey)
            where TGrainInterface : IGrain
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(grainInterfaceType, grainPrimaryKey, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            where TGrainInterface : IGrain
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension: keyExtension, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            where TGrainInterface : IGrain
        {
            return (TGrainInterface)CreateGrainReference(typeof(TGrainInterface), GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension: keyExtension, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
        {
            return (IGrain)CreateGrainReference(grainInterfaceType, GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension: null, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey)
        {
            return (IGrain)CreateGrainReference(grainInterfaceType, GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension: null, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            return (IGrain)CreateGrainReference(grainInterfaceType, GetGrainId(grainInterfaceType, grainPrimaryKey, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
        {
            return (IGrain)CreateGrainReference(grainInterfaceType, GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension, grainClassNamePrefix: null));
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
        {
            return (IGrain)CreateGrainReference(grainInterfaceType, GetGrainId(grainInterfaceType, grainPrimaryKey, keyExtension, grainClassNamePrefix: null));
        }

        private GrainId GetGrainId(Type interfaceType, long grainPrimaryKey, string keyExtension, string grainClassNamePrefix)
        {
            return LegacyGrainId.GetGrainId(GetTypeCode(interfaceType, grainClassNamePrefix), grainPrimaryKey, keyExtension);
        }

        private GrainId GetGrainId(Type interfaceType, Guid grainPrimaryKey, string keyExtension, string grainClassNamePrefix)
        {
            return LegacyGrainId.GetGrainId(GetTypeCode(interfaceType, grainClassNamePrefix), grainPrimaryKey, keyExtension);
        }

        private GrainId GetGrainId(Type interafaceType, string grainPrimaryKey, string grainClassNamePrefix)
        {
            return LegacyGrainId.GetGrainId(GetTypeCode(interafaceType, grainClassNamePrefix), grainPrimaryKey);
        }

        private long GetTypeCode(Type interfaceType, string grainClassNamePrefix = null)
        {
            if (!GrainInterfaceUtils.IsGrainType(interfaceType))
            {
                throw new ArgumentException("Cannot create grain-reference for non-grain type: " + interfaceType.FullName);
            }

            var found = this.runtimeClient.GrainTypeResolver.TryGetGrainClassData(interfaceType, out GrainClassData implementation, grainClassNamePrefix);
            if (!found)
            {
                var grainClassPrefixString = string.IsNullOrEmpty(grainClassNamePrefix)
                    ? string.Empty
                    : ", grainClassNamePrefix: " + grainClassNamePrefix;
                throw new ArgumentException(
                    $"Cannot find an implementation class for grain interface: {interfaceType} with implementation prefix: {grainClassPrefixString ?? "(none)"}. " +
                    "Make sure the grain assembly was correctly deployed and loaded in the silo.");
            }

            return implementation.GetTypeCode(interfaceType);
        }

        private object CreateGrainReference(Type interfaceType, GrainId grainId) => GrainReferenceRuntime.GrainReferenceFactory.CreateGrainReference(interfaceType, grainId);

        private object CreateObjectReference(Type interfaceType, IAddressable obj)
        {
            if (!interfaceType.IsInterface)
            {
                throw new ArgumentException(
                    $"The provided type parameter must be an interface. '{interfaceType.FullName}' is not an interface.");
            }

            if (!interfaceType.IsInstanceOfType(obj))
            {
                throw new ArgumentException($"The provided object must implement '{interfaceType.FullName}'.", nameof(obj));
            }

            IGrainMethodInvoker invoker;
            if (!this.invokers.TryGetValue(interfaceType, out invoker))
            {
                var invokerType = this.typeCache.GetGrainMethodInvokerType(interfaceType);
                invoker = (IGrainMethodInvoker)Activator.CreateInstance(invokerType);
                this.invokers.TryAdd(interfaceType, invoker);
            }

            return this.Cast(this.runtimeClient.CreateObjectReference(obj, invoker), interfaceType);
        }
    }
}
