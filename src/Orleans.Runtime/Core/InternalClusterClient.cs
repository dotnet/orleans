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
            where TGrainInterface : IGrainWithGuidKey
        {
            return this.grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey
        {
            return this.grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            return this.grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            return this.grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            return this.grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
        {
            return this.grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver
        {
            this.grainFactory.DeleteObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable
        {
            return this.grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination)
        {
            return this.grainFactory.GetSystemTarget<TGrainInterface>(grainType, destination);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainId grainId)
        {
            return this.grainFactory.GetSystemTarget<TGrainInterface>(grainId);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain)
        {
            return this.grainFactory.Cast<TGrainInterface>(grain);
        }

        /// <inheritdoc />
        object IInternalGrainFactory.Cast(IAddressable grain, Type interfaceType)
        {
            return this.grainFactory.Cast(grain, interfaceType);
        }

        /// <inheritdoc />
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
        {
            return this.grainFactory.GetGrain<TGrainInterface>(grainId);
        }

        /// <inheritdoc />
        IAddressable IGrainFactory.GetGrain(GrainId grainId)
        {
            return this.grainFactory.GetGrain(grainId);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
        {
            return this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey)
        {
            return this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            return this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
        {
            return this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
        {
            return this.grainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);
        }

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceId)
        {
            return this.grainFactory.GetGrain(grainId, interfaceId);
        }
    }
}
