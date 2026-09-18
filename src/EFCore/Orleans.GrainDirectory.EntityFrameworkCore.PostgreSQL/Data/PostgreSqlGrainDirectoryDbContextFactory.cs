using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlGrainDirectoryDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlGrainDirectoryDbContext>
{
    public PostgreSqlGrainDirectoryDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlGrainDirectoryDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=orleans;Username=postgres;Password=password", options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(PostgreSqlGrainDirectoryDbContext).Assembly.FullName);
        });
        return new PostgreSqlGrainDirectoryDbContext(optionsBuilder.Options);
    }
}
