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

    [StorageProvider(ProviderName = "MemoryStore")]
    public class EchoGrain : Grain<EchoTaskGrainState>, IEchoGrain
    {
        private ILogger logger;

        public EchoGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            logger.Info(GetType().FullName + " created");
            return base.OnActivateAsync();
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

    [StorageProvider(ProviderName = "MemoryStore")]
    internal class EchoTaskGrain : Grain<EchoTaskGrainState>, IEchoTaskGrain, IDebuggerHelperTestGrain
    {
        private readonly IInternalGrainFactory internalGrainFactory;
        private ILogger logger;

        public EchoTaskGrain(IInternalGrainFactory internalGrainFactory, ILogger<EchoTaskGrain> logger)
        {
            this.internalGrainFactory = internalGrainFactory;
            this.logger = logger;
        }

        public Task<int> GetMyIdAsync() { return Task.FromResult(State.MyId); } 
        public Task<string> GetLastEchoAsync() { return Task.FromResult(State.LastEcho); }

        public override Task OnActivateAsync()
        {
            logger.Info(GetType().FullName + " created");
            return base.OnActivateAsync();
        }

        public Task<string> EchoAsync(string data)
        {
            logger.Info("IEchoGrainAsync.Echo=" + data);
            State.LastEcho = data;
            return Task.FromResult(data);
        }

        public Task<string> EchoErrorAsync(string data)
        {
            logger.Info("IEchoGrainAsync.EchoError=" + data);
            State.LastEcho = data;
            throw new Exception(data);
        }

        private Task<string> EchoErrorAV(string data)
        {
            logger.Info("IEchoGrainAsync.EchoErrorAV=" + data);
            State.LastEcho = data;
            throw new Exception(data);
        }

        public async Task<string> AwaitMethodErrorAsync(string data)
        {
            logger.Info("IEchoGrainAsync.CallMethodErrorAsync=" + data);
            return await EchoErrorAsync(data);
        }

        public async Task<string> AwaitAVMethodErrorAsync(string data)
        {
            logger.Info("IEchoGrainAsync.CallMethodErrorAsync=" + data);
            return await EchoErrorAV(data);
        }

        public async Task<string> AwaitAVGrainCallErrorAsync(string data)
        {
            logger.Info("IEchoGrainAsync.AwaitAVGrainErrorAsync=" + data);
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());
            return await avGrain.EchoError(data);
        }

        public Task<int> BlockingCallTimeoutAsync(TimeSpan delay)
        {
            logger.Info("IEchoGrainAsync.BlockingCallTimeout Delay={0}", delay);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Thread.Sleep(delay);
            logger.Info("IEchoGrainAsync.BlockingCallTimeout Awoke from sleep after {0}", sw.Elapsed);
            throw new InvalidOperationException("Timeout should have been returned to caller before " + delay);
        }

        public Task PingAsync()
        {
            logger.Info("IEchoGrainAsync.Ping");
            return Task.CompletedTask;
        }

        public Task PingLocalSiloAsync()
        {
            logger.Info("IEchoGrainAsync.PingLocal");
            SiloAddress mySilo = Data.Address.Silo;
            return GetSiloControlReference(mySilo).Ping("PingLocal");
        }

        public Task PingRemoteSiloAsync(SiloAddress siloAddress)
        {
            logger.Info("IEchoGrainAsync.PingRemote");
            return GetSiloControlReference(siloAddress).Ping("PingRemote");
        }

        public async Task PingOtherSiloAsync()
        {
            logger.Info("IEchoGrainAsync.PingOtherSilo");
            SiloAddress mySilo = Data.Address.Silo;

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);
            var silos = await mgmtGrain.GetHosts();

            SiloAddress siloAddress = silos.Where(pair => !pair.Key.Equals(mySilo)).Select(pair => pair.Key).First();
            logger.Info("Sending Ping to remote silo {0}", siloAddress);

            await GetSiloControlReference(siloAddress).Ping("PingOtherSilo-" + siloAddress);
            logger.Info("Ping reply received for {0}", siloAddress);
        }

        public async Task PingClusterMemberAsync()
        {
            logger.Info("IEchoGrainAsync.PingClusterMemberAsync");
            SiloAddress mySilo = Data.Address.Silo;

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);
            var silos = await mgmtGrain.GetHosts();

            SiloAddress siloAddress = silos.Where(pair => !pair.Key.Equals(mySilo)).Select(pair => pair.Key).First();
            logger.Info("Sending Ping to remote silo {0}", siloAddress);

            var oracle = this.internalGrainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, siloAddress);

            await oracle.Ping(1);
            logger.Info("Ping reply received for {0}", siloAddress);
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
        private ILogger logger;

        public BlockingEchoTaskGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            logger.Info(GetType().FullName + " created");
            return base.OnActivateAsync();
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
            string name = GetType().Name + ".Echo";

            logger.Info(name + " Data=" + data);
            State.LastEcho = data;
            var result = Task.FromResult(data);
            logger.Info(name + " Result=" + result);
            return result;
        }

        public async Task<string> CallMethodTask_Await(string data)
        {
            string name = GetType().Name + ".CallMethodTask_Await";

            logger.Info(name + " Data=" + data);
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());
            var result = await avGrain.EchoAsync(data);
            logger.Info(name + " Result=" + result);
            return result;
        }

        public async Task<string> CallMethodAV_Await(string data)
        {
            string name = GetType().Name + ".CallMethodAV_Await";

            logger.Info(name + " Data=" + data);
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());
            var result = await avGrain.Echo(data);
            logger.Info(name + " Result=" + result);
            return result;
        }

        #pragma warning disable 1998
        public async Task<string> CallMethodTask_Block(string data)
        {
            string name = GetType().Name + ".CallMethodTask_Block";

            logger.Info(name + " Data=" + data);
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.EchoAsync(data).Result;

            logger.Info(name + " Result=" + result);
            return result;
        }
        #pragma warning restore 1998

        #pragma warning disable 1998
        public async Task<string> CallMethodAV_Block(string data)
        {
            string name = GetType().Name + ".CallMethodAV_Block";

            logger.Info(name + " Data=" + data);
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.Echo(data).Result;
            
            logger.Info(name + " Result=" + result);
            return result;
        }
        #pragma warning restore 1998
    }

    [Reentrant]
    [StorageProvider(ProviderName = "MemoryStore")]
    public class ReentrantBlockingEchoTaskGrain : Grain<EchoTaskGrainState>, IReentrantBlockingEchoTaskGrain
    {
        private ILogger logger;

        public ReentrantBlockingEchoTaskGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            logger.Info(GetType().FullName + " created");
            return base.OnActivateAsync();
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
            string name = GetType().Name + ".Echo";

            logger.Info(name + " Data=" + data);
            State.LastEcho = data;
            var result = Task.FromResult(data);
            logger.Info(name + " Result=" + result);
            return result;
        }

        public async Task<string> CallMethodTask_Await(string data)
        {
            string name = GetType().Name + ".CallMethodTask_Await";

            logger.Info(name + " Data=" + data);
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());
            var result = await avGrain.EchoAsync(data);
            logger.Info(name + " Result=" + result);
            return result;
        }

        public async Task<string> CallMethodAV_Await(string data)
        {
            string name = GetType().Name + ".CallMethodAV_Await";

            logger.Info(name + " Data=" + data);
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());
            var result = await avGrain.Echo(data);
            logger.Info(name + " Result=" + result);
            return result;
        }

#pragma warning disable 1998
        public async Task<string> CallMethodTask_Block(string data)
        {
            string name = GetType().Name + ".CallMethodTask_Block";

            logger.Info(name + " Data=" + data);
            IEchoTaskGrain avGrain = GrainFactory.GetGrain<IEchoTaskGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.EchoAsync(data).Result;

            logger.Info(name + " Result=" + result);
            return result;
        }
#pragma warning restore 1998

#pragma warning disable 1998
        public async Task<string> CallMethodAV_Block(string data)
        {
            string name = GetType().Name + ".CallMethodAV_Block";

            logger.Info(name + " Data=" + data);
            IEchoGrain avGrain = GrainFactory.GetGrain<IEchoGrain>(this.GetPrimaryKey());

            // Note: We deliberately use .Result here in this test case to block current executing thread
            var result = avGrain.Echo(data).Result;
            
            logger.Info(name + " Result=" + result);
            return result;
        }
#pragma warning restore 1998
    }
}
