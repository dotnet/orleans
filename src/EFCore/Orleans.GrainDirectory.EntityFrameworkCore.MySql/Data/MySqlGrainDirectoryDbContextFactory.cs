using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;

public class MySqlGrainDirectoryDbContextFactory : IDesignTimeDbContextFactory<MySqlGrainDirectoryDbContext>
{
    public MySqlGrainDirectoryDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MySqlGrainDirectoryDbContext>();
        optionsBuilder.UseMySql("Server=localhost;Database=orleans;User=root;Password=password", new MySqlServerVersion(new Version(8, 0)), options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(MySqlGrainDirectoryDbContext).Assembly.FullName);
        });
        return new MySqlGrainDirectoryDbContext(optionsBuilder.Options);
    }
}
