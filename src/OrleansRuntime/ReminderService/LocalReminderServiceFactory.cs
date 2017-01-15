using System;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;


namespace Orleans.Runtime
{
    internal class LocalReminderServiceFactory
    {
        private readonly Logger logger;

        public LocalReminderServiceFactory()
        {
            logger = LogManager.GetLogger("ReminderFactory", LoggerType.Runtime);
        }

        internal IReminderService CreateReminderService(Silo silo, IGrainFactory grainFactory, TimeSpan iniTimeSpan, ISiloRuntimeClient runtimeClient)
        {
            var reminderServiceType = silo.GlobalConfig.ReminderServiceType;
            logger.Info("Creating reminder grain service for type={0}", Enum.GetName(typeof(GlobalConfiguration.ReminderServiceProviderType), reminderServiceType));

            // GrainInterfaceMap only holds IGrain types, not ISystemTarget types, so resolved via Orleans.CodeGeneration.
            // Resolve this before merge.
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(typeof(IReminderService));
            var grainId = GrainId.GetGrainServiceGrainId(0, typeCode);

            ReminderTable.Initialize(silo, grainFactory, silo.GlobalConfig.ReminderTableAssembly);
            return new LocalReminderService(
                silo,
                grainId, 
                ReminderTable.Singleton, 
                silo.GlobalConfig,
                iniTimeSpan);
        }
    }
}
