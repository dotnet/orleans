using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace UnitTests.General
{
    public static class GrainCallBootstrapTestConstants
    {
        public const int A = 2;
        public const int B = 3;
        public const long GrainId = 12345;
    }

    public class MockBootstrapProvider : IControllable, IBootstrapProvider
    {
        public enum Commands
        {
            Base = 100,
            InitCount = Base + 1,
            QueryName = Base + 2
        }

        private int initCount;

        public string Name { get; private set; }
        protected ILogger logger { get; private set; }

        public int InitCount
        {
            get { return initCount; }
        }

        public MockBootstrapProvider()
        {
#if DEBUG
            // Note: Can't use logger here because it is not initialized until the Init method is called.
            Console.WriteLine("Constructor - MockBootstrapProvider");
#endif
        }

        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            logger = providerRuntime.ServiceProvider.GetRequiredService<ILogger<MockBootstrapProvider>>();
            logger.Info("Init Name={0}", Name);
            Interlocked.Increment(ref initCount);
            return Task.CompletedTask;
        }

        public Task Close()
        {
            logger.Info("Close Name={0}", Name);
            return Task.CompletedTask;
        }

        public virtual Task<object> ExecuteCommand(int command, object arg)
        {
            switch ((Commands) command)
            {
                case Commands.InitCount:
                    return Task.FromResult<object>(this.InitCount);
                case Commands.QueryName:
                    return Task.FromResult<object>(this.Name);
                default:
                    return Task.FromResult<object>(true);
            }
        }
    }

    public class GrainCallBootstrapper : MockBootstrapProvider
    {
        public GrainCallBootstrapper()
        {
#if DEBUG
            // Note: Can't use logger here because it is not initialized until the Init method is called.
            Console.WriteLine("Constructor - {0}", GetType().Name);
#endif
        }

        public override async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            await base.Init(name, providerRuntime, config);

            long grainId = GrainCallBootstrapTestConstants.GrainId;
            int a = GrainCallBootstrapTestConstants.A;
            int b = GrainCallBootstrapTestConstants.B;
            ISimpleGrain grain = providerRuntime.GrainFactory.GetGrain<ISimpleGrain>(grainId, SimpleGrain.SimpleGrainNamePrefix);
            
            logger.Info("Setting A={0} on {1}", a, grainId);
            await grain.SetA(a);
            logger.Info("Setting B={0} on {1}", b, grainId);
            await grain.SetB(b);
            
            logger.Info("Getting AxB from {0}", grainId);
            int axb = await grain.GetAxB();
            logger.Info("Got AxB={0} from {1}", axb, grainId);
            
            int expected = a * b;
            int actual = axb;
            if (expected != actual) {
                throw new Exception(string.Format(
                    "Value returned to {0} by {1} should be {2} not {3}", 
                     GetType().Name, grainId, expected, actual));
            }
        }
    }

    public class LocalGrainInitBootstrapper : MockBootstrapProvider
    {
        public LocalGrainInitBootstrapper()
        {
#if DEBUG
            // Note: Can't use logger here because it is not initialized until the Init method is called.
            Console.WriteLine("Constructor - {0}", GetType().Name);
#endif
        }

        public override async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            await base.Init(name, providerRuntime, config);
            ILocalContentGrain grain = providerRuntime.GrainFactory.GetGrain<ILocalContentGrain>(Guid.NewGuid());
            // issue any grain call to activate this grain.
            await grain.Init();
            logger.Info("Finished Init Name={0}", name);
        }
    }

    public class ControllableBootstrapProvider : MockBootstrapProvider
    {
        public new enum Commands
        {
            Base = 200,
            EchoArg = Base + 1
        }

        private string serviceId;
        private string siloId;

        private static int idCounter;
        private readonly int myId;

        public ControllableBootstrapProvider()
        {
            myId = Interlocked.Increment(ref idCounter);
#if DEBUG
            // Note: Can't use logger here because it is not initialized until the Init method is called.
            Console.WriteLine("Constructor - {0} - instance id {1}", GetType().Name, myId);
#endif
        }

        public override async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            await base.Init(name, providerRuntime, config);
            serviceId = providerRuntime.ServiceId;
            siloId = providerRuntime.SiloIdentity;
            logger.Info("Finished Init instance id {0} on silo {1} in service {2}", myId, siloId, serviceId);
        }

        private object EchoArg(object arg)
        {
            logger.Info("ExecuteCommand {0} for instance id {1} on silo {2} with arg={3}", "EchoArg", myId, siloId, arg);
            logger.Info("Finished ExecuteCommand {0} for instance id {1} on silo {2}", "EchoArg", myId, siloId);
            return arg;
        }

        #region IControllable interface methods
        /// <summary>
        /// A function to execute a control command.
        /// </summary>
        /// <param name="command">A serial number of the command.</param>
        /// <param name="arg">An opaque command argument</param>
        public override Task<object> ExecuteCommand(int command, object arg)
        {
            switch ((Commands) command)
            {
                case Commands.EchoArg:
                    return Task.FromResult(EchoArg(arg));
                default:
                    return base.ExecuteCommand(command, arg);
            }
        }
        #endregion
    }
}
