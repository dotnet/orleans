using Orleans.Core.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using TestGrains;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class MigrationTests : HostedTestClusterEnsureDefaultStarted
    {
        public MigrationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that grain migration works for a simple grain which directly implements <see cref="IGrainMigrationParticipant"/>.
        /// The test does not specify an alternative location for the grain to migrate to, but spins until it selects an alternative on its own.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task BasicGrainMigrationTest()
        {
            for (var i = 1; i < 100; ++i)
            {
                var grain = GrainFactory.GetGrain<IMigrationTestGrain>(GetRandomGrainId());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalHost = await grain.GetHostAddress();
                SiloAddress newHost;
                do
                {
                    // Trigger migration without setting a placement hint, so the grain placement provider will be
                    // free to select any location including the existing one.
                    await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();
                    newHost = await grain.GetHostAddress();
                } while (newHost == originalHost);

                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);
            }
        }

        /// <summary>
        /// Tests that grain migration works for a simple grain which directly implements <see cref="IGrainMigrationParticipant"/>.
        /// The test specifies an alternative location for the grain to migrate to and asserts that it migrates to that location.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DirectedGrainMigrationTest()
        {
            for (var i = 1; i < 100; ++i)
            {
                var grain = GrainFactory.GetGrain<IMigrationTestGrain>(GetRandomGrainId());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalHost = await grain.GetHostAddress();
                var targetHost = Fixture.HostedCluster.GetActiveSilos().Select(s => s.SiloAddress).First(address => address != originalHost);

                // Trigger migration, setting a placement hint to coerce the placement director to use the target silo
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                var newHost = await grain.GetHostAddress();
                Assert.Equal(targetHost, newHost);

                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);
            }
        }

        /// <summary>
        /// Tests that grain migration works for a simple grain which uses <see cref="Grain{TGrainState}"/> for state.
        /// The test specifies an alternative location for the grain to migrate to and asserts that it migrates to that location.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DirectedGrainMigrationTest_GrainOfT()
        {
            for (var i = 1; i < 100; ++i)
            {
                var grain = GrainFactory.GetGrain<IMigrationTestGrain_GrainOfT>(GetRandomGrainId());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalHost = await grain.GetHostAddress();
                var targetHost = Fixture.HostedCluster.GetActiveSilos().Select(s => s.SiloAddress).First(address => address != originalHost);

                // Trigger migration, setting a placement hint to coerce the placement director to use the target silo
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                var newHost = await grain.GetHostAddress();
                Assert.Equal(targetHost, newHost);

                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);
            }
        }

        /// <summary>
        /// Tests that grain migration works for a simple grain which uses <see cref="IPersistentState{TState}"/> for state.
        /// The test specifies an alternative location for the grain to migrate to and asserts that it migrates to that location.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task DirectedGrainMigrationTest_IPersistentStateOfT()
        {
            for (var i = 1; i < 100; ++i)
            {
                var grain = GrainFactory.GetGrain<IMigrationTestGrain_IPersistentStateOfT>(GetRandomGrainId());
                var expectedStateA = Random.Shared.Next();
                var expectedStateB = Random.Shared.Next();
                await grain.SetState(expectedStateA, expectedStateB);
                var originalHost = await grain.GetHostAddress();
                var targetHost = Fixture.HostedCluster.GetActiveSilos().Select(s => s.SiloAddress).First(address => address != originalHost);

                // Trigger migration, setting a placement hint to coerce the placement director to use the target silo
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                var newHost = await grain.GetHostAddress();
                Assert.Equal(targetHost, newHost);

                var (actualA, actualB) = await grain.GetState();
                Assert.Equal(expectedStateA, actualA);
                Assert.Equal(expectedStateB, actualB);
            }
        }
    }

    public interface IMigrationTestGrain : IGrainWithIntegerKey
    {
        ValueTask SetState(int state);
        ValueTask<int> GetState();
        ValueTask<SiloAddress> GetHostAddress();
    }

    public class MigrationTestGrain : IMigrationTestGrain, IGrainMigrationParticipant
    {
        private int _state;
        private readonly SiloAddress _hostAddress;
        public  MigrationTestGrain(ILocalSiloDetails siloDetails) => _hostAddress = siloDetails.SiloAddress;

        public ValueTask<int> GetState() => new(_state);

        public ValueTask SetState(int state)
        {
            _state = state;
            return default;
        }

        public ValueTask<SiloAddress> GetHostAddress() => new(_hostAddress);

        public void OnDehydrate(IDehydrationContext migrationContext)
        {
            migrationContext.TryAddValue("state", _state);
        }

        public void OnRehydrate(IRehydrationContext migrationContext)
        {
            migrationContext.TryGetValue("state", out _state);
        }
    }

    public interface IMigrationTestGrain_GrainOfT : IGrainWithIntegerKey
    {
        ValueTask SetState(int state);
        ValueTask<int> GetState();
        ValueTask<SiloAddress> GetHostAddress();
    }

    public class MigrationTestGrainWithMemoryStorage : Grain<MyMigrationStateClass>, IMigrationTestGrain_GrainOfT
    {
        private readonly SiloAddress _hostAddress;
        public  MigrationTestGrainWithMemoryStorage(ILocalSiloDetails siloDetails) => _hostAddress = siloDetails.SiloAddress;

        public ValueTask<int> GetState() => new(State.Value);

        public ValueTask SetState(int state)
        {
            State.Value = state;
            return default;
        }

        public ValueTask<SiloAddress> GetHostAddress() => new(_hostAddress);
    }

    [GenerateSerializer]
    public class MyMigrationStateClass
    {
        [Id(0)]
        public int Value { get; set; }
    }

    public interface IMigrationTestGrain_IPersistentStateOfT : IGrainWithIntegerKey
    {
        ValueTask SetState(int a, int b);
        ValueTask<(int A, int B)> GetState();
        ValueTask<SiloAddress> GetHostAddress();
    }

    public class MigrationTestGrainWithInjectedMemoryStorage : IMigrationTestGrain_IPersistentStateOfT
    {
        private readonly SiloAddress _hostAddress;
        private readonly IPersistentState<MyMigrationStateClass> _stateA;
        private readonly IPersistentState<MyMigrationStateClass> _stateB;

        public MigrationTestGrainWithInjectedMemoryStorage(
            [PersistentState("a")] IPersistentState<MyMigrationStateClass> stateA,
            [PersistentState("b")] IPersistentState<MyMigrationStateClass> stateB,
            ILocalSiloDetails siloDetails)
        {
            _stateA = stateA;
            _stateB = stateB;
            _hostAddress = siloDetails.SiloAddress;
        }

        public ValueTask<(int A, int B)> GetState() => new((_stateA.State.Value, _stateB.State.Value));

        public ValueTask SetState(int a, int b)
        {
            _stateA.State.Value = a;
            _stateB.State.Value = b;
            return default;
        }

        public ValueTask<SiloAddress> GetHostAddress() => new(_hostAddress);
    }
}
