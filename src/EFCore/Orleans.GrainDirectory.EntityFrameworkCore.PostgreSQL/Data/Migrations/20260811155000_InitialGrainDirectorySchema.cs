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
                    ClusterId = table.Column<string>(type: "text", nullable: false),
                    GrainId = table.Column<string>(type: "text", nullable: false),
                    SiloAddress = table.Column<string>(type: "text", nullable: false),
                    ActivationId = table.Column<string>(type: "text", nullable: false),
                    MembershipVersion = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => new { x.ClusterId, x.GrainId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_ClusterId_GrainId_ActivationId",
                table: "Activations",
                columns: new[] { "ClusterId", "GrainId", "ActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_ClusterId_SiloAddress",
                table: "Activations",
                columns: new[] { "ClusterId", "SiloAddress" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activations");
        }
    }
}
