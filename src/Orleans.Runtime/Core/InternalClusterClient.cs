using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Client for communicating with clusters of Orleans silos.
    /// </summary>
    internal class InternalClusterClient : IInternalClusterClient
    {
        private readonly IRuntimeClient runtimeClient;
        private readonly IInternalGrainFactory grainFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="InternalClusterClient"/> class.
        /// </summary>
        public InternalClusterClient(IRuntimeClient runtimeClient, IInternalGrainFactory grainFactory)
        {
            this.runtimeClient = runtimeClient;
            this.grainFactory = grainFactory;
        }

        /// <inheritdoc />
        public IGrainFactory GrainFactory => grainFactory;

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => runtimeClient.ServiceProvider;

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey => grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => grainFactory.DeleteObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable => grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination) => grainFactory.GetSystemTarget<TGrainInterface>(grainType, destination);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainId grainId) => grainFactory.GetSystemTarget<TGrainInterface>(grainId);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain) => grainFactory.Cast<TGrainInterface>(grain);

        /// <inheritdoc />
        object IInternalGrainFactory.Cast(IAddressable grain, Type interfaceType) => grainFactory.Cast(grain, interfaceType);

        /// <inheritdoc />
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) => grainFactory.GetGrain<TGrainInterface>(grainId);

        /// <inheritdoc />
        IAddressable IGrainFactory.GetGrain(GrainId grainId) => grainFactory.GetGrain(grainId);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceId) => grainFactory.GetGrain(grainId, interfaceId);
    }
}
