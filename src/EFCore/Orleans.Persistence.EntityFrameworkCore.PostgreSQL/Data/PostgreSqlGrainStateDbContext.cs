using Microsoft.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.Data;

namespace Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlGrainStateDbContext : GuidGrainStateDbContext<PostgreSqlGrainStateDbContext>
{
    public PostgreSqlGrainStateDbContext(DbContextOptions<PostgreSqlGrainStateDbContext> options) : base(options)
    {
    }
}
