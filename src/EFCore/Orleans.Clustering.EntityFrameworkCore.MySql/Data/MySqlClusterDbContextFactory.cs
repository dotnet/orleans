using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Clustering.EntityFrameworkCore.MySql.Data;

public class MySqlClusterDbContextFactory: IDesignTimeDbContextFactory<MySqlClusterDbContext>
{
    public MySqlClusterDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MySqlClusterDbContext>();
        optionsBuilder.UseMySQL("Data Source=db.db", opt =>
        {
            opt.MigrationsHistoryTable("__EFMigrationsHistory");
            opt.MigrationsAssembly(typeof(MySqlClusterDbContext).Assembly.FullName);
        });
        return new MySqlClusterDbContext(optionsBuilder.Options);
    }
}