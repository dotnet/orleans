using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ClientObserverRegistrar : SystemTarget, IClientObserverRegistrar, ILifecycleParticipant<ISiloLifecycle>
    {
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MIN = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MAX = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromSeconds(1);

        private readonly ILocalGrainDirectory grainDirectory;
        private readonly SiloAddress myAddress;
        private readonly OrleansTaskScheduler scheduler;
        private readonly IClusterMembershipService clusterMembershipService;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly ILogger logger;
        private readonly IAsyncTimer refreshTimer;
        private readonly CancellationTokenSource shutdownCancellation = new CancellationTokenSource();
        private HostedClient hostedClient;
        private Gateway gateway;
        private Task clientRefreshLoopTask;
        
        public ClientObserverRegistrar(
            ILocalSiloDetails siloDetails,
            ILocalGrainDirectory grainDirectory,
            OrleansTaskScheduler scheduler,
            IOptions<SiloMessagingOptions> messagingOptions,
            ILoggerFactory loggerFactory,
            IClusterMembershipService clusterMembershipService,
            IAsyncTimerFactory timerFactory)
            : base(Constants.ClientObserverRegistrarType, siloDetails.SiloAddress, loggerFactory)
        {
            this.grainDirectory = grainDirectory;
            this.myAddress = siloDetails.SiloAddress;
            this.scheduler = scheduler;
            this.clusterMembershipService = clusterMembershipService;
            this.messagingOptions = messagingOptions.Value;
            this.logger = loggerFactory.CreateLogger<ClientObserverRegistrar>();
            this.refreshTimer = timerFactory.Create(this.messagingOptions.ClientRegistrationRefresh, "ClientObserverRegistrar.ClientRefreshTimer");
        }

        internal void SetHostedClient(HostedClient client)
        {
            this.hostedClient = client;
            if (client != null)
            {
                this.scheduler.QueueAction(Start, this);
            }
        }

        internal void SetGateway(Gateway gateway)
        {
            this.gateway = gateway;

            // Only start ClientRefreshTimer if this silo has a gateway.
            // Need to start the timer in the system target context.
            scheduler.QueueAction(Start, this);
        }

        private void Start()
        {
            if (clientRefreshLoopTask is object)
            {
                return;
            }

            clientRefreshLoopTask = RunClientRefreshLoop();
            if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Client registrar service started successfully."); }
        }

        private async Task RunClientRefreshLoop()
        {
            var membershipUpdates = this.clusterMembershipService.MembershipUpdates.GetAsyncEnumerator(this.shutdownCancellation.Token);

            Task<bool> membershipTask = null;
            Task<bool> timerTask = this.refreshTimer.NextTick(new SafeRandom().NextTimeSpan(this.messagingOptions.ClientRegistrationRefresh));

            while (true)
            {
                membershipTask ??= membershipUpdates.MoveNextAsync().AsTask();
                timerTask ??= this.refreshTimer.NextTick();

                // Wait for either of the tasks to complete.
                await Task.WhenAny(membershipTask, timerTask);

                if (timerTask.IsCompleted)
                {
                    if (!await timerTask)
                    {
                        break;
                    }

                    timerTask = null;
                }

                if (membershipTask.IsCompleted)
                {
                    if (!await membershipTask)
                    {
                        break;
                    }

                    membershipTask = null;
                }

                await OnRefreshClients();
            }
        }

        internal void ClientAdded(ClientGrainId clientId)
        {
            // Use a ActivationId that is hashed from clientId, and not random ActivationId.
            // That way, when we refresh it in the directiry, it's the same one.
            var addr = GetClientActivationAddress(clientId);
            scheduler.QueueTask(
                () => ExecuteWithRetries(() => grainDirectory.RegisterAsync(addr, singleActivation:false), ErrorCode.ClientRegistrarFailedToRegister, String.Format("Directory.RegisterAsync {0} failed.", addr)),
                this).Ignore();
        }

        internal void ClientDropped(ClientGrainId clientId)
        {
            var addr = GetClientActivationAddress(clientId);
            scheduler.QueueTask(
                () => ExecuteWithRetries(() => grainDirectory.UnregisterAsync(addr, Orleans.GrainDirectory.UnregistrationCause.Force), ErrorCode.ClientRegistrarFailedToUnregister, String.Format("Directory.UnRegisterAsync {0} failed.", addr)), 
                this).Ignore();
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
                    this.messagingOptions.ClientRegistrationRefresh, // "maxExecutionTime"
                    new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP)); // how long to wait between error retries
            }
            catch (Exception exc)
            {
                logger.Error(errorCode, errorStr, exc);
            }
        }

        private async Task OnRefreshClients()
        {
            try
            {
                List<ClientGrainId> clients = null;
                if (this.gateway is Gateway gw)
                {
                    var gatewayClients = gw.GetConnectedClients();
                    clients = new List<ClientGrainId>(gatewayClients.Count + 1);
                    clients.AddRange(gatewayClients);
                }

                if (this.hostedClient?.ClientId is ClientGrainId hostedClientId)
                {
                    clients ??= new List<ClientGrainId>(1);
                    clients.Add(hostedClientId);
                }

                if (clients is null)
                {
                    return;
                }

                var tasks = new List<Task>();
                foreach (ClientGrainId clientId in clients)
                {
                    var addr = GetClientActivationAddress(clientId);
                    Task task = grainDirectory.RegisterAsync(addr, singleActivation: false).
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

        private ActivationAddress GetClientActivationAddress(ClientGrainId clientId)
        {
            // Need to pick a unique deterministic ActivationId for this client.
            // We store it in the grain directory and there for every GrainId we use ActivationId as a key
            // so every GW needs to behave as a different "activation" with a different ActivationId (its not enough that they have different SiloAddress)
            string stringToHash = clientId.ToString() + myAddress.Endpoint + myAddress.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Guid hash = Utils.CalculateGuidHash(stringToHash);
            var activationId = ActivationId.GetActivationId(UniqueKey.NewKey(hash));
            return ActivationAddress.GetAddress(myAddress, clientId.GrainId, activationId);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClientObserverRegistrar),
                ServiceLifecycleStage.RuntimeServices,
                _ => Task.CompletedTask,
                async ct =>
                {
                    shutdownCancellation.Cancel();
                    refreshTimer?.Dispose();

                    if (clientRefreshLoopTask is Task task)
                    {
                        await Task.WhenAny(ct.WhenCancelled(), task);
                    }
                });
        }
    }
}


