using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Reminders.EntityFrameworkCore.MySql.Data;

public class MySqlReminderDbContextFactory : IDesignTimeDbContextFactory<MySqlReminderDbContext>
{
    public MySqlReminderDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MySqlReminderDbContext>();
        optionsBuilder.UseMySql("Server=localhost;Database=orleans;User=root;Password=password", new MySqlServerVersion(new Version(8, 0)), options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(MySqlReminderDbContext).Assembly.FullName);
        });
        return new MySqlReminderDbContext(optionsBuilder.Options);
    }
}
