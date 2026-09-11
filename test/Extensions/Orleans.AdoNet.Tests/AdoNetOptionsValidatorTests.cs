using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using UnitTests.StorageTests.Relational;

namespace UnitTests.AdoNet;

[TestCategory("AdoNet")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
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

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Streaming_ValidatesMaxMessagesPerRead(int maxMessagesPerRead, bool valid)
    {
        var options = ValidStreamOptions();
        options.MaxMessagesPerRead = maxMessagesPerRead;

        AssertStreamingValidation(valid, options, nameof(AdoNetStreamOptions.MaxMessagesPerRead));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Streaming_ValidatesCheckpointPersistInterval(int seconds, bool valid)
    {
        var options = ValidStreamOptions();
        options.CheckpointPersistInterval = TimeSpan.FromSeconds(seconds);

        AssertStreamingValidation(valid, options, nameof(AdoNetStreamOptions.CheckpointPersistInterval));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Streaming_ValidatesRetentionPeriod(int seconds, bool valid)
    {
        var options = ValidStreamOptions();
        options.RetentionPeriod = TimeSpan.FromSeconds(seconds);

        AssertStreamingValidation(valid, options, nameof(AdoNetStreamOptions.RetentionPeriod));
    }

    [Fact]
    public void Streaming_RejectsSubSecondRetentionPeriod()
    {
        var options = ValidStreamOptions();
        options.RetentionPeriod = TimeSpan.FromMilliseconds(500);

        AssertStreamingValidation(false, options, nameof(AdoNetStreamOptions.RetentionPeriod));
    }

    [Theory]
    [InlineData(1.1)]
    [InlineData(1.9)]
    public void Streaming_AllowsFractionalRetentionWhichRoundsUp(double seconds)
    {
        var options = ValidStreamOptions();
        options.RetentionPeriod = TimeSpan.FromSeconds(seconds);

        new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration();
        Assert.Equal(2, AdoNetStreamTime.ToSqlSeconds(options.RetentionPeriod));
    }

    [Fact]
    public void Streaming_AllowsNullMaximumRetentionPeriod()
    {
        var options = ValidStreamOptions();
        options.RetentionPeriod = TimeSpan.FromSeconds(30);
        options.MaximumRetentionPeriod = null;

        // Should not throw: a null hard ceiling means no hard-retention diagnostics are ever produced.
        new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration();
    }

    [Theory]
    [InlineData(30, 30, true)] // equal to the normal retention period is allowed
    [InlineData(30, 31, true)] // greater than the normal retention period is allowed
    [InlineData(30, 29, false)] // a hard ceiling tighter than normal retention is invalid
    public void Streaming_ValidatesMaximumRetentionPeriodAgainstRetentionPeriod(int retentionSeconds, int maximumRetentionSeconds, bool valid)
    {
        var options = ValidStreamOptions();
        options.RetentionPeriod = TimeSpan.FromSeconds(retentionSeconds);
        options.MaximumRetentionPeriod = TimeSpan.FromSeconds(maximumRetentionSeconds);

        if (valid)
        {
            new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration();
        }
        else
        {
            var exception = Assert.Throws<OrleansConfigurationException>(() => new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration());
            Assert.Contains(nameof(AdoNetStreamOptions.MaximumRetentionPeriod), exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Streaming_ValidatesCleanupInterval(int seconds, bool valid)
    {
        var options = ValidStreamOptions();
        options.CleanupInterval = TimeSpan.FromSeconds(seconds);

        AssertStreamingValidation(valid, options, nameof(AdoNetStreamOptions.CleanupInterval));
    }

    [Fact]
    public void Streaming_RejectsSubSecondCleanupInterval()
    {
        var options = ValidStreamOptions();
        options.CleanupInterval = TimeSpan.FromMilliseconds(500);

        AssertStreamingValidation(false, options, nameof(AdoNetStreamOptions.CleanupInterval));
    }

    [Fact]
    public void Streaming_RejectsRetentionWhoseCeilingExceedsSqlIntegerRange()
    {
        var options = ValidStreamOptions();
        options.RetentionPeriod = TimeSpan.FromSeconds(int.MaxValue) + TimeSpan.FromTicks(1);

        AssertStreamingValidation(false, options, nameof(AdoNetStreamOptions.RetentionPeriod));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Streaming_ValidatesCleanupBatchSize(int cleanupBatchSize, bool valid)
    {
        var options = ValidStreamOptions();
        options.CleanupBatchSize = cleanupBatchSize;

        AssertStreamingValidation(valid, options, nameof(AdoNetStreamOptions.CleanupBatchSize));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Streaming_ValidatesInitializationTimeout(int seconds, bool valid)
    {
        var options = ValidStreamOptions();
        options.InitializationTimeout = TimeSpan.FromSeconds(seconds);

        AssertStreamingValidation(valid, options, nameof(AdoNetStreamOptions.InitializationTimeout));
    }

    [Fact]
    public void Streaming_Options_HaveRetentionSafeDefaults()
    {
        var options = new AdoNetStreamOptions();

        Assert.False(options.StartFromNow);
        Assert.Equal(TimeSpan.FromDays(1), options.RetentionPeriod);
        Assert.Null(options.MaximumRetentionPeriod);
    }

    private static AdoNetStreamOptions ValidStreamOptions() => new()
    {
        Invariant = AdoNetInvariants.InvariantNameSqlLite,
        ConnectionString = "Data Source=:memory:",
        MaxMessagesPerRead = 100,
        CheckpointPersistInterval = TimeSpan.FromSeconds(5),
        RetentionPeriod = TimeSpan.FromMinutes(1),
        MaximumRetentionPeriod = TimeSpan.FromMinutes(5),
        CleanupInterval = TimeSpan.FromMinutes(1),
        CleanupBatchSize = 1000,
    };

    private static void AssertStreamingValidation(bool valid, AdoNetStreamOptions options, string propertyName)
    {
        if (valid)
        {
            new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration();
        }
        else
        {
            var exception = Assert.Throws<OrleansConfigurationException>(() => new AdoNetStreamOptionsValidator(options, "stream").ValidateConfiguration());
            Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
        }
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
