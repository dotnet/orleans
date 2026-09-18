using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialGrainDirectorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activations",
                columns: table => new
                {
                    ClusterIdHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    GrainIdHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    SiloAddressHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ClusterId = table.Column<string>(type: "text", nullable: false),
                    GrainId = table.Column<string>(type: "text", nullable: false),
                    SiloAddress = table.Column<string>(type: "text", nullable: false),
                    ActivationId = table.Column<string>(type: "text", nullable: false),
                    MembershipVersion = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => new { x.ClusterIdHash, x.GrainIdHash });
                    table.CheckConstraint("CK_Activations_ClusterIdHash_Length", "octet_length(\"ClusterIdHash\") = 32");
                    table.CheckConstraint("CK_Activations_GrainIdHash_Length", "octet_length(\"GrainIdHash\") = 32");
                    table.CheckConstraint("CK_Activations_SiloAddressHash_Length", "octet_length(\"SiloAddressHash\") = 32");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_ClusterIdHash_SiloAddressHash",
                table: "Activations",
                columns: new[] { "ClusterIdHash", "SiloAddressHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activations");
        }
    }
}
