using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Persistence.EntityFrameworkCore.MySql.Data;

public class MySqlGrainStateDbContextFactory : IDesignTimeDbContextFactory<MySqlGrainStateDbContext>
{
    public MySqlGrainStateDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MySqlGrainStateDbContext>();
        optionsBuilder.UseMySql("Server=localhost;Database=orleans;User=root;Password=password", new MySqlServerVersion(new Version(8, 0)), options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(MySqlGrainStateDbContext).Assembly.FullName);
        });
        return new MySqlGrainStateDbContext(optionsBuilder.Options);
    }
}
