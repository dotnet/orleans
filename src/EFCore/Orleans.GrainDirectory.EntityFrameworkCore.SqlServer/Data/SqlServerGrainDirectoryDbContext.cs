using Microsoft.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;

public class SqlServerGrainDirectoryDbContext : GrainDirectoryDbContext<SqlServerGrainDirectoryDbContext, byte[]>
{
    private const string IdentifierCollation = "Latin1_General_100_BIN2";

    public SqlServerGrainDirectoryDbContext(DbContextOptions<SqlServerGrainDirectoryDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainActivationRecord<byte[]>>(c =>
        {
            c.HasKey(p => new {p.ClusterId, p.GrainId}).IsClustered(false).HasName("PK_Activations");
            c.Property(p => p.ClusterId).HasMaxLength(150).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.GrainId).HasMaxLength(512).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.SiloAddress).HasMaxLength(256).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.ActivationId).HasMaxLength(64).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.MembershipVersion).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c.HasIndex(p => new {p.ClusterId, p.SiloAddress}).IsClustered(false).HasDatabaseName("IDX_Activations_ClusterId_SiloAddress");
            c.HasIndex(p => new {p.ClusterId, p.GrainId, p.ActivationId}).IsClustered(false).HasDatabaseName("IDX_Activations_ClusterId_GrainId_ActivationId");
        });
    }
}