using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Reminders")]
public sealed class ReminderTests_EFCoreSqlServer :
    EFCoreReminderServiceTestsBase<
        SqlServerReminderDbContext,
        byte[],
        SqlServerEFCoreProviderConfiguration>
{
    public ReminderTests_EFCoreSqlServer(
        EFCoreReminderServiceFixture<
            SqlServerReminderDbContext,
            byte[],
            SqlServerEFCoreProviderConfiguration> fixture)
        : base(fixture)
    {
    }
}
