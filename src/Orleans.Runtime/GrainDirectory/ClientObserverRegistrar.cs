using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ClientObserverRegistrar : SystemTarget, IClientObserverRegistrar, ISiloStatusListener
    {
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MIN = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MAX = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromSeconds(1);

        private readonly ILocalGrainDirectory grainDirectory;
        private readonly SiloAddress myAddress;
        private readonly OrleansTaskScheduler scheduler;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly ILogger logger;
        private IHostedClient hostedClient;
        private Gateway gateway;
        private IDisposable refreshTimer;
        
        public ClientObserverRegistrar(
            ILocalSiloDetails siloDetails,
            ILocalGrainDirectory dir,
            OrleansTaskScheduler scheduler,
            IOptions<SiloMessagingOptions> messagingOptions,
            ILoggerFactory loggerFactory)
            : base(Constants.ClientObserverRegistrarId, siloDetails.SiloAddress, loggerFactory)
        {
            grainDirectory = dir;
            myAddress = siloDetails.SiloAddress;
            this.scheduler = scheduler;
            this.messagingOptions = messagingOptions.Value;
            logger = loggerFactory.CreateLogger<ClientObserverRegistrar>();
        }

        internal void SetHostedClient(IHostedClient client)
        {
            this.hostedClient = client;
            if (client != null)
            {
                this.scheduler.QueueAction(Start, this.SchedulingContext).Ignore();
            }
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
            if (this.refreshTimer != null) return;
            var random = new SafeRandom();
            var randomOffset = random.NextTimeSpan(this.messagingOptions.ClientRegistrationRefresh);
            this.refreshTimer = this.RegisterTimer(
                this.OnClientRefreshTimer,
                null,
                randomOffset,
                this.messagingOptions.ClientRegistrationRefresh,
                "ClientObserverRegistrar.ClientRefreshTimer");
            if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Client registrar service started successfully."); }
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
                    this.messagingOptions.ClientRegistrationRefresh, // "maxExecutionTime"
                    new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP)); // how long to wait between error retries
            }
            catch (Exception exc)
            {
                logger.Error(errorCode, errorStr, exc);
            }
        }

        private async Task OnClientRefreshTimer(object data)
        {
            try
            {
                var clients = new List<GrainId>();
                if (this.gateway != null) clients.AddRange(gateway.GetConnectedClients());
                var hostedClientId = this.hostedClient?.ClientId;
                if (hostedClientId != null) clients.Add(hostedClientId);

                var tasks = new List<Task>();
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
            string stringToHash = clientId.ToParsableString() + myAddress.Endpoint + myAddress.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Guid hash = Utils.CalculateGuidHash(stringToHash);
            UniqueKey key = UniqueKey.NewKey(hash);
            return ActivationAddress.GetAddress(myAddress, clientId, ActivationId.GetActivationId(key));
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (status != SiloStatus.Dead)
                return;

            if (Equals(updatedSilo, this.Silo))
                refreshTimer?.Dispose();

            scheduler.QueueTask(() => OnClientRefreshTimer(null), SchedulingContext).Ignore();
        }
    }
}


