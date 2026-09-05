using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlReminderDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlReminderDbContext>
{
    public PostgreSqlReminderDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlReminderDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=orleans;Username=postgres;Password=password", options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(PostgreSqlReminderDbContext).Assembly.FullName);
        });
        return new PostgreSqlReminderDbContext(optionsBuilder.Options);
    }
}
