using System;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime
{
    internal class LocalReminderServiceFactory
    {
        private readonly TraceLogger logger;

        internal LocalReminderServiceFactory()
        {
            logger = TraceLogger.GetLogger("ReminderFactory", TraceLogger.LoggerType.Runtime);
        }

        internal IReminderService CreateReminderService(Silo silo, IGrainFactory grainFactory, TimeSpan iniTimeSpan)
        {
            var reminderServiceType = silo.GlobalConfig.ReminderServiceType;
            logger.Info("Creating reminder system target for type={0}", Enum.GetName(typeof(GlobalConfiguration.ReminderServiceProviderType), reminderServiceType));

            ReminderTable.Initialize(silo, grainFactory, silo.GlobalConfig.ReminderTableAssembly);
            return new LocalReminderService(
                silo.SiloAddress, 
                Constants.ReminderServiceId, 
                silo.RingProvider, 
                silo.LocalScheduler, 
                ReminderTable.Singleton, 
                silo.GlobalConfig,
                iniTimeSpan);
        }
    }
}
