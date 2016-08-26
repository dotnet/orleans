using Orleans.Streams.AdHoc;

namespace Orleans.Runtime
{
    using Orleans.Core;

    internal class ObserverGrainExtensionManager : IObserverGrainExtensionManager
    {
        private readonly IGrainExtensionManager extensionManager;

        public ObserverGrainExtensionManager(IGrainExtensionManager extensionManager)
        {
            this.extensionManager = extensionManager;
        }

        /// <summary>
        /// Returns the <see cref="IObserverGrainExtension"/> for the current grain, installing it if required.
        /// </summary>
        /// <returns>The <see cref="IObserverGrainExtension"/> for the current grain.</returns>
        public IObserverGrainExtension GetOrAddExtension()
        {
            IObserverGrainExtensionRemote handler;
            if (!this.extensionManager.TryGetExtensionHandler(out handler))
            {
                this.extensionManager.TryAddExtension(handler = new ObserverGrainExtension(), typeof(IObserverGrainExtensionRemote));
            }

            return handler as IObserverGrainExtension;
        }
    }
}