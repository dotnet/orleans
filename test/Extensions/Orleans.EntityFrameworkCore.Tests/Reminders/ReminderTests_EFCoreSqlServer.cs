using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.SqlServer)]
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
