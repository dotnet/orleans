using Microsoft.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.Data;

namespace Orleans.Persistence.EntityFrameworkCore.MySql.Data;

public class MySqlGrainStateDbContext : GuidGrainStateDbContext<MySqlGrainStateDbContext>
{
    private const string IdentifierCollation = "utf8mb4_bin";

    public MySqlGrainStateDbContext(DbContextOptions<MySqlGrainStateDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GrainStateRecord<Guid>>(entity =>
        {
            entity.Property(record => record.ServiceId).HasMaxLength(191).UseCollation(IdentifierCollation);
            entity.Property(record => record.GrainType).HasMaxLength(191).UseCollation(IdentifierCollation);
            entity.Property(record => record.StateType).HasMaxLength(191).UseCollation(IdentifierCollation);
            entity.Property(record => record.GrainId).HasMaxLength(191).UseCollation(IdentifierCollation);
        });
    }
}
