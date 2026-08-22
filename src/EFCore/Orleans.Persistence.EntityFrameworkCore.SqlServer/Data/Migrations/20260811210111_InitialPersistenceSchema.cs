using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Persistence.EntityFrameworkCore.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistenceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GrainState",
                columns: table => new
                {
                    ServiceId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GrainType = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, collation: "Latin1_General_100_BIN2"),
                    StateType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GrainId = table.Column<string>(type: "nvarchar(299)", maxLength: 299, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ETag = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrainState", x => new { x.ServiceId, x.GrainType, x.StateType, x.GrainId })
                        .Annotation("SqlServer:Clustered", false);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrainState");
        }
    }
}
