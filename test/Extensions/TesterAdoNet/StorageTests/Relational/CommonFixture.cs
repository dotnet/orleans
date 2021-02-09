using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Tests.SqlUtils;
using Orleans.Storage;
using TestExtensions;
using UnitTests.General;
using Orleans.Streams;

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
        private Dictionary<string, IGrainStorage> StorageProviders { get; set; } = new Dictionary<string, IGrainStorage>();

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
            _ = this.Services.GetRequiredService<IOptions<ClusterOptions>>();
            DefaultProviderRuntime = new ClientProviderRuntime(
                this.InternalGrainFactory,
                this.Services,
                this.Services.GetRequiredService<ClientGrainContext>());
        }

        /// <summary>
        /// Returns a correct implementation of the persistence provider according to environment variables.
        /// </summary>
        /// <remarks>If the environment invariants have failed to hold upon creation of the storage provider,
        /// a <em>null</em> value will be provided.</remarks>
        public async Task<IGrainStorage> GetStorageProvider(string storageInvariant)
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

                            var options = new AdoNetGrainStorageOptions()
                            {
                                ConnectionString = Storage.Storage.ConnectionString,
                                Invariant = storageInvariant
                            };
                            var clusterOptions = new ClusterOptions()
                            {
                                ServiceId = Guid.NewGuid().ToString()
                            };
                            var storageProvider = new AdoNetGrainStorage(DefaultProviderRuntime.ServiceProvider.GetService<ILogger<AdoNetGrainStorage>>(), DefaultProviderRuntime, Options.Create(options), Options.Create(clusterOptions), storageInvariant + "_StorageProvider");
                            ISiloLifecycleSubject siloLifeCycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
                            storageProvider.Participate(siloLifeCycle);
                            await siloLifeCycle.OnStart(CancellationToken.None);

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
