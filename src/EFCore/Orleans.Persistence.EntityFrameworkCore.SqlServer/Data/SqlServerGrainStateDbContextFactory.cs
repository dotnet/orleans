using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;

public class SqlServerGrainStateDbContextFactory : IDesignTimeDbContextFactory<SqlServerGrainStateDbContext>
{
    public SqlServerGrainStateDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerGrainStateDbContext>();
        optionsBuilder.UseSqlServer("Data Source=db.db", opt =>
        {
            opt.MigrationsHistoryTable("__EFMigrationsHistory");
            opt.MigrationsAssembly(typeof(SqlServerGrainStateDbContext).Assembly.FullName);
        });
        return new SqlServerGrainStateDbContext(optionsBuilder.Options);
    }
}