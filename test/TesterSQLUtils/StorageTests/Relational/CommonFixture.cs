using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage;
using Orleans.SqlUtils;
using Orleans.Storage;

using TestExtensions;

using UnitTests.General;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// A common fixture to all the classes. XUnit.NET keeps this alive during the test run upon first
    /// instantiation, so this is used to coordinate environment invariant enforcing and to cache
    /// heavier setup.
    /// </summary>
    public class CommonFixture : TestEnvironmentFixture
    {
        /// <summary>
        /// Caches storage provider for multiple uses using a unique, back-end specific key.
        /// The value will be <em>null</em> if environment invariants have failed to hold upon
        /// storage provider creation.
        /// </summary>
        private Dictionary<string, IStorageProvider> StorageProviders { get; set; } = new Dictionary<string, IStorageProvider>();

        /// <summary>
        /// This is used to lock the storage providers dictionary.
        /// </summary>
        private static AsyncLock StorageLock { get; } = new AsyncLock();

        /// <summary>
        /// Caches DefaultProviderRuntime for multiple uses.
        /// </summary>
        private IProviderRuntime DefaultProviderRuntime { get; }

        /// <summary>
        /// The environment contract and its invariants.
        /// </summary>
        private TestEnvironmentInvariant Invariants { get; } = new TestEnvironmentInvariant();

        /// <summary>
        /// The underlying relational storage connection if used.
        /// </summary>
        public RelationalStorageForTesting Storage { get; set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public CommonFixture()
        {
            DefaultProviderRuntime = new StorageProviderManager(
                this.GrainFactory,
                this.Services,
                new ClientProviderRuntime(this.InternalGrainFactory, this.Services));
            ((StorageProviderManager) DefaultProviderRuntime).LoadEmptyStorageProviders().WaitWithThrow(TestConstants.InitTimeout);
        }


        /// <summary>
        /// Returns a correct implementation of the persistence provider according to environment variables.
        /// </summary>
        /// <remarks>If the environment invariants have failed to hold upon creation of the storage provider,
        /// a <em>null</em> value will be provided.</remarks>
        public async Task<IStorageProvider> GetStorageProvider(string storageInvariant)
        {
            //Make sure the environment invariants hold before trying to give a functioning SUT instantiation.
            //This is done instead of the constructor to have more granularity on how the environment should be initialized.
            try
            {
                using(await StorageLock.LockAsync())
                {
                    if(AdoNetInvariants.Invariants.Contains(storageInvariant))
                    {
                        if(!StorageProviders.ContainsKey(storageInvariant))
                        {
                            Storage = Invariants.EnsureStorageForTesting(Invariants.ActiveSettings.ConnectionStrings.First(i => i.StorageInvariant == storageInvariant));

                            var properties = new Dictionary<string, string>();
                            properties["DataConnectionString"] = Storage.Storage.ConnectionString;
                            properties["AdoInvariant"] = storageInvariant;

                            var config = new ProviderConfiguration(properties, null);
                            var storageProvider = new AdoNetStorageProvider();
                            await storageProvider.Init(storageInvariant + "_StorageProvider", DefaultProviderRuntime, config);

                            StorageProviders[storageInvariant] = storageProvider;
                        }
                    }
                }
            }
            catch
            {
                StorageProviders.Add(storageInvariant, null);
            }

            return StorageProviders[storageInvariant];
        }
    }
}
