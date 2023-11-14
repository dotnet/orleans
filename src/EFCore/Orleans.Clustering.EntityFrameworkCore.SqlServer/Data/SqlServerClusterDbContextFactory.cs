using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;

public class SqlServerClusterDbContextFactory : IDesignTimeDbContextFactory<SqlServerClusterDbContext>
{
    public SqlServerClusterDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerClusterDbContext>();
        optionsBuilder.UseSqlServer("Data Source=db.db", opt =>
        {
            opt.MigrationsHistoryTable("__EFMigrationsHistory");
            opt.MigrationsAssembly(typeof(SqlServerClusterDbContext).Assembly.FullName);
        });
        return new SqlServerClusterDbContext(optionsBuilder.Options);
    }
}