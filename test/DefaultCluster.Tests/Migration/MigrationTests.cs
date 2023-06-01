using Orleans.Core.Internal;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class MigrationTests : HostedTestClusterEnsureDefaultStarted
    {
        public MigrationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

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
                    await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();
                    newHost = await grain.GetHostAddress();
                } while (newHost == originalHost);

                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);
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
}