using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ClientObserverRegistrar : SystemTarget, IClientObserverRegistrar
    {
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MIN = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MAX = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromSeconds(1);

        private readonly ILocalGrainDirectory grainDirectory;
        private readonly SiloAddress myAddress;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ClusterConfiguration orleansConfig;
        private readonly Logger logger;
        private GrainTimer clientRefreshTimer;
        private Gateway gateway;
       

        internal ClientObserverRegistrar(SiloAddress myAddr, ILocalGrainDirectory dir, OrleansTaskScheduler scheduler, ClusterConfiguration config)
            : base(Constants.ClientObserverRegistrarId, myAddr)
        {
            grainDirectory = dir;
            myAddress = myAddr;
            this.scheduler = scheduler;
            orleansConfig = config;
            logger = LogManager.GetLogger(typeof(ClientObserverRegistrar).Name);
        }

        internal void SetGateway(Gateway gateway)
        {
            this.gateway = gateway;
            // Only start ClientRefreshTimer if this silo has a gateway.
            // Need to start the timer in the system target context.
            scheduler.QueueAction(Start, this.SchedulingContext).Ignore();
        }

        private void Start()
        {
            var random = new SafeRandom();
            var randomOffset = random.NextTimeSpan(orleansConfig.Globals.ClientRegistrationRefresh);
            clientRefreshTimer = GrainTimer.FromTaskCallback(
                    OnClientRefreshTimer, 
                    null, 
                    randomOffset, 
                    orleansConfig.Globals.ClientRegistrationRefresh, 
                    "ClientObserverRegistrar.ClientRefreshTimer");
            clientRefreshTimer.Start();
            if (logger.IsVerbose) { logger.Verbose("Client registrar service started successfully."); }
        }

        internal void ClientAdded(GrainId clientId)
        {
            // Use a ActivationId that is hashed from clientId, and not random ActivationId.
            // That way, when we refresh it in the directiry, it's the same one.
            var addr = GetClientActivationAddress(clientId);
            scheduler.QueueTask(
                () => ExecuteWithRetries(() => grainDirectory.RegisterAsync(addr, singleActivation:false), ErrorCode.ClientRegistrarFailedToRegister, String.Format("Directory.RegisterAsync {0} failed.", addr)),
                this.SchedulingContext)
                        .Ignore();
        }

        internal void ClientDropped(GrainId clientId)
        {
            var addr = GetClientActivationAddress(clientId);
            scheduler.QueueTask(
                () => ExecuteWithRetries(() => grainDirectory.UnregisterAsync(addr, Orleans.GrainDirectory.UnregistrationCause.Force), ErrorCode.ClientRegistrarFailedToUnregister, String.Format("Directory.UnRegisterAsync {0} failed.", addr)), 
                this.SchedulingContext)
                        .Ignore();
        }

        private async Task ExecuteWithRetries(Func<Task> functionToExecute, ErrorCode errorCode, string errorStr)
        {
            try
            {
                // Try to register/unregister the client in the directory.
                // If failed, keep retrying with exponentially increasing time intervals in between, until:
                // either succeeds or max time of orleansConfig.Globals.ClientRegistrationRefresh has reached.
                // If failed to register after that time, it will be retried further on by clientRefreshTimer.
                // In the unregsiter case just drop it. At the worst, where will be garbage in the directory.
                await AsyncExecutorWithRetries.ExecuteWithRetries(
                    _ =>
                    {
                        return functionToExecute();
                    },
                    AsyncExecutorWithRetries.INFINITE_RETRIES, // Do not limit the number of on-error retries, control it via "maxExecutionTime"
                    (exc, i) => true, // Retry on all errors.         
                    orleansConfig.Globals.ClientRegistrationRefresh, // "maxExecutionTime"
                    new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP)); // how long to wait between error retries
            }
            catch (Exception exc)
            {
                logger.Error(errorCode, errorStr, exc);
            }
        }

        private async Task OnClientRefreshTimer(object data)
        {
            if (gateway == null) return;
            try
            {
                ICollection<GrainId> clients = gateway.GetConnectedClients().ToList();
                List<Task> tasks = new List<Task>();
                foreach (GrainId clientId in clients)
                {
                    var addr = GetClientActivationAddress(clientId);
                    Task task = grainDirectory.RegisterAsync(addr, singleActivation:false).
                        LogException(logger, ErrorCode.ClientRegistrarFailedToRegister_2, String.Format("Directory.RegisterAsync {0} failed.", addr));
                    tasks.Add(task);
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.ClientRegistrarTimerFailed, 
                    String.Format("OnClientRefreshTimer has thrown an exceptions."), exc);
            }
        }

        private ActivationAddress GetClientActivationAddress(GrainId clientId)
        {
            // Need to pick a unique deterministic ActivationId for this client.
            // We store it in the grain directory and there for every GrainId we use ActivationId as a key
            // so every GW needs to behave as a different "activation" with a different ActivationId (its not enough that they have different SiloAddress)
            return ActivationAddress.GetAddress(myAddress, clientId, ActivationId.GetClientGWActivation(clientId, myAddress));
        }
     }
}


