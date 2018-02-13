using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Legacy;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Configures reminders using legacy configuration.
    /// </summary>
    internal static class LegacyRemindersConfigurator
    {
        /// <summary>
        /// Configures reminders using legacy configuration.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="services">The service collection.</param>
        internal static void Configure(GlobalConfiguration configuration, IServiceCollection services)
        {
            var serviceType = configuration.ReminderServiceType;

            switch (serviceType)
            {
                case GlobalConfiguration.ReminderServiceProviderType.AdoNet:
                    {
                        var adapter = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyReminderTableAdapter>(Constants.ORLEANS_REMINDERS_ADONET);
                        adapter.Configure(configuration, services);
                        break;
                    }

                case GlobalConfiguration.ReminderServiceProviderType.AzureTable:
                    {
                        var adapter = LegacyAssemblyLoader.LoadAndCreateInstance<ILegacyReminderTableAdapter>(Constants.ORLEANS_REMINDERS_AZURESTORAGE);
                        adapter.Configure(configuration, services);
                        break;
                    }

                case GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain:
                    services.UseInMemoryReminderService();
                    break;
                case GlobalConfiguration.ReminderServiceProviderType.MockTable:
                    {
                        services.AddSingleton<IReminderTable, MockReminderTable>();
                        services.AddOptions<MockReminderTableOptions>()
                            .Configure<GlobalConfiguration>((options, config) => { options.OperationDelay = config.MockReminderTableTimeout; });
                        services.TryConfigureFormatter<MockReminderTableOptions, MockReminderTableOptionsFormatter>();
                        break;
                    }

                case GlobalConfiguration.ReminderServiceProviderType.Custom:
                    services.AddSingleton<IReminderTable>(
                        serviceProvider =>
                            {
                                var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ReminderTableFactory");
                                return AssemblyLoader.LoadAndCreateInstance<IReminderTable>(configuration.ReminderTableAssembly, logger, serviceProvider);
                            });
                    break;
                case GlobalConfiguration.ReminderServiceProviderType.NotSpecified:
                case GlobalConfiguration.ReminderServiceProviderType.Disabled:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(configuration.ReminderServiceType),
                        $"The {nameof(configuration.ReminderServiceType)} value {serviceType} is not supported.");
            }
        }
    }
}