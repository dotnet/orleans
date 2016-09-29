using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    public class EchoTaskGrainState
    {
        public int MyId { get; set; }
        public string LastEcho { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class EchoGrain : Grain<EchoTaskGrainState>, IEchoGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
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
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class EchoTaskGrain : Grain<EchoTaskGrainState>, IEchoTaskGrain
    {
        private  Logger logger;

        public Task<int> GetMyIdAsync() { return Task.FromResult(State.MyId); } 
        public Task<string> GetLastEchoAsync() { return Task.FromResult(State.LastEcho); }

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
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
            return TaskDone.Done;
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

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
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

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            var silos = await mgmtGrain.GetHosts();

            SiloAddress siloAddress = silos.Where(pair => !pair.Key.Equals(mySilo)).Select(pair => pair.Key).First();
            logger.Info("Sending Ping to remote silo {0}", siloAddress);

            var oracle = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipOracleId, siloAddress);

            await oracle.Ping(1);
            logger.Info("Ping reply received for {0}", siloAddress);
        }

        private ISiloControl GetSiloControlReference(SiloAddress silo)
        {
            return InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlId, silo);
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class BlockingEchoTaskGrain : Grain<EchoTaskGrainState>, IBlockingEchoTaskGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
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
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger();
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
