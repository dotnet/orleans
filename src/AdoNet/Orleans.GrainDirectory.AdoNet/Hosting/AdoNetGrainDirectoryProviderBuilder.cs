using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Hosting;
using Orleans.Providers;
using static System.String;

[assembly: RegisterProvider("AdoNet", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetGrainDirectoryProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddAdoNetGrainDirectory(name, (OptionsBuilder<AdoNetGrainDirectoryOptions> optionsBuilder) =>
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var invariant = configurationSection["Invariant"];
                if (!IsNullOrEmpty(invariant))
                {
                    options.Invariant = invariant;
                }

                var connectionString = configurationSection["ConnectionString"];
                var connectionName = configurationSection["ConnectionName"];
                if (!IsNullOrEmpty(connectionString))
                {
                    options.ConnectionString = connectionString;
                }
                else if (!IsNullOrEmpty(connectionName))
                {
                    options.ConnectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
                }
            }));
    }
}
