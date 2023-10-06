using Microsoft.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;

public class SqlServerGrainDirectoryDbContext : GrainDirectoryDbContext<SqlServerGrainDirectoryDbContext, byte[]>
{
    public SqlServerGrainDirectoryDbContext(DbContextOptions<SqlServerGrainDirectoryDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainActivationRecord<byte[]>>(c =>
        {
            c.HasKey(p => new {p.ClusterId, p.GrainId}).IsClustered(false).HasName("PK_Activations");
            c.Property(p => p.ClusterId).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.SiloAddress).IsRequired();
            c.Property(p => p.ActivationId).IsRequired();
            c.Property(p => p.MembershipVersion).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c.HasIndex(p => new {p.ClusterId, p.SiloAddress}).IsClustered(false).HasDatabaseName("IDX_Activations_CusterId_SiloAddress");
            c.HasIndex(p => new {p.ClusterId, p.GrainId, p.ActivationId}).IsClustered(false).HasDatabaseName("IDX_Activations_ClusterId_GrainId_ActivationId");
        });
    }
}