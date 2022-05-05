using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Utilities;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Grains
{
    [Serializable]
    [GenerateSerializer]
    public class EchoTaskGrainState
    {
        [Id(0)]
        public int MyId { get; set; }
        [Id(1)]
        public string LastEcho { get; set; }
    }

    [CollectionAgeLimit(Minutes = 5)]
    [StorageProvider(ProviderName = "MemoryStore")]
    public class EchoGrain : Grain<EchoTaskGrainState>, IEchoGrain
    {
        private ILogger logger;

        public EchoGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info(GetType().FullName + " created");
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<string> GetLastEcho()
        {
            return Task.FromResult(State.LastEcho);
        }

        public Task<string> Echo(string data)
        {
            logger.Info("IEchoGrain.Echo=" + data);
            State.LastEcho = data;
            return Task.FromResult(data);
        }

        public Task<string> EchoError(string data)
        {
            logger.Info("IEchoGrain.EchoError=" + data);
            State.LastEcho = data;
            throw new Exception(data);
        }

        public Task<DateTime?> EchoNullable(DateTime? value) => Task.FromResult(value);
    }

    [CollectionAgeLimit(Minutes = 5)]
    [StorageProvider(ProviderName = "MemoryStore")]
    internal class EchoTaskGrain : Grain<EchoTaskGrainState>, IEchoTaskGrain, IDebuggerHelperTestGrain
    {
        private readonly IInternalGrainFactory internalGrainFactory;
        private readonly IGrainContext _grainContext;

        public EchoTaskGrain(IInternalGrainFactory internalGrainFactory, ILogger<EchoTaskGrain> logger, IGrainContext grainContext)
        {
            this.internalGrainFactory = internalGrainFactory;
            _grainContext = grainContext;
        }

        public Task<int> GetMyIdAsync() { return Task.FromResult(State.MyId); }
        public Task<string> GetLastEchoAsync() { return Task.FromResult(State.LastEcho); }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<string> EchoAsync(string data)
        {
            State.LastEcho = data;
            return Task.FromResult(data);
        }

        public Task<string> EchoErrorAsync(string data)
        {
            State.LastEcho = data;
            throw new Exception(data);
        }

        private Task<string> EchoErrorAV(string data)
        {
            State.LastEcho = data;
            throw new Exception(data);
        }

        public async Task<string> AwaitMethodErrorAsync(string data)
        {
            return await EchoErrorAsync(data);
        }

        public async Task<string> AwaitAVMethodErrorAsync(string data)
        {
            return await EchoErrorAV(data);
        }

        public async Task<string> AwaitAVGrainCallErrorAsync(string data)
        {
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());
            return await avGrain.EchoError(data);
        }

        public Task<int> BlockingCallTimeoutAsync(TimeSpan delay)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Thread.Sleep(delay);
            throw new InvalidOperationException("Timeout should have been returned to caller before " + delay);
        }

        public Task PingAsync()
        {
            return Task.CompletedTask;
        }

        public Task PingLocalSiloAsync()
        {
            SiloAddress mySilo = _grainContext.Address.SiloAddress;
            return GetSiloControlReference(mySilo).Ping("PingLocal");
        }

        public Task PingRemoteSiloAsync(SiloAddress siloAddress)
        {
            return GetSiloControlReference(siloAddress).Ping("PingRemote");
        }

        public async Task PingOtherSiloAsync()
        {
            SiloAddress mySilo = _grainContext.Address.SiloAddress;

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);
            var silos = await mgmtGrain.GetHosts();

            SiloAddress siloAddress = silos.Where(pair => !pair.Key.Equals(mySilo)).Select(pair => pair.Key).First();

            await GetSiloControlReference(siloAddress).Ping("PingOtherSilo-" + siloAddress);
        }

        public async Task PingClusterMemberAsync()
        {
            SiloAddress mySilo = _grainContext.Address.SiloAddress;

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);
            var silos = await mgmtGrain.GetHosts();

            SiloAddress siloAddress = silos.Where(pair => !pair.Key.Equals(mySilo)).Select(pair => pair.Key).First();

            var oracle = this.internalGrainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, siloAddress);

            await oracle.Ping(1);
        }

        private ISiloControl GetSiloControlReference(SiloAddress silo)
        {
            return this.internalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo);
        }

        public Task OrleansDebuggerHelper_GetGrainInstance_Test()
        {
            var result = OrleansDebuggerHelper.GetGrainInstance(null);
            Assert.Null(result);

            result = OrleansDebuggerHelper.GetGrainInstance(this);
            Assert.Same(this, result);

            result = OrleansDebuggerHelper.GetGrainInstance(this.AsReference<IDebuggerHelperTestGrain>());
            Assert.Same(this, result);

            result = OrleansDebuggerHelper.GetGrainInstance(this.GrainFactory.GetGrain<IEchoGrain>(Guid.NewGuid()));
            Assert.Null(result);

            return Task.CompletedTask;
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class BlockingEchoTaskGrain : Grain<EchoTaskGrainState>, IBlockingEchoTaskGrain
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<int> GetMyId()
        {
            return Task.FromResult(State.MyId);
        }

        public Task<string> GetLastEcho()
        {
            return Task.FromResult(State.LastEcho);
        }

        public Task<string> Echo(string data)
        {
            State.LastEcho = data;
            var result = Task.FromResult(data);
            return result;
        }

        public async Task<string> CallMethodTask_Await(string data)
        {
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());
            var result = await avGrain.EchoAsync(data);
            return result;
        }

        public async Task<string> CallMethodAV_Await(string data)
        {
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());
            var result = await avGrain.Echo(data);
            return result;
        }

        #pragma warning disable 1998
        public async Task<string> CallMethodTask_Block(string data)
        {
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.EchoAsync(data).Result;

            return result;
        }
        #pragma warning restore 1998

        #pragma warning disable 1998
        public async Task<string> CallMethodAV_Block(string data)
        {
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.Echo(data).Result;

            return result;
        }
        #pragma warning restore 1998
    }

    [Reentrant]
    [StorageProvider(ProviderName = "MemoryStore")]
    public class ReentrantBlockingEchoTaskGrain : Grain<EchoTaskGrainState>, IReentrantBlockingEchoTaskGrain
    {
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<int> GetMyId()
        {
            return Task.FromResult(State.MyId);
        }

        public Task<string> GetLastEcho()
        {
            return Task.FromResult(State.LastEcho);
        }

        public Task<string> Echo(string data)
        {
            State.LastEcho = data;
            var result = Task.FromResult(data);
            return result;
        }

        public async Task<string> CallMethodTask_Await(string data)
        {
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());
            var result = await avGrain.EchoAsync(data);
            return result;
        }

        public async Task<string> CallMethodAV_Await(string data)
        {
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());
            var result = await avGrain.Echo(data);
            return result;
        }

#pragma warning disable 1998
        public async Task<string> CallMethodTask_Block(string data)
        {
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.EchoAsync(data).Result;

            return result;
        }
#pragma warning restore 1998

#pragma warning disable 1998
        public async Task<string> CallMethodAV_Block(string data)
        {
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.Echo(data).Result;

            return result;
        }
#pragma warning restore 1998
    }
}
