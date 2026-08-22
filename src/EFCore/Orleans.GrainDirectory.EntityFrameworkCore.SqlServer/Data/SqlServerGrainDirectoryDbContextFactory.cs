using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;

public class SqlServerGrainDirectoryDbContextFactory: IDesignTimeDbContextFactory<SqlServerGrainDirectoryDbContext>
{
    public SqlServerGrainDirectoryDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerGrainDirectoryDbContext>();
        optionsBuilder.UseSqlServer("Data Source=db.db", opt =>
        {
            opt.MigrationsHistoryTable("__EFMigrationsHistory");
            opt.MigrationsAssembly(typeof(SqlServerGrainDirectoryDbContext).Assembly.FullName);
        });
        return new SqlServerGrainDirectoryDbContext(optionsBuilder.Options);
    }
}