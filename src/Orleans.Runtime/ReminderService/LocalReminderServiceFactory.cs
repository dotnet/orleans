using System;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;


namespace Orleans.Runtime
{
    internal class LocalReminderServiceFactory
    {
        private readonly IReminderTable reminderTable;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        public LocalReminderServiceFactory(IReminderTable reminderTable, ILoggerFactory loggerFactory)
        {
            this.reminderTable = reminderTable;
            logger = loggerFactory.CreateLogger<LocalReminderServiceFactory>();
            this.loggerFactory = loggerFactory;
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

            return new LocalReminderService(silo, grainId, this.reminderTable, silo.GlobalConfig, iniTimeSpan, this.loggerFactory);
        }
    }
}