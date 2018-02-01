using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace Orleans.Runtime.ReminderService
{
    internal class ReminderTableFactory
    {
        private readonly ReminderOptions options;
        private readonly IGrainFactory grainFactory;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;

        public ReminderTableFactory(
            IOptions<ReminderOptions> options,
            IGrainFactory grainFactory,
            IServiceProvider serviceProvider,
            ILogger<ReminderTableFactory> logger)
        {
            this.options = options.Value;
            this.grainFactory = grainFactory;
            this.logger = logger;
            this.serviceProvider = serviceProvider;
        }

        public IReminderTable Create()
        {
            var serviceType = this.options.ReminderService;

            switch (serviceType)
            {
                case ReminderOptions.BuiltIn.SqlServer:
                    return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(
                        Constants.ORLEANS_REMINDERS_ADONET,
                        logger,
                        this.serviceProvider);
                case ReminderOptions.BuiltIn.AzureTable:
                    return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(
                        Constants.ORLEANS_REMINDERS_AZURESTORAGE,
                        logger,
                        this.serviceProvider);
                case ReminderOptions.BuiltIn.ReminderTableGrain:
                    return this.grainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
                case ReminderOptions.BuiltIn.MockTable:
                    return new MockReminderTable(this.options.MockReminderTableTimeout);
                case ReminderOptions.BuiltIn.Custom:
                    return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(
                        this.options.ReminderTableAssembly,
                        logger,
                        this.serviceProvider);
                default:
                    throw new NotSupportedException(
                              $"The reminder table does not currently support service provider {serviceType}.");

            }
        }
    }
}