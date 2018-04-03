using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.Timers;
using Orleans.Storage;

namespace Orleans.Runtime
{
    internal class GrainRuntime : IGrainRuntime
    {
        private readonly ISiloRuntimeClient runtimeClient;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;

        public GrainRuntime(
            IOptions<ClusterOptions> clusterOptions,
            ILocalSiloDetails localSiloDetails,
            IGrainFactory grainFactory,
            ITimerRegistry timerRegistry,
            IReminderRegistry reminderRegistry,
            IServiceProvider serviceProvider,
            ISiloRuntimeClient runtimeClient,
            ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<GrainRuntime>();
            this.runtimeClient = runtimeClient;
            ServiceId = clusterOptions.Value.ServiceId;
            SiloAddress = localSiloDetails.SiloAddress;
            SiloIdentity = SiloAddress.ToLongString();
            GrainFactory = grainFactory;
            TimerRegistry = timerRegistry;
            ReminderRegistry = reminderRegistry;
            ServiceProvider = serviceProvider;
            this.loggerFactory = loggerFactory;
        }

        public string ServiceId { get; }

        public string SiloIdentity { get; }

        public SiloAddress SiloAddress { get; }

        public IGrainFactory GrainFactory { get; }
        
        public ITimerRegistry TimerRegistry { get; }
        
        public IReminderRegistry ReminderRegistry { get; }

        public IServiceProvider ServiceProvider { get; }

        public void DeactivateOnIdle(Grain grain)
        {
            this.runtimeClient.DeactivateOnIdle(grain.Data.ActivationId);
        }

        public void DelayDeactivation(Grain grain, TimeSpan timeSpan)
        {
            grain.Data.DelayDeactivation(timeSpan);
        }

        public IStorage<TGrainState> GetStorage<TGrainState>(Grain grain) where TGrainState : new()
        {
            IGrainStorage grainStorage = grain.GetGrainStorage(ServiceProvider);
            string grainTypeName = grain.GetType().FullName;
            return new StateStorageBridge<TGrainState>(grainTypeName, grain.GrainReference, grainStorage, this.loggerFactory);
        }
    }
}