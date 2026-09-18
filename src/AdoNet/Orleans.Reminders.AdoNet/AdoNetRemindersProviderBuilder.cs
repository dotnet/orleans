using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Reminders.AdoNet.Storage;

[assembly: RegisterProvider("AdoNet", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]
[assembly: RegisterProvider("OracleDatabase", "Reminders", "Silo", typeof(AdoNetRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseAdoNetReminderService((OptionsBuilder<AdoNetReminderTableOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var invariant = AdoNetProviderConfiguration.GetInvariant(configurationSection);
                if (!string.IsNullOrWhiteSpace(invariant))
                {
                    options.Invariant = invariant;
                }

                var connectionString = AdoNetProviderConfiguration.GetConnectionString(configurationSection, services);
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    options.ConnectionString = connectionString;
                }
            }));
    }
}
