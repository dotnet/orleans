using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data.Migrations
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
                    ServiceId = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    GrainType = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    StateType = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    GrainId = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrainState", x => new { x.ServiceId, x.GrainType, x.StateType, x.GrainId });
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
