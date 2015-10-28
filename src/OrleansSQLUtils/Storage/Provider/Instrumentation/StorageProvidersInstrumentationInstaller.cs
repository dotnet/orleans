using System.ComponentModel;
using System.Configuration.Install;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    [RunInstaller(true)]
    public partial class StorageProvidersInstrumentationInstaller : Installer
    {
        public StorageProvidersInstrumentationInstaller()
        {
            Installers.Add(new StorageProvidersInstrumentationManager(false).GetInstaller());
        }
    }
}
