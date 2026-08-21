using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.EntityFrameworkCore.Tests.Infrastructure;

public class EFCoreDatabaseFixture<TDbContext> : IAsyncLifetime
    where TDbContext : DbContext
{
    private readonly EFCoreTestDatabase _database;
    private readonly string _databaseName;
    private readonly string _migrationsAssembly;
    private readonly Action<string>? _writeOutput;
    private ServiceProvider? _services;
    private IDbContextFactory<TDbContext>? _factory;

    public EFCoreDatabaseFixture(
        EFCoreTestDatabase database,
        string feature,
        string testId,
        string? migrationsAssembly = null,
        Action<string>? writeOutput = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _databaseName = database.CreateDatabaseName(feature, testId);
        _migrationsAssembly = migrationsAssembly ?? typeof(TDbContext).Assembly.GetName().Name!;
        _writeOutput = writeOutput;
    }

    public string ConnectionString { get; private set; } = string.Empty;

    public IDbContextFactory<TDbContext> Factory =>
        _factory ?? throw new InvalidOperationException("The database fixture has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var configuredConnectionString = _database.RequireConnectionString();
        ConnectionString = _database.WithDatabase(configuredConnectionString, _databaseName);

        _services = new ServiceCollection()
            .AddPooledDbContextFactory<TDbContext>(
                options => _database.ConfigureOptions(options, ConnectionString, _migrationsAssembly))
            .BuildServiceProvider();

        _factory = _services.GetRequiredService<IDbContextFactory<TDbContext>>();
        await _database.MigrateAsync(_factory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _database.DeleteDatabaseAsync(
                _factory,
                exception => _writeOutput?.Invoke(
                    $"Unable to delete isolated {_database.Name} database '{_databaseName}': {exception.Message}"));
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }
}
