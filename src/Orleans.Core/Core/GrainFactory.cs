using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    internal class GrainFactory : IInternalGrainFactory
    {
        private GrainReferenceRuntime grainReferenceRuntime;

        /// <summary>
        /// The cache of typed system target references.
        /// </summary>
        private readonly Dictionary<(GrainId, Type), ISystemTarget> typedSystemTargetReferenceCache = new Dictionary<(GrainId, Type), ISystemTarget>();

        private readonly ImrGrainMethodInvokerProvider invokers;
        private readonly GrainReferenceActivator referenceActivator;
        private readonly GrainInterfaceTypeResolver interfaceTypeResolver;
        private readonly GrainInterfaceTypeToGrainTypeResolver interfaceTypeToGrainTypeResolver;
        private readonly IRuntimeClient runtimeClient;

        public GrainFactory(
            IRuntimeClient runtimeClient,
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            GrainInterfaceTypeToGrainTypeResolver interfaceToTypeResolver,
            ImrGrainMethodInvokerProvider invokers)
        {
            this.runtimeClient = runtimeClient;
            this.referenceActivator = referenceActivator;
            this.interfaceTypeResolver = interfaceTypeResolver;
            this.interfaceTypeToGrainTypeResolver = interfaceToTypeResolver;
            this.invokers = invokers;
        }

        private GrainReferenceRuntime GrainReferenceRuntime => this.grainReferenceRuntime ??= (GrainReferenceRuntime)this.runtimeClient.GrainReferenceRuntime;

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(primaryKey);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey
        {
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(primaryKey);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            var grainKey = IdSpan.Create(primaryKey);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            var grainKey = GrainIdKeyExtensions.CreateGuidKey(primaryKey, keyExtension);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(primaryKey, keyExtension);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }


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
        public object Cast(IAddressable grain, Type interfaceType) => this.GrainReferenceRuntime.Cast(grain, interfaceType);

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
            ValueTuple<GrainId, Type> key = ValueTuple.Create(grainId, typeof(TGrainInterface));

            lock (this.typedSystemTargetReferenceCache)
            {
                if (this.typedSystemTargetReferenceCache.TryGetValue(key, out reference))
                {
                    return (TGrainInterface)reference;
                }

                reference = this.GetGrain<TGrainInterface>(grainId);
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
        public IAddressable GetGrain(GrainId grainId) => this.referenceActivator.CreateReference(grainId, default);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid key)
        {
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(key);
            return (IGrain)GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long key)
        {
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(key);
            return (IGrain)GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string key)
        {
            var grainKey = IdSpan.Create(key);
            return (IGrain)GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid key, string keyExtension)
        {
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(key, keyExtension);
            return (IGrain)GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long key, string keyExtension)
        {
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(key, keyExtension);
            return (IGrain)GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        private IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
        {
            var grainInterfaceType = this.interfaceTypeResolver.GetGrainInterfaceType(interfaceType);

            GrainType grainType;
            if (!string.IsNullOrWhiteSpace(grainClassNamePrefix))
            {
                grainType = this.interfaceTypeToGrainTypeResolver.GetGrainType(grainInterfaceType, grainClassNamePrefix);
            }
            else
            {
                grainType = this.interfaceTypeToGrainTypeResolver.GetGrainType(grainInterfaceType);
            }

            var grainId = GrainId.Create(grainType, grainKey);
            var grain = this.referenceActivator.CreateReference(grainId, grainInterfaceType);
            return grain;
        }

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
        {
            return this.referenceActivator.CreateReference(grainId, interfaceType);
        }

        private object CreateGrainReference(Type interfaceType, GrainId grainId)
        {
            var grainInterfaceType = this.interfaceTypeResolver.GetGrainInterfaceType(interfaceType);
            return this.referenceActivator.CreateReference(grainId, grainInterfaceType);
        }

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

            var grainInterfaceType = this.interfaceTypeResolver.GetGrainInterfaceType(interfaceType);
            if (!this.invokers.TryGet(grainInterfaceType, out var invoker))
            {
                throw new KeyNotFoundException($"Could not find an invoker for interface {grainInterfaceType}");
            }

            return this.Cast(this.runtimeClient.CreateObjectReference(obj, invoker), interfaceType);
        }

        private static void DisallowNullOrWhiteSpaceKeyExtensions(string keyExt)
        {
            if (!string.IsNullOrWhiteSpace(keyExt)) return;

            if (null == keyExt)
            {
                throw new ArgumentNullException(nameof(keyExt)); 
            }
            
            throw new ArgumentException("Key extension is empty or white space.", nameof(keyExt));
        }
    }
}
