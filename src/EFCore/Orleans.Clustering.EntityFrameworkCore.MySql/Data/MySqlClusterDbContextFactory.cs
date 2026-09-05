using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Clustering.EntityFrameworkCore.MySql.Data;

public class MySqlClusterDbContextFactory : IDesignTimeDbContextFactory<MySqlClusterDbContext>
{
    public MySqlClusterDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MySqlClusterDbContext>();
        optionsBuilder.UseMySql("Server=localhost;Database=orleans;User=root;Password=password", new MySqlServerVersion(new Version(8, 0)), opt =>
        {
            opt.MigrationsHistoryTable("__EFMigrationsHistory");
            opt.MigrationsAssembly(typeof(MySqlClusterDbContext).Assembly.FullName);
        });
        return new MySqlClusterDbContext(optionsBuilder.Options);
    }
}
