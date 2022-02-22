using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        private readonly GrainReferenceActivator referenceActivator;
        private readonly GrainInterfaceTypeResolver interfaceTypeResolver;
        private readonly GrainInterfaceTypeToGrainTypeResolver interfaceTypeToGrainTypeResolver;
        private readonly IRuntimeClient runtimeClient;

        public GrainFactory(
            IRuntimeClient runtimeClient,
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            GrainInterfaceTypeToGrainTypeResolver interfaceToTypeResolver)
        {
            this.runtimeClient = runtimeClient;
            this.referenceActivator = referenceActivator;
            this.interfaceTypeResolver = interfaceTypeResolver;
            this.interfaceTypeToGrainTypeResolver = interfaceToTypeResolver;
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
            ValidateGrainKeyExtension(keyExtension);

            var grainKey = GrainIdKeyExtensions.CreateGuidKey(primaryKey, keyExtension);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            ValidateGrainKeyExtension(keyExtension);

            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(primaryKey, keyExtension);
            return (TGrainInterface)GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }


        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
        {
            return this.CreateObjectReference<TGrainObserverInterface>((IAddressable)obj);
        }

        /// <inheritdoc />
        public void DeleteObjectReference<TGrainObserverInterface>(
            IGrainObserver obj) where TGrainObserverInterface : IGrainObserver
        {
            this.runtimeClient.DeleteObjectReference(obj);
        }

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
                where TGrainObserverInterface : IAddressable
        {
            return (TGrainObserverInterface)this.CreateObjectReference(typeof(TGrainObserverInterface), obj);
        }

        /// <inheritdoc />
        public TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceType = typeof(TGrainInterface);
            return (TGrainInterface)this.Cast(grain, interfaceType);
        }

        /// <inheritdoc />
        public object Cast(IAddressable grain, Type interfaceType) => this.GrainReferenceRuntime.Cast(grain, interfaceType);

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            var grainId = SystemTargetGrainId.Create(grainType, destination);
            return this.GetSystemTarget<TGrainInterface>(grainId.GrainId);
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
        {
            return this.referenceActivator.CreateReference(grainId, interfaceType);
        }

        /// <summary>
        /// Gets a grain reference which implements the specified grain interface type and has the specified grain key, without specifying the grain type directly.
        /// </summary>
        /// <remarks>
        /// This method infers the most appropriate <see cref="GrainId.Type"/> value based on the <paramref name="interfaceType"/> argument and optional <paramref name="grainClassNamePrefix"/> argument.
        /// The <see cref="GrainInterfaceTypeToGrainTypeResolver"/> type is responsible for determining the most appropriate grain type.
        /// </remarks>
        /// <param name="interfaceType">The interface type which the returned grain reference will implement.</param>
        /// <param name="grainKey">The <see cref="GrainId.Key"/> portion of the grain id.</param>
        /// <param name="grainClassNamePrefix">An optional grain class name prefix.</param>
        /// <returns>A grain reference which implements the provided interface.</returns>
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

        /// <summary>
        /// Creates a grain reference.
        /// </summary>
        /// <param name="interfaceType">The interface type which the reference must implement..</param>
        /// <param name="grainId">The grain id which the reference will target.</param>
        /// <returns>A grain reference.</returns>
        private object CreateGrainReference(Type interfaceType, GrainId grainId)
        {
            var grainInterfaceType = this.interfaceTypeResolver.GetGrainInterfaceType(interfaceType);
            return this.referenceActivator.CreateReference(grainId, grainInterfaceType);
        }

        /// <summary>
        /// Creates an object reference which points to the provided object.
        /// </summary>
        /// <param name="interfaceType">The interface type which the reference must implement..</param>
        /// <param name="obj">The addressable object implementation.</param>
        /// <returns>An object reference.</returns>
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

            return this.Cast(this.runtimeClient.CreateObjectReference(obj), interfaceType);
        }

        /// <summary>
        /// Validates the provided grain key extension.
        /// </summary>
        /// <param name="keyExt">The grain key extension.</param>
        /// <exception cref="ArgumentNullException">The key is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The key is empty or contains only whitespace.</exception>
        private static void ValidateGrainKeyExtension(string keyExt)
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
