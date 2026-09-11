using static System.String;
using Orleans.GrainDirectory.AdoNet.Storage;

[assembly: RegisterProvider("AdoNet", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetGrainDirectoryProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddAdoNetGrainDirectory(name ?? "Default", optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            var invariant = AdoNetProviderConfiguration.GetInvariant(configurationSection);
            if (!IsNullOrWhiteSpace(invariant))
            {
                options.Invariant = invariant;
            }

            var connectionString = AdoNetProviderConfiguration.GetConnectionString(configurationSection, services);
            if (!IsNullOrWhiteSpace(connectionString))
            {
                options.ConnectionString = connectionString;
            }
        }));
    }
}
