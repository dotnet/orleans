using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Orleans.Persistence.EntityFrameworkCore.Data;

namespace Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;

public class SqlServerGrainStateDbContext : GrainStateDbContext<SqlServerGrainStateDbContext, byte[]>
{
    private const string IdentifierCollation = "Latin1_General_100_BIN2";

    public SqlServerGrainStateDbContext(DbContextOptions<SqlServerGrainStateDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainStateRecord<byte[]>>(c =>
        {
            c.HasKey(p => p.KeyHash).IsClustered(false).HasName("PK_GrainState");
            c.Property(p => p.KeyHash)
                .HasColumnType("binary(32)")
                .IsRequired()
                .UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.ServiceId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.GrainType).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.StateType).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.GrainId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.Data).IsRequired(false);
            c.Property(p => p.ETag).IsRequired().IsRowVersion();
        });
    }
}
