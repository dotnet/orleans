using Microsoft.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore;
using Orleans.Hosting;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore;

namespace Orleans.EntityFrameworkCore.Tests.Infrastructure;

public delegate ISiloBuilder ConfigureClustering(
    ISiloBuilder builder,
    Action<DbContextOptionsBuilder> configureDatabase);

public delegate ISiloBuilder ConfigureNamedFeature(
    ISiloBuilder builder,
    string name,
    Action<DbContextOptionsBuilder> configureDatabase);

public delegate ISiloBuilder ConfigureReminderService(
    ISiloBuilder builder,
    Action<DbContextOptionsBuilder> configureDatabase);

public abstract class EFCoreProviderConfiguration<TETag>
{
    public abstract EFCoreTestDatabase Database { get; }

    public abstract Func<IEFClusterETagConverter<TETag>> CreateClusterETagConverter { get; }

    public abstract Func<IEFGrainDirectoryETagConverter<TETag>> CreateGrainDirectoryETagConverter { get; }

    public abstract Func<IEFGrainStorageETagConverter<TETag>> CreateGrainStorageETagConverter { get; }

    public abstract Func<IEFReminderETagConverter<TETag>> CreateReminderETagConverter { get; }

    public abstract ConfigureClustering UseClustering { get; }

    public abstract ConfigureNamedFeature UseGrainDirectory { get; }

    public abstract ConfigureNamedFeature UseGrainStorage { get; }

    public abstract ConfigureReminderService UseReminderService { get; }
}

public sealed class SqlServerEFCoreProviderConfiguration : EFCoreProviderConfiguration<byte[]>
{
    public override EFCoreTestDatabase Database => EFCoreTestDatabase.SqlServer;

    public override Func<IEFClusterETagConverter<byte[]>> CreateClusterETagConverter =>
        static () => new Orleans.Clustering.EntityFrameworkCore.SqlServer.SqlServerClusterETagConverter();

    public override Func<IEFGrainDirectoryETagConverter<byte[]>> CreateGrainDirectoryETagConverter =>
        static () => new Orleans.GrainDirectory.SqlServerGrainDirectoryETagConverter();

    public override Func<IEFGrainStorageETagConverter<byte[]>> CreateGrainStorageETagConverter =>
        static () => new Orleans.Persistence.SqlServerGrainStateETagConverter();

    public override Func<IEFReminderETagConverter<byte[]>> CreateReminderETagConverter =>
        static () => new Orleans.Reminders.SqlServerReminderETagConverter();

    public override ConfigureClustering UseClustering =>
        static (builder, configure) =>
            Orleans.Clustering.SqlServerHostingExtensions.UseEntityFrameworkCoreSqlServerClustering(builder, configure);

    public override ConfigureNamedFeature UseGrainDirectory =>
        static (builder, name, configure) =>
            Orleans.GrainDirectory.SqlServerHostingExtensions.AddEntityFrameworkCoreSqlServerGrainDirectory(builder, name, configure);

    public override ConfigureNamedFeature UseGrainStorage =>
        static (builder, name, configure) =>
            Orleans.Persistence.SqlHostingExtensions.AddEntityFrameworkCoreSqlServerGrainStorage(builder, name, configure);

    public override ConfigureReminderService UseReminderService =>
        static (builder, configure) =>
            Orleans.Reminders.SqlServerHostingExtensions.UseEntityFrameworkCoreSqlServerReminderService(builder, configure);
}

public sealed class MySqlEFCoreProviderConfiguration : EFCoreProviderConfiguration<Guid>
{
    public override EFCoreTestDatabase Database => EFCoreTestDatabase.MySql;

    public override Func<IEFClusterETagConverter<Guid>> CreateClusterETagConverter =>
        static () => new GuidClusterETagConverter();

    public override Func<IEFGrainDirectoryETagConverter<Guid>> CreateGrainDirectoryETagConverter =>
        static () => new GuidGrainDirectoryETagConverter();

    public override Func<IEFGrainStorageETagConverter<Guid>> CreateGrainStorageETagConverter =>
        static () => new GuidGrainStorageETagConverter();

    public override Func<IEFReminderETagConverter<Guid>> CreateReminderETagConverter =>
        static () => new GuidReminderETagConverter();

    public override ConfigureClustering UseClustering =>
        static (builder, configure) =>
            Orleans.Clustering.MySqlHostingExtensions.UseEntityFrameworkCoreMySqlClustering(builder, configure);

    public override ConfigureNamedFeature UseGrainDirectory =>
        static (builder, name, configure) =>
            Orleans.GrainDirectory.MySqlHostingExtensions.AddEntityFrameworkCoreMySqlGrainDirectory(builder, name, configure);

    public override ConfigureNamedFeature UseGrainStorage =>
        static (builder, name, configure) =>
            Orleans.Persistence.MySqlHostingExtensions.AddEntityFrameworkCoreMySqlGrainStorage(builder, name, configure);

    public override ConfigureReminderService UseReminderService =>
        static (builder, configure) =>
            Orleans.Reminders.MySqlHostingExtensions.UseEntityFrameworkCoreMySqlReminderService(builder, configure);
}

public sealed class PostgreSqlEFCoreProviderConfiguration : EFCoreProviderConfiguration<Guid>
{
    public override EFCoreTestDatabase Database => EFCoreTestDatabase.PostgreSql;

    public override Func<IEFClusterETagConverter<Guid>> CreateClusterETagConverter =>
        static () => new GuidClusterETagConverter();

    public override Func<IEFGrainDirectoryETagConverter<Guid>> CreateGrainDirectoryETagConverter =>
        static () => new GuidGrainDirectoryETagConverter();

    public override Func<IEFGrainStorageETagConverter<Guid>> CreateGrainStorageETagConverter =>
        static () => new GuidGrainStorageETagConverter();

    public override Func<IEFReminderETagConverter<Guid>> CreateReminderETagConverter =>
        static () => new GuidReminderETagConverter();

    public override ConfigureClustering UseClustering =>
        static (builder, configure) =>
            Orleans.Clustering.PostgreSqlHostingExtensions.UseEntityFrameworkCorePostgreSqlClustering(builder, configure);

    public override ConfigureNamedFeature UseGrainDirectory =>
        static (builder, name, configure) =>
            Orleans.GrainDirectory.PostgreSqlHostingExtensions.AddEntityFrameworkCorePostgreSqlGrainDirectory(builder, name, configure);

    public override ConfigureNamedFeature UseGrainStorage =>
        static (builder, name, configure) =>
            Orleans.Persistence.PostgreSqlHostingExtensions.AddEntityFrameworkCorePostgreSqlGrainStorage(builder, name, configure);

    public override ConfigureReminderService UseReminderService =>
        static (builder, configure) =>
            Orleans.Reminders.PostgreSqlHostingExtensions.UseEntityFrameworkCorePostgreSqlReminderService(builder, configure);
}
