using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Tests.SqlUtils;
using UnitTests.StorageTests.Relational;

namespace UnitTests.AdoNet;

[TestCategory("AdoNet")]
public sealed class AdoNetOptionsValidatorTests
{
    [Fact]
    public void NamedOptions_ResolveKeyedDataSources()
    {
        using var first = new TrackingSqliteDataSource("Data Source=first.db");
        using var second = new TrackingSqliteDataSource("Data Source=second.db");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<DbDataSource>("first", first);
        services.AddKeyedSingleton<DbDataSource>("second", second);
        Configure("first");
        Configure("second");
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptionsMonitor<AdoNetStreamOptions>>();

        Assert.Same(first, options.Get("first").DataSource);
        Assert.Same(second, options.Get("second").DataSource);

        void Configure(string name)
        {
            services.AddOptions<AdoNetStreamOptions>(name).Configure<IServiceProvider>((value, provider) =>
            {
                value.Invariant = AdoNetInvariants.InvariantNameSqlLite;
                value.DataSource = provider.GetRequiredKeyedService<DbDataSource>(name);
            });
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ClusteringClient_ValidatesConnectionSource(bool connectionString, bool dataSource, bool valid)
    {
        using var source = new TrackingSqliteDataSource("Data Source=:memory:");
        var options = new AdoNetClusteringClientOptions
        {
            Invariant = AdoNetInvariants.InvariantNameSqlLite,
            ConnectionString = connectionString ? source.ConnectionString : null,
            DataSource = dataSource ? source : null
        };

        AssertValidation(valid, new AdoNetClusteringClientOptionsValidator(Options.Create(options)).ValidateConfiguration);
    }

    [Fact]
    public void ClusteringClient_RequiresInvariant()
    {
        var options = new AdoNetClusteringClientOptions { ConnectionString = "configured", Invariant = " " };

        AssertInvariantRequired(new AdoNetClusteringClientOptionsValidator(Options.Create(options)).ValidateConfiguration);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ClusteringSilo_ValidatesConnectionSource(bool connectionString, bool dataSource, bool valid)
    {
        using var source = new TrackingSqliteDataSource("Data Source=:memory:");
        var options = new AdoNetClusteringSiloOptions
        {
            Invariant = AdoNetInvariants.InvariantNameSqlLite,
            ConnectionString = connectionString ? source.ConnectionString : null,
            DataSource = dataSource ? source : null
        };

        AssertValidation(valid, new AdoNetClusteringSiloOptionsValidator(Options.Create(options)).ValidateConfiguration);
    }

    [Fact]
    public void ClusteringSilo_RequiresInvariant()
    {
        var options = new AdoNetClusteringSiloOptions { ConnectionString = "configured", Invariant = " " };

        AssertInvariantRequired(new AdoNetClusteringSiloOptionsValidator(Options.Create(options)).ValidateConfiguration);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ReminderTable_ValidatesConnectionSource(bool connectionString, bool dataSource, bool valid)
    {
        using var source = new TrackingSqliteDataSource("Data Source=:memory:");
        var options = new AdoNetReminderTableOptions
        {
            Invariant = AdoNetInvariants.InvariantNameSqlLite,
            ConnectionString = connectionString ? source.ConnectionString : null,
            DataSource = dataSource ? source : null
        };

        AssertValidation(valid, new AdoNetReminderTableOptionsValidator(Options.Create(options)).ValidateConfiguration);
    }

    [Fact]
    public void ReminderTable_RequiresInvariant()
    {
        var options = new AdoNetReminderTableOptions { ConnectionString = "configured", Invariant = " " };

        AssertInvariantRequired(new AdoNetReminderTableOptionsValidator(Options.Create(options)).ValidateConfiguration);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void GrainDirectory_ValidatesConnectionSource(bool connectionString, bool dataSource, bool valid)
    {
        using var source = new TrackingSqliteDataSource("Data Source=:memory:");
        var options = new AdoNetGrainDirectoryOptions
        {
            Invariant = AdoNetInvariants.InvariantNameSqlLite,
            ConnectionString = connectionString ? source.ConnectionString : null,
            DataSource = dataSource ? source : null
        };

        AssertValidation(valid, new AdoNetGrainDirectoryOptionsValidator(options, "directory").ValidateConfiguration);
    }

    [Fact]
    public void GrainDirectory_RequiresInvariant()
    {
        var options = new AdoNetGrainDirectoryOptions { ConnectionString = "configured", Invariant = " " };

        AssertInvariantRequired(new AdoNetGrainDirectoryOptionsValidator(options, "directory").ValidateConfiguration);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void GrainStorage_ValidatesConnectionSource(bool connectionString, bool dataSource, bool valid)
    {
        using var source = new TrackingSqliteDataSource("Data Source=:memory:");
        var options = new AdoNetGrainStorageOptions
        {
            Invariant = AdoNetInvariants.InvariantNameSqlLite,
            ConnectionString = connectionString ? source.ConnectionString : null,
            DataSource = dataSource ? source : null,
            HashPicker = new StorageHasherPicker([new ConstantHasher()])
        };

        AssertValidation(valid, new AdoNetGrainStorageOptionsValidator(options, "storage").ValidateConfiguration);
    }

    [Fact]
    public void GrainStorage_RequiresInvariant()
    {
        var options = new AdoNetGrainStorageOptions
        {
            ConnectionString = "configured",
            Invariant = " ",
            HashPicker = new StorageHasherPicker([new ConstantHasher()])
        };

        AssertInvariantRequired(new AdoNetGrainStorageOptionsValidator(options, "storage").ValidateConfiguration);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void Streaming_ValidatesConnectionSource(bool connectionString, bool dataSource, bool valid)
    {
        using var source = new TrackingSqliteDataSource("Data Source=:memory:");
        var options = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariants.InvariantNameSqlLite,
            ConnectionString = connectionString ? source.ConnectionString : null,
            DataSource = dataSource ? source : null
        };

        AssertValidation(valid, new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration);
    }

    [Fact]
    public void Streaming_RequiresInvariant()
    {
        var options = new AdoNetStreamOptions { ConnectionString = "configured", Invariant = " " };

        AssertInvariantRequired(new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration);
    }

    private static void AssertValidation(bool valid, Action validate)
    {
        if (valid)
        {
            validate();
        }
        else
        {
            var exception = Assert.Throws<OrleansConfigurationException>(validate);
            Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertInvariantRequired(Action validate)
    {
        var exception = Assert.Throws<OrleansConfigurationException>(validate);
        Assert.Contains("Invariant", exception.Message, StringComparison.Ordinal);
        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
