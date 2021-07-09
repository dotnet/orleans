using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;

namespace Orleans.Runtime
{
    /// <summary>
    /// Shared runtime services which grains use.
    /// </summary>
    internal class InternalGrainRuntime
    {
        public InternalGrainRuntime(
            MessageCenter messageCenter,
            Catalog catalog,
            GrainVersionManifest versionManifest,
            RuntimeMessagingTrace messagingTrace,
            GrainLocator grainLocator,
            CompatibilityDirectorManager compatibilityDirectorManager,
            IOptions<GrainCollectionOptions> collectionOptions,
            ActivationCollector activationCollector,
            ILocalGrainDirectory localGrainDirectory,
            IActivationWorkingSet activationWorkingSet)
        {
            MessageCenter = messageCenter;
            Catalog = catalog;
            RuntimeClient = catalog.RuntimeClient;
            GrainVersionManifest = versionManifest;
            MessagingTrace = messagingTrace;
            CompatibilityDirectorManager = compatibilityDirectorManager;
            GrainLocator = grainLocator;
            CollectionOptions = collectionOptions;
            ActivationCollector = activationCollector;
            LocalGrainDirectory = localGrainDirectory;
            ActivationWorkingSet = activationWorkingSet;
        }

        public InsideRuntimeClient RuntimeClient { get; }
        public MessageCenter MessageCenter { get; }
        public Catalog Catalog { get; }
        public GrainVersionManifest GrainVersionManifest { get; }
        public RuntimeMessagingTrace MessagingTrace { get; }
        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }
        public GrainLocator GrainLocator { get; }
        public IOptions<GrainCollectionOptions> CollectionOptions { get; }
        public ActivationCollector ActivationCollector { get; }
        public ILocalGrainDirectory LocalGrainDirectory { get; }
        public IActivationWorkingSet ActivationWorkingSet { get; }
    }
}
