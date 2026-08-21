using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Infrastructure;

public abstract class EFCoreTestDatabase
{
    public static EFCoreTestDatabase SqlServer { get; } = new SqlServerTestDatabase();

    public static EFCoreTestDatabase MySql { get; } = new MySqlTestDatabase();

    public static EFCoreTestDatabase PostgreSql { get; } = new PostgreSqlTestDatabase();

    public abstract string Name { get; }

    public abstract string ConfigurationVariable { get; }

    public abstract string? GetConfiguredConnectionString();

    public string RequireConnectionString()
    {
        var connectionString = GetConfiguredConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"{Name} connection string is not configured. Set {ConfigurationVariable}.");
        }

        return connectionString;
    }

    public string CreateDatabaseName(string feature, string testId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);

        var value = $"orleans_{Name}_{feature}_{testId}_{Guid.NewGuid():N}";
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        if (result.Length <= 63)
        {
            return result.ToString();
        }

        const int suffixLength = 33;
        return string.Concat(
            result.ToString(0, 63 - suffixLength),
            "_",
            result.ToString(result.Length - suffixLength + 1, suffixLength - 1));
    }

    public abstract string WithDatabase(string connectionString, string databaseName);

    public abstract void ConfigureOptions(
        DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        string migrationsAssembly);

    public async Task MigrateAsync<TDbContext>(
        IDbContextFactory<TDbContext> factory,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);

        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }

    public async Task DeleteDatabaseAsync<TDbContext>(
        IDbContextFactory<TDbContext> factory,
        Action<Exception>? cleanupFailure = null,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);

        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await context.Database.EnsureDeletedAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            cleanupFailure?.Invoke(exception);
        }
    }
}

public sealed class SqlServerTestDatabase : EFCoreTestDatabase
{
    public override string Name => "sqlserver";

    public override string ConfigurationVariable => "ORLEANSMSSQLCONNECTIONSTRING";

    public override string? GetConfiguredConnectionString() => TestDefaultConfiguration.MsSqlConnectionString;

    public override string WithDatabase(string connectionString, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName }.ConnectionString;
    }

    public override void ConfigureOptions(
        DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        string migrationsAssembly) =>
        optionsBuilder.UseSqlServer(connectionString, options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(migrationsAssembly);
        });
}

public sealed class MySqlTestDatabase : EFCoreTestDatabase
{
    private static readonly MySqlServerVersion ServerVersion = new(new Version(8, 0, 0));

    public override string Name => "mysql";

    public override string ConfigurationVariable => "ORLEANSMYSQLCONNECTIONSTRING";

    public override string? GetConfiguredConnectionString() => TestDefaultConfiguration.MySqlConnectionString;

    public override string WithDatabase(string connectionString, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return new MySqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
    }

    public override void ConfigureOptions(
        DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        string migrationsAssembly) =>
        optionsBuilder.UseMySql(connectionString, ServerVersion, options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(migrationsAssembly);
        });
}

public sealed class PostgreSqlTestDatabase : EFCoreTestDatabase
{
    public override string Name => "postgresql";

    public override string ConfigurationVariable => "ORLEANSPOSTGRESCONNECTIONSTRING";

    public override string? GetConfiguredConnectionString() => TestDefaultConfiguration.PostgresConnectionString;

    public override string WithDatabase(string connectionString, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
    }

    public override void ConfigureOptions(
        DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        string migrationsAssembly) =>
        optionsBuilder.UseNpgsql(connectionString, options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(migrationsAssembly);
        });
}
