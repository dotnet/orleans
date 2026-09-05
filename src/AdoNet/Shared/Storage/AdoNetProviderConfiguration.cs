using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage;
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage;
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage;
#elif STREAMING_ADONET
namespace Orleans.Streaming.AdoNet.Storage;
#elif GRAINDIRECTORY_ADONET
namespace Orleans.GrainDirectory.AdoNet.Storage;
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

internal static class AdoNetProviderConfiguration
{
    public static string? GetInvariant(IConfigurationSection configurationSection)
    {
        var invariant = configurationSection["Invariant"];
        if (!string.IsNullOrWhiteSpace(invariant))
        {
            return invariant;
        }

        return configurationSection["ProviderType"] switch
        {
            "SqlServerDatabase" or "AzureSqlDatabase" => AdoNetInvariants.InvariantNameSqlServer,
            "PostgresDatabase" or "AzurePostgresFlexibleServerDatabase" => AdoNetInvariants.InvariantNamePostgreSql,
            "MySqlDatabase" => AdoNetInvariants.InvariantNameMySql,
            "OracleDatabase" => AdoNetInvariants.InvariantNameOracleDatabase,
            _ => null,
        };
    }

    public static string? GetConnectionString(IConfigurationSection configurationSection, IServiceProvider services)
    {
        var connectionString = configurationSection["ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var connectionName = configurationSection["ServiceKey"];
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            connectionName = configurationSection["ConnectionName"];
        }

        return string.IsNullOrWhiteSpace(connectionName)
            ? null
            : services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
    }
}
