using System;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;


namespace Orleans.Runtime
{
    internal class LocalReminderServiceFactory
    {
        private readonly IReminderTable reminderTable;
        private readonly Logger logger;

        public LocalReminderServiceFactory(IReminderTable reminderTable)
        {
            this.reminderTable = reminderTable;
            logger = LogManager.GetLogger("ReminderFactory", LoggerType.Runtime);
        }

        internal IReminderService CreateReminderService(
            Silo silo,
            TimeSpan iniTimeSpan,
            ISiloRuntimeClient runtimeClient)
        {
            logger.Info(
                "Creating reminder grain service for type={0}", this.reminderTable.GetType());

            // GrainInterfaceMap only holds IGrain types, not ISystemTarget types, so resolved via Orleans.CodeGeneration.
            // Resolve this before merge.
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(typeof(IReminderService));
            var grainId = GrainId.GetGrainServiceGrainId(0, typeCode);

            return new LocalReminderService(silo, grainId, this.reminderTable, silo.GlobalConfig, iniTimeSpan);
        }
    }
}