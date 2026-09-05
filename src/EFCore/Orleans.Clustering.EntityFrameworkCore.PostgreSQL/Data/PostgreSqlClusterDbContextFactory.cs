using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlClusterDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlClusterDbContext>
{
    public PostgreSqlClusterDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlClusterDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=orleans;Username=postgres;Password=password", options =>
        {
            options.MigrationsHistoryTable("__EFMigrationsHistory");
            options.MigrationsAssembly(typeof(PostgreSqlClusterDbContext).Assembly.FullName);
        });
        return new PostgreSqlClusterDbContext(optionsBuilder.Options);
    }
}
