using System;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;


namespace Orleans.Runtime
{
    internal class LocalReminderServiceFactory
    {
        private readonly Logger logger;

        internal LocalReminderServiceFactory()
        {
            logger = LogManager.GetLogger("ReminderFactory", LoggerType.Runtime);
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
