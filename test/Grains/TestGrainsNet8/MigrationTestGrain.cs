using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace Tester.AzureUtils.Migration.Grains
{
    /// <summary>
    /// mock grain interface for migration tests
    /// </summary>
    public interface ISimplePersistentMigrationGrain : ISimplePersistentGrain
    {
    }

    [Serializable]
    public class MigrationTestGrain_State
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    [StorageProvider(ProviderName = "migration1")]
    [Orleans.Persistence.Cosmos.GrainType("migrationtestgrain")]
    public class MigrationTestGrainStorage1 : MigrationTestGrain
    {
        public MigrationTestGrainStorage1(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }

    [StorageProvider(ProviderName = "migration2")]
    [Orleans.Persistence.Cosmos.GrainType("migrationtestgrain")]
    public class MigrationTestGrainStorage2 : MigrationTestGrain
    {
        public MigrationTestGrainStorage2(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    [Orleans.Persistence.Cosmos.GrainType("migrationtestgrain")]
    public class MigrationTestGrain : Grain<MigrationTestGrain_State>, ISimplePersistentMigrationGrain
    {
        private ILogger logger;
        private Guid version;

        public MigrationTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            logger.Info("Activate.");
            version = Guid.NewGuid();
            return base.OnActivateAsync();
        }
        public Task SetA(int a)
        {
            State.A = a;
            return WriteStateAsync();
        }

        public Task SetA(int a, bool deactivate)
        {
            if (deactivate)
                DeactivateOnIdle();
            return SetA(a);
        }

        public Task SetB(int b)
        {
            State.B = b;
            return WriteStateAsync();
        }

        public Task IncrementA()
        {
            State.A++;
            return WriteStateAsync();
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(State.A * State.B);
        }

        public Task<int> GetAxB(int a, int b)
        {
            return Task.FromResult(a * b);
        }

        public Task<int> GetA()
        {
            return Task.FromResult(State.A);
        }

        public Task<Guid> GetVersion()
        {
            return Task.FromResult(version);
        }

        public Task<object> GetRequestContext()
        {
            var info = RequestContext.Get("GrainInfo");
            return Task.FromResult(info);
        }

        public Task SetRequestContext(int data)
        {
            RequestContext.Set("GrainInfo", data);
            return Task.CompletedTask;
        }
    }
}
