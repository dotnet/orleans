using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;
using TestExtensions;
using UnitTests;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.PostgreSql)]
[TestCategory(EFCoreTestCategories.PostgreSqlProvider)]
public sealed class EFCorePostgreSqlReminderTableTests :
    EFCoreReminderTableTestsBase<PostgreSqlReminderDbContext, Guid>
{
    public EFCorePostgreSqlReminderTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment,
        ITestOutputHelper testOutput)
        : base(fixture, environment, testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.PostgreSql;

    protected override IEFReminderETagConverter<Guid> CreateETagConverter() =>
        new GuidReminderETagConverter();
}
