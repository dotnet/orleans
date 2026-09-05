using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlGrainStateDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlGrainStateDbContext>
{
    public PostgreSqlGrainStateDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlGrainStateDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=orleans;Username=postgres;Password=password", options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(PostgreSqlGrainStateDbContext).Assembly.FullName);
        });
        return new PostgreSqlGrainStateDbContext(optionsBuilder.Options);
    }
}
