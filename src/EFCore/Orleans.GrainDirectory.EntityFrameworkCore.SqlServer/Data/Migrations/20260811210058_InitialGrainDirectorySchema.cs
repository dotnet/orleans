using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data.Migrations
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
                    ClusterId = table.Column<string>(type: "nvarchar(450)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    GrainId = table.Column<string>(type: "nvarchar(450)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    SiloAddress = table.Column<string>(type: "nvarchar(450)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActivationId = table.Column<string>(type: "nvarchar(450)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    MembershipVersion = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => new { x.ClusterId, x.GrainId })
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IDX_Activations_ClusterId_GrainId_ActivationId",
                table: "Activations",
                columns: new[] { "ClusterId", "GrainId", "ActivationId" })
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IDX_Activations_ClusterId_SiloAddress",
                table: "Activations",
                columns: new[] { "ClusterId", "SiloAddress" })
                .Annotation("SqlServer:Clustered", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activations");
        }
    }
}
