using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.PostgreSql)]
[TestCategory(EFCoreTestCategories.PostgreSqlProvider)]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.PostgreSqlProvider)]
[TestArea("Reminders")]
public sealed class ReminderTests_EFCorePostgreSql :
    EFCoreReminderServiceTestsBase<
        PostgreSqlReminderDbContext,
        Guid,
        PostgreSqlEFCoreProviderConfiguration>
{
    public ReminderTests_EFCorePostgreSql(
        EFCoreReminderServiceFixture<
            PostgreSqlReminderDbContext,
            Guid,
            PostgreSqlEFCoreProviderConfiguration> fixture)
        : base(fixture)
    {
    }
}
