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
                    ServiceId = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: false),
                    GrainType = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: false),
                    StateType = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: false),
                    GrainId = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
