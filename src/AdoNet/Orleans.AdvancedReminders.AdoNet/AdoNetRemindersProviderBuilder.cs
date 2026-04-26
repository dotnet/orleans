using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.AdvancedReminders.AdoNet;

[assembly: RegisterProvider("AdoNet", "AdvancedReminders", "Silo", typeof(AdvancedAdoNetRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdvancedAdoNetRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseAdoNetAdvancedReminderService((OptionsBuilder<AdoNetReminderTableOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var invariant = configurationSection[nameof(options.Invariant)];
                if (!string.IsNullOrEmpty(invariant))
                {
                    options.Invariant = invariant;
                }

                var connectionString = configurationSection[nameof(options.ConnectionString)];
                var connectionName = configurationSection["ConnectionName"];
                if (string.IsNullOrEmpty(connectionString) && !string.IsNullOrEmpty(connectionName))
                {
                    connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
                }

                if (!string.IsNullOrEmpty(connectionString))
                {
                    options.ConnectionString = connectionString;
                }
            }));
    }
}
