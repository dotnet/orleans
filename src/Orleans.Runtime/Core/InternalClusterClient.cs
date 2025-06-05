using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Client for communicating with clusters of Orleans silos.
    /// </summary>
    internal class InternalClusterClient : IInternalClusterClient
    {
        private readonly IRuntimeClient _runtimeClient;
        private readonly IInternalGrainFactory _grainFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="InternalClusterClient"/> class.
        /// </summary>
        public InternalClusterClient(IRuntimeClient runtimeClient, IInternalGrainFactory grainFactory)
        {
            _runtimeClient = runtimeClient;
            _grainFactory = grainFactory;
        }

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => _runtimeClient.ServiceProvider;

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => _grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => _grainFactory.DeleteObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable => _grainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination) => _grainFactory.GetSystemTarget<TGrainInterface>(grainType, destination);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainId grainId) => _grainFactory.GetSystemTarget<TGrainInterface>(grainId);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain) => _grainFactory.Cast<TGrainInterface>(grain);

        /// <inheritdoc />
        object IInternalGrainFactory.Cast(IAddressable grain, Type interfaceType) => _grainFactory.Cast(grain, interfaceType);

        /// <inheritdoc />
        public GrainInterfaceType GetGrainInterfaceType(Type interfaceType) => _grainFactory.GetGrainInterfaceType(interfaceType);
        
        /// <inheritdoc />
        public IAddressable CreateGrainReference(GrainId grainId, GrainInterfaceType interfaceType) => _grainFactory.CreateGrainReference(grainId, interfaceType);

        /// <inheritdoc />
        public GrainType GetGrainType(GrainInterfaceType grainInterfaceType, string grainClassNamePrefix = null) => _grainFactory.GetGrainType(grainInterfaceType, grainClassNamePrefix);
    }
}
