using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;

public class SqlServerReminderDbContextFactory : IDesignTimeDbContextFactory<SqlServerReminderDbContext>
{
    public SqlServerReminderDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerReminderDbContext>();
        optionsBuilder.UseSqlServer("Data Source=db.db", opt =>
        {
            opt.MigrationsHistoryTable("__EFMigrationsHistory");
            opt.MigrationsAssembly(typeof(SqlServerReminderDbContext).Assembly.FullName);
        });
        return new SqlServerReminderDbContext(optionsBuilder.Options);
    }
}