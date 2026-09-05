using Microsoft.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;

public class MySqlGrainDirectoryDbContext : GuidGrainDirectoryDbContext<MySqlGrainDirectoryDbContext>
{
    private const string IdentifierCollation = "utf8mb4_bin";

    public MySqlGrainDirectoryDbContext(DbContextOptions<MySqlGrainDirectoryDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GrainActivationRecord<Guid>>(entity =>
        {
            entity.Property(record => record.ClusterId).UseCollation(IdentifierCollation);
            entity.Property(record => record.GrainId).UseCollation(IdentifierCollation);
            entity.Property(record => record.SiloAddress).UseCollation(IdentifierCollation);
            entity.Property(record => record.ActivationId).UseCollation(IdentifierCollation);
        });
    }
}
