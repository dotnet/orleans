using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Microsoft.Extensions.Options;
using Orleans.Configuration;



#if NET7_0_OR_GREATER
using Orleans.Storage.Migration.AzureStorage;
using Orleans.Persistence.Migration;
#endif

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationBaseTests
    {
        protected BaseAzureTestClusterFixture fixture;
        public const string SourceStorageName = "source-storage";
        public const string DestinationStorageName = "destination-storage";

        protected MigrationBaseTests(BaseAzureTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        private IServiceProvider? serviceProvider;
        protected IServiceProvider ServiceProvider
        {
            get
            {
                if (this.serviceProvider == null)
                {
                    var silo = (InProcessSiloHandle)this.fixture.HostedCluster.Primary;
                    this.serviceProvider = silo.SiloHost.Services;
                }
                return this.serviceProvider;
            }
        }

        private ClusterOptions? clusterOptions;
        protected ClusterOptions ClusterOptions
        {
            get
            {
                if (this.clusterOptions == null)
                {
                    this.clusterOptions = ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value;
                }
                return this.clusterOptions;
            }
        }

        private IGrainStorage? sourceStorage;
        protected IGrainStorage SourceStorage
        {
            get
            {
                if (this.sourceStorage == null)
                {
                    this.sourceStorage = ServiceProvider.GetRequiredServiceByName<IGrainStorage>(SourceStorageName);
                }
                return this.sourceStorage;
            }
        }

        private IGrainStorage? destinationStorage;
        protected IGrainStorage DestinationStorage
        {
            get
            {
                if (this.destinationStorage == null)
                {
                    this.destinationStorage = ServiceProvider.GetRequiredServiceByName<IGrainStorage>(DestinationStorageName);
                }
                return this.destinationStorage;
            }
        }

        private IGrainStorage? migrationStorage;
        protected IGrainStorage MigrationStorage
        {
            get
            {
                if (this.migrationStorage == null)
                {
                    this.migrationStorage = ServiceProvider.GetRequiredServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
                }
                return this.migrationStorage;
            }
        }

        private IReminderTable? reminderTable;
        protected IReminderTable ReminderTable
        {
            get
            {
                if (reminderTable == null)
                {
                    reminderTable = ServiceProvider.GetRequiredService<IReminderTable>();
                }
                return reminderTable;
            }
        }

#if NET7_0_OR_GREATER
        private OfflineMigrator? offlineMigrator;
        protected OfflineMigrator OfflineMigrator
        {
            get
            {
                if (this.offlineMigrator == null)
                {
                    this.offlineMigrator = ServiceProvider.GetRequiredService<OfflineMigrator>();
                }
                return this.offlineMigrator;
            }
        }
#endif

    }
}