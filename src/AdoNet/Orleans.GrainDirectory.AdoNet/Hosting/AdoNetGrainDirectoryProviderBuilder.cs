using static System.String;

[assembly: RegisterProvider("AdoNet", "GrainDirectory", "Silo", typeof(AdoNetGrainDirectoryProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetGrainDirectoryProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddAdoNetGrainDirectory(name ?? "Default", optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            var invariant = configurationSection["Invariant"];
            if (!IsNullOrWhiteSpace(invariant))
            {
                options.Invariant = invariant;
            }

            var connectionString = configurationSection["ConnectionString"];
            var connectionName = configurationSection["ConnectionName"];
            if (!IsNullOrWhiteSpace(connectionString))
            {
                options.ConnectionString = connectionString;
            }
            else if (!IsNullOrWhiteSpace(connectionName))
            {
                connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
                if (!IsNullOrWhiteSpace(connectionString))
                {
                    options.ConnectionString = connectionString;
                }
            }
        }));
    }
}
