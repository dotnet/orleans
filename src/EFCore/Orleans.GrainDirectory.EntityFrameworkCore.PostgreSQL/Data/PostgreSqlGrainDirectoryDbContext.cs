using Microsoft.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlGrainDirectoryDbContext : GuidGrainDirectoryDbContext<PostgreSqlGrainDirectoryDbContext>
{
    public PostgreSqlGrainDirectoryDbContext(DbContextOptions<PostgreSqlGrainDirectoryDbContext> options) : base(options)
    {
    }
}
