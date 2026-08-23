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
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GrainActivationRecord<byte[]>>(c =>
        {
            c.HasKey(p => new {p.ClusterIdHash, p.GrainIdHash}).IsClustered(false).HasName("PK_Activations");
            c.Property(p => p.ClusterIdHash).HasColumnType("binary(32)");
            c.Property(p => p.GrainIdHash).HasColumnType("binary(32)");
            c.Property(p => p.SiloAddressHash).HasColumnType("binary(32)");
            c.Property(p => p.ClusterId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.GrainId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.SiloAddress).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.ActivationId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c.HasIndex(p => new {p.ClusterIdHash, p.SiloAddressHash}).IsClustered(false).HasDatabaseName("IDX_Activations_ClusterIdHash_SiloAddressHash");
        });
    }
}