using System.ComponentModel;
using System.Configuration.Install;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    /// <summary>
    /// Installer of performance counters
    /// </summary>
    [RunInstaller(true)]
    public partial class StorageProvidersInstrumentationInstaller : Installer
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public StorageProvidersInstrumentationInstaller()
        {
            Installers.Add(new StorageProvidersInstrumentationManager(false).GetInstaller());
        }
    }
}
