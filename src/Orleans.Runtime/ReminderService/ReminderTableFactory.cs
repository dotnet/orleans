using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal class ReminderTableFactory
    {
        private readonly GlobalConfiguration globalConfiguration;
        private readonly IGrainFactory grainFactory;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;

        public ReminderTableFactory(
            GlobalConfiguration globalConfiguration,
            IGrainFactory grainFactory,
            IServiceProvider serviceProvider,
            ILogger<ReminderTableFactory> logger)
        {
            this.globalConfiguration = globalConfiguration;
            this.grainFactory = grainFactory;
            this.logger = logger;
            this.serviceProvider = serviceProvider;
        }

        public IReminderTable Create()
        {
            var config = this.globalConfiguration;
            var serviceType = config.ReminderServiceType;

            switch (serviceType)
            {
                default:
                    throw new NotSupportedException(
                              $"The reminder table does not currently support service provider {serviceType}.");
                case GlobalConfiguration.ReminderServiceProviderType.SqlServer:
                    return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(
                        Constants.ORLEANS_REMINDERS_ADONET,
                        logger,
                        this.serviceProvider);
                case GlobalConfiguration.ReminderServiceProviderType.AzureTable:
                    return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(
                        Constants.ORLEANS_REMINDERS_AZURESTORAGE,
                        logger,
                        this.serviceProvider);
                case GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain:
                    return this.grainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
                case GlobalConfiguration.ReminderServiceProviderType.MockTable:
                    return new MockReminderTable(config.MockReminderTableTimeout);
                case GlobalConfiguration.ReminderServiceProviderType.Custom:
                    return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(
                        config.ReminderTableAssembly,
                        logger,
                        this.serviceProvider);
            }
        }
    }
}