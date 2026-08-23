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
                    ClusterIdHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    GrainIdHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    SiloAddressHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    ClusterId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    GrainId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    SiloAddress = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActivationId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    MembershipVersion = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => new { x.ClusterIdHash, x.GrainIdHash })
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IDX_Activations_ClusterIdHash_SiloAddressHash",
                table: "Activations",
                columns: new[] { "ClusterIdHash", "SiloAddressHash" })
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
