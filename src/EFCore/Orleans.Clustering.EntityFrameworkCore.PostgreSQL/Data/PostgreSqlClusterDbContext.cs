using Microsoft.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlClusterDbContext : GuidClusterDbContext<PostgreSqlClusterDbContext>
{
    public PostgreSqlClusterDbContext(DbContextOptions<PostgreSqlClusterDbContext> options) : base(options)
    {
    }
}
