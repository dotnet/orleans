using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.AzureUtils.Migration
{
    public abstract class MigrationBaseTests
    {
        protected BaseAzureTestClusterFixture fixture;
        public const string SourceStorageName = "source-storage";
        public const string DestinationStorageName = "destination-storage";

        private IServiceProvider? serviceProvider;
        private IServiceProvider ServiceProvider
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

        private IGrainStorage? sourceStorage;
        private IGrainStorage SourceStorage
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
        private IGrainStorage DestinationStorage
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
        private IGrainStorage MigrationStorage
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

        protected MigrationBaseTests(BaseAzureTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task ReadFromSourceTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(100);
            var grainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var stateName = (typeof(SimplePersistentGrain)).FullName;

            // Write directly to source storage
            await SourceStorage.WriteStateAsync(stateName, (GrainReference) grain, grainState);

            Assert.Equal(grainState.State.A, await grain.GetA());
            Assert.Equal(grainState.State.A * grainState.State.B, await grain.GetAxB());
        }

        [Fact]
        public async Task ReadFromTargetTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(200);
            var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var newGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 20, B = 30 });
            var stateName = (typeof(SimplePersistentGrain)).FullName;

            // Write directly to storages
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);
            await DestinationStorage.WriteStateAsync(stateName, (GrainReference)grain, newGrainState);

            Assert.Equal(newGrainState.State.A, await grain.GetA());
            Assert.Equal(newGrainState.State.A * newGrainState.State.B, await grain.GetAxB());
        }

        [Fact]
        public async Task ReadFromSourceThenWriteToTargetTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(300);
            var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var newState = new SimplePersistentGrain_State { A = 20, B = 30 };
            var stateName = (typeof(SimplePersistentGrain)).FullName;

            // Write directly to source storage
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);

            // Grain should read from source but write to destination
            Assert.Equal(oldGrainState.State.A, await grain.GetA());
            Assert.Equal(oldGrainState.State.A * oldGrainState.State.B, await grain.GetAxB());
            await grain.SetA(newState.A);
            await grain.SetB(newState.B);

            var newGrainState = new GrainState<SimplePersistentGrain_State>();
            await DestinationStorage.ReadStateAsync(stateName, (GrainReference)grain, newGrainState);

            Assert.Equal(newGrainState.State.A, await grain.GetA());
            Assert.Equal(newGrainState.State.A * newGrainState.State.B, await grain.GetAxB());
        }

        [Fact]
        public async Task ClearAllTest()
        {
            var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(400);
            var oldGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 33, B = 806 });
            var newGrainState = new GrainState<SimplePersistentGrain_State>(new() { A = 20, B = 30 });
            var stateName = (typeof(SimplePersistentGrain)).FullName;

            // Write directly to storages
            await SourceStorage.WriteStateAsync(stateName, (GrainReference)grain, oldGrainState);
            await DestinationStorage.WriteStateAsync(stateName, (GrainReference)grain, newGrainState);

            // Clear
            var migratedState = new GrainState<SimplePersistentGrain_State>();
            await MigrationStorage.ReadStateAsync(stateName, (GrainReference)grain, migratedState);
            await MigrationStorage.ClearStateAsync(stateName, (GrainReference)grain, migratedState);

            // Read
            var oldGrainState2 = new GrainState<SimplePersistentGrain_State>();
            var newGrainState2 = new GrainState<SimplePersistentGrain_State>();
            await SourceStorage.ReadStateAsync(stateName, (GrainReference)grain, oldGrainState2);
            await DestinationStorage.ReadStateAsync(stateName, (GrainReference)grain, newGrainState2);
            Assert.False(oldGrainState2.RecordExists);
            Assert.False(newGrainState2.RecordExists);
        }
    }
}
