using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Logging;
using Moq;
using Orleans;
using Orleans.Persistence.Migration;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Tester.AzureUtils.Migration.Units
{
    public class MigrationGrainStorageTests
    {
        private readonly Mock<IGrainStorage> sourceStorage;
        private readonly Mock<IGrainStorage> destinationStorage;
        private readonly ILogger<MigrationGrainStorage> migrationGrainStorageLogger;

        private readonly string grainType;
        private readonly GrainReference? grainReference;

        public MigrationGrainStorageTests()
        {
            sourceStorage = PrepareMockGrainStorage();
            destinationStorage = PrepareMockGrainStorage();
            migrationGrainStorageLogger = new LoggerFactory().CreateLogger<MigrationGrainStorage>();

            grainType = "example";
            grainReference = null; // we dont care - interested part is what we invoke, not the ref \ state
        }

        [Fact]
        public async Task DisabledMode_ShouldNotTouchDestinationStorage()
        {
            var grainState = CreateGrainState();
            var storage = BuildMigrationGrainStorage(GrainMigrationMode.Disabled);

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // only source is invoked
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // destination is never invoked
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
        }

        [Fact]
        public async Task ReadSource_WriteBoth_Mode_ShouldNotTouchDestinationStorage()
        {
            var grainState = CreateGrainState();
            var storage = BuildMigrationGrainStorage(GrainMigrationMode.ReadSource_WriteBoth);

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // ClearStateAsync is invoked for both (because WriteBoth)
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // WriteStateAsync is invoked for both (because WriteBoth)
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // ReadStateAsync is only for source. Destination is not invoked
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
        }

        [Fact]
        public async Task ReadDestinationWithFallback_WriteBoth_Mode_ShouldNotTouchDestinationStorage()
        {
            var grainState = CreateGrainState();
            var storage = BuildMigrationGrainStorage(GrainMigrationMode.ReadDestinationWithFallback_WriteBoth);

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // ClearStateAsync is invoked for both (because WriteBoth)
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // WriteStateAsync is invoked for both (because WriteBoth)
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // ReadStateAsync is happening for both storages, if not found in first lookup (here - destination)
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
        }

        [Fact]
        public async Task ReadDestinationWithFallback_WriteDestination_Mode_ShouldNotTouchDestinationStorage()
        {
            var grainState = CreateGrainState();
            var storage = BuildMigrationGrainStorage(GrainMigrationMode.ReadDestinationWithFallback_WriteDestination);

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // ClearStateAsync is invoked for destination only
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // WriteStateAsync is invoked for destination only
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // ReadStateAsync is happening for both storages, if not found in first lookup (here - destination)
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
        }

        [Fact]
        public async Task ReadWriteDestination_Mode_ShouldNotTouchDestinationStorage()
        {
            var grainState = CreateGrainState();
            var storage = BuildMigrationGrainStorage(GrainMigrationMode.ReadWriteDestination);

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // source not invoked at all
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());

            // destination is always invoked
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
        }

        [Fact]
        public async Task Disable_ShouldAffectBehavior()
        {
            var grainState = CreateGrainState();
            var storage = BuildMigrationGrainStorage(GrainMigrationMode.ReadWriteDestination);

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // source not invoked at all
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Never());

            // destination is always invoked
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // then change to disabled
            storage.DisableMigrationTooling();

            await storage.ClearStateAsync(grainType, grainReference!, grainState);
            await storage.ReadStateAsync(grainType, grainReference!, grainState);
            await storage.WriteStateAsync(grainType, grainReference!, grainState);

            // source is now invoked
            sourceStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            sourceStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            sourceStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());

            // destination is not touched more than it was before
            destinationStorage.Verify(
                x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.ReadStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
            destinationStorage.Verify(
                x => x.WriteStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()),
                times: Times.Once());
        }

        IGrainState CreateGrainState()
        {
            var state = new GrainState<object>();
            state.ETag = "{}"; // "nothing" ETag for the MigrationGrainStorage impl details
            return state;
        }

        MigrationGrainStorage BuildMigrationGrainStorage(GrainMigrationMode grainMigrationMode)
            => new MigrationGrainStorage(sourceStorage.Object, destinationStorage.Object, new MigrationGrainStorageOptions { Mode = grainMigrationMode }, migrationGrainStorageLogger);

        Mock<IGrainStorage> PrepareMockGrainStorage()
        {
            var moq = new Mock<IGrainStorage>();
            moq
                .Setup(x => x.ClearStateAsync(It.IsAny<string>(), It.IsAny<GrainReference>(), It.IsAny<IGrainState>()))
                .Returns(Task.CompletedTask)
                .Callback((string x, GrainReference y, IGrainState state) => state.RecordExists = false);

            return moq;
        }
    }

    class NoopGrainStorage : IGrainStorage
    {
        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => Task.CompletedTask; 
        public Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => Task.CompletedTask;
        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => Task.CompletedTask;
    }
}