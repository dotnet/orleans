using Microsoft.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.Data;

namespace Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;

public class SqlServerGrainStateDbContext : GrainStateDbContext<SqlServerGrainStateDbContext, byte[]>
{
    public SqlServerGrainStateDbContext(DbContextOptions<SqlServerGrainStateDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainStateRecord<byte[]>>(c =>
        {
            c.HasKey(p => new {p.ServiceId, p.GrainType, p.StateType, p.GrainId}).IsClustered(false).HasName("PK_GrainState");
            c.Property(p => p.ServiceId).HasMaxLength(280).IsRequired();
            c.Property(p => p.GrainType).HasMaxLength(280).IsRequired();
            c.Property(p => p.StateType).HasMaxLength(280).IsRequired();
            c.Property(p => p.GrainId).HasMaxLength(280).IsRequired();
            c.Property(p => p.Data).IsRequired(false);
            c.Property(p => p.ETag).IsRequired().IsRowVersion();
        });
    }
}