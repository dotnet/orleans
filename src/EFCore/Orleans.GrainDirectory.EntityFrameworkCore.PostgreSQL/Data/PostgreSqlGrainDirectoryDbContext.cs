using Microsoft.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlGrainDirectoryDbContext : GuidGrainDirectoryDbContext<PostgreSqlGrainDirectoryDbContext>
{
    public PostgreSqlGrainDirectoryDbContext(DbContextOptions<PostgreSqlGrainDirectoryDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GrainActivationRecord<Guid>>(entity =>
        {
            entity.Property(record => record.ClusterIdHash).HasColumnType("bytea");
            entity.Property(record => record.GrainIdHash).HasColumnType("bytea");
            entity.Property(record => record.SiloAddressHash).HasColumnType("bytea");
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Activations_ClusterIdHash_Length", "octet_length(\"ClusterIdHash\") = 32");
                table.HasCheckConstraint("CK_Activations_GrainIdHash_Length", "octet_length(\"GrainIdHash\") = 32");
                table.HasCheckConstraint("CK_Activations_SiloAddressHash_Length", "octet_length(\"SiloAddressHash\") = 32");
            });
        });
    }
}
