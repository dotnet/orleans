using System;
using System.Threading.Tasks;

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
        public IGrainFactory GrainFactory => this.grainFactory;

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => this.grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => this.grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey => this.grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => this.grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => this.grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => this.grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => this.grainFactory.DeleteObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable => this.grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination) => this.grainFactory.GetSystemTarget<TGrainInterface>(grainType, destination);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainId grainId) => this.grainFactory.GetSystemTarget<TGrainInterface>(grainId);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain) => this.grainFactory.Cast<TGrainInterface>(grain);

        /// <inheritdoc />
        object IInternalGrainFactory.Cast(IAddressable grain, Type interfaceType) => this.grainFactory.Cast(grain, interfaceType);

        /// <inheritdoc />
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) => this.grainFactory.GetGrain<TGrainInterface>(grainId);

        /// <inheritdoc />
        IAddressable IGrainFactory.GetGrain(GrainId grainId) => this.grainFactory.GetGrain(grainId);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceId) => this.grainFactory.GetGrain(grainId, interfaceId);
    }
}
